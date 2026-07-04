using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace InstantCompress;

/// <summary>
/// Single-window UI and batch orchestration: drag &amp; drop, progress/ETA, cancel. Plain code-behind, no MVVM.
/// </summary>
public partial class MainWindow : Window
{
    private Preset _preset = Preset.Low;
    private string _format = "jpg";
    // ponytail: single-job assumption (one window, one bool).
    private bool _busy, _cancelled;
    private CancellationTokenSource? _cts;
    private Task? _job;                // running batch, awaited by OnClosing so close never kills a mid-write worker
    private object? _latestProgress;   // boxed Compressor.Progress, last write wins; UI reads it on the 250ms timer
    private readonly Stopwatch _sw = new();
    private readonly List<string> _errors = [];
    private int _done, _total;
    private string? _lastOutDir;       // last batch's output folder, for the Open folder button
    private long _bytesDone, _bytesTotal;
    private readonly DispatcherTimer _etaTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly IBrush _normalBorder, _accentBorder;

    /// <summary>
    /// Wires up drop-zone handlers and the coalesced progress timer.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        _normalBorder = DropZone.BorderBrush!;
        _accentBorder = (IBrush)Resources["Accent"]!;
        // Coalesced progress: workers store only the latest snapshot (no Dispatcher.Post per file — a
        // 1000 files/s post storm starves the UI thread); this timer applies it ~4x/sec.
        _etaTimer.Tick += (_, _) =>
        {
            if (Volatile.Read(ref _latestProgress) is Compressor.Progress p) ApplyProgress(p);
            else RefreshCounter();
        };

        DropZone.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = !_busy && e.DataTransfer.Contains(DataFormat.File)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        DropZone.AddHandler(DragDrop.DragEnterEvent,
            (_, e) => SetDropHighlight(!_busy && e.DataTransfer.Contains(DataFormat.File)));
        DropZone.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropHighlight(false));
        DropZone.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>
    /// Toggles the drop-zone accent border.
    /// </summary>
    private void SetDropHighlight(bool on) => DropZone.BorderBrush = on ? _accentBorder : _normalBorder;

    /// <summary>
    /// Radio semantics over plain ToggleButtons: check the clicked button, uncheck its siblings.
    /// </summary>
    private void OnSegClick(object? sender, RoutedEventArgs e)
    {
        var btn = (ToggleButton)sender!;
        var group = (StackPanel)btn.Parent!;
        foreach (var t in group.Children.OfType<ToggleButton>())
            t.IsChecked = t == btn;
        var value = (string)btn.Tag!;
        if (group.Name == "PresetGroup") _preset = Enum.Parse<Preset>(value, ignoreCase: true);
        else _format = value;
    }

    /// <summary>
    /// Expands dropped files and folders to supported images and starts a job; notices if nothing is usable.
    /// </summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        if (_busy) return; // ignore drops while a job runs
        string[]? paths = e.DataTransfer.TryGetFiles()?          // items are files or folders
            .Select(f => f.TryGetLocalPath())
            .Where(p => p != null).Select(p => p!)
            .ToArray();
        if (paths is not { Length: > 0 }) return;                // non-file drop
        StartFrom(paths);
    }

    /// <summary>
    /// Opens a file picker (whitelist-filtered) as a discoverable alternative to drag &amp; drop.
    /// </summary>
    private async void OnChooseClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = "Choose images",
            FileTypeFilter = [new FilePickerFileType("Images")
                { Patterns = [.. Compressor.SupportedExts.Select(x => "*" + x)] }],
        });
        var paths = picked.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToArray();
        if (paths.Length > 0) StartFrom(paths);
    }

    /// <summary>
    /// Gathers supported images from arbitrary paths (files/folders) and starts a job, or shows a notice.
    /// </summary>
    private void StartFrom(IEnumerable<string> paths)
    {
        var images = Compressor.Gather(paths);
        if (images.Count == 0) { ShowNotice("No supported local images found."); return; }
        StartJob(images);
    }

    /// <summary>
    /// Shows a standalone message in the error panel (not a per-file error).
    /// </summary>
    private void ShowNotice(string msg)
    {
        _errors.Clear();
        DonePanel.IsVisible = false;
        ErrorsText.Text = msg;
        ErrorPanel.IsVisible = true;
    }

    /// <summary>
    /// Runs the batch off the UI thread and updates the busy/done panels.
    /// </summary>
    private async void StartJob(IReadOnlyList<string> files)
    {
        _busy = true;
        _cancelled = false;
        _cts = new CancellationTokenSource();
        _errors.Clear();
        ErrorPanel.IsVisible = false;
        DonePanel.IsVisible = false;
        _done = 0; _total = files.Count; _bytesDone = 0; _bytesTotal = 0;
        _latestProgress = null;
        Bar.Maximum = files.Count;
        Bar.Value = 0;
        CurrentFileText.Text = "";
        RefreshCounter();
        IdlePanel.IsVisible = false;
        BusyPanel.IsVisible = true;
        _sw.Restart();
        _etaTimer.Start();

        (string fmt, var preset) = (_format, Compressor.Presets[_preset]); // captured at drop
        string? outDir = null;
        try
        {
            var job = Task.Run(() => Compressor.CompressBatch(files, fmt, preset,
                p => Volatile.Write(ref _latestProgress, p), // hot path: no dispatcher, timer picks it up
                err => Dispatcher.UIThread.Post(() => AppendError(err)),
                _cts.Token));
            _job = job;
            outDir = await job;
        }
        catch (Exception ex)
        {
            AppendError(ex.Message); // whole-batch abort (e.g. output dir creation failed)
        }
        finally
        {
            _etaTimer.Stop();
            _sw.Stop();
            _cts.Dispose();
            _cts = null;
            _busy = false;
            BusyPanel.IsVisible = false;
            IdlePanel.IsVisible = true;
        }
        if (outDir != null)
        {
            _lastOutDir = outDir;
            DoneText.Text = _cancelled ? $"Cancelled — partial output in {outDir}" : $"Done — {outDir}";
            DonePanel.IsVisible = true;
        }
    }

    /// <summary>
    /// Opens the last output folder in the OS file manager.
    /// </summary>
    private void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_lastOutDir == null) return;
        try { Process.Start(new ProcessStartInfo(_lastOutDir) { UseShellExecute = true }); }
        catch (Exception ex) { ShowNotice("Could not open folder: " + ex.Message); }
    }

    /// <summary>
    /// Applies a progress snapshot to the bar and counter, ignoring stale (out-of-order) snapshots.
    /// </summary>
    private void ApplyProgress(Compressor.Progress p)
    {
        if (p.Done >= _done) // ignore stale snapshots so the bar never moves backwards
        {
            _done = p.Done; _total = p.Total; _bytesDone = p.BytesDone; _bytesTotal = p.BytesTotal;
            Bar.Value = p.Done;
            CurrentFileText.Text = p.CurrentFile;
        }
        RefreshCounter();
    }

    /// <summary>
    /// Updates the "n / total" counter with a byte-weighted ETA.
    /// </summary>
    private void RefreshCounter()
    {
        var eta = "";
        if (_bytesDone > 0 && _bytesTotal > 0)
        {
            double elapsed = _sw.Elapsed.TotalSeconds;
            var s = (int)Math.Round(Math.Max(0, elapsed / _bytesDone * _bytesTotal - elapsed));
            eta = s >= 60 ? $" · ~{s / 60}m {s % 60}s left" : $" · ~{s}s left";
        }
        CounterText.Text = $"{_done} / {_total}{eta}";
    }

    /// <summary>
    /// Appends a per-file error to the error panel.
    /// </summary>
    private void AppendError(string err)
    {
        _errors.Add(err);
        ErrorsText.Text = string.Join("\n", _errors);
        ErrorPanel.IsVisible = true;
    }

    /// <summary>
    /// Requests cancellation of the running batch.
    /// </summary>
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _cancelled = true;
        _cts?.Cancel();
    }

    /// <summary>
    /// On close mid-run: cancel and await the workers so they finish/clean up their partial outputs —
    /// otherwise process exit kills a thread inside a native encode, leaving a truncated file.
    /// </summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_job is not { IsCompleted: false } job) return;
        e.Cancel = true;
        _cancelled = true;
        _cts?.Cancel();
        try { await job; } catch { }
        Close(); // re-enters OnClosing; job is completed now, so the close goes through
    }
}
