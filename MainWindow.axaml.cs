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
    // No TIFF: SkiaSharp/Skia ships no TIFF codec.
    private static readonly string[] Exts = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"];

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
    /// Filters a drop to supported local image files and starts a job.
    /// </summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        if (_busy) return; // ignore drops while a job runs
        string[]? files = e.DataTransfer.TryGetFiles()?
            .OfType<IStorageFile>() // dropped folders are skipped (no recursion)
            .Select(f => f.TryGetLocalPath())
            .Where(p => p != null && Exts.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .Select(p => p!)
            .ToArray();
        if (files is { Length: > 0 }) StartJob(files);
    }

    /// <summary>
    /// Runs the batch off the UI thread and updates the busy/done panels.
    /// </summary>
    private async void StartJob(string[] files)
    {
        _busy = true;
        _cancelled = false;
        _cts = new CancellationTokenSource();
        _errors.Clear();
        ErrorPanel.IsVisible = false;
        DonePanel.IsVisible = false;
        _done = 0; _total = files.Length; _bytesDone = 0; _bytesTotal = 0;
        _latestProgress = null;
        Bar.Maximum = files.Length;
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
            DoneText.Text = _cancelled ? $"Cancelled — partial output in {outDir}" : $"Done — {outDir}";
            DonePanel.IsVisible = true;
        }
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
