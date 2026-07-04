using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;

namespace InstantCompress;

/// <summary>
/// Single-window UI and batch orchestration: drag &amp; drop, progress/ETA, cancel. Plain code-behind, no MVVM.
/// </summary>
public partial class MainWindow : Window
{
    private Preset _preset = Preset.Medium; // matches the Medium default checked in XAML
    private string _format = "jpg";
    private bool _busy, _cancelled;
    private CancellationTokenSource? _cts;
    private Task? _job;                // running batch, awaited by OnClosing so close never kills a mid-write worker
    private object? _latestProgress;   // boxed Compressor.Progress, last write wins; UI reads it on the 250ms timer
    private readonly Stopwatch _sw = new();
    private readonly List<string> _errors = [];
    private int _done, _total;
    private string? _lastOutDir;       // last batch's output folder, for the Open folder button
    private IReadOnlyList<Compressor.FileResult> _lastResults = [];
    private long _bytesDone, _bytesTotal;
    private readonly DispatcherTimer _etaTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly IBrush _normalBorder, _accentBorder;
    private readonly Settings _settings = SettingsStore.Load();
    private bool _loading;             // suppress persistence while applying loaded settings to the UI

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

        ApplyTheme(_settings.Theme);
        ApplySettingsToUi();
    }

    /// <summary>
    /// Applies the saved theme choice, or the OS theme when none is saved.
    /// </summary>
    private void ApplyTheme(string? theme)
    {
        Application.Current!.RequestedThemeVariant = theme switch
        {
            "dark" => ThemeVariant.Dark,
            "light" => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
        bool dark = Application.Current.ActualThemeVariant == ThemeVariant.Dark;
        ThemeToggle.IsChecked = dark;
        ThemeToggle.Content = dark ? "Light" : "Dark";
    }

    /// <summary>
    /// Flips between light and dark, overriding the OS theme, and persists the choice.
    /// </summary>
    private void OnThemeToggleClick(object? sender, RoutedEventArgs e)
    {
        _settings.Theme = ThemeToggle.IsChecked == true ? "dark" : "light";
        SettingsStore.Save(_settings);
        ApplyTheme(_settings.Theme);
    }

    /// <summary>
    /// Applies loaded settings to the controls without triggering a save-back.
    /// </summary>
    private void ApplySettingsToUi()
    {
        _loading = true;
        _preset = Enum.Parse<Preset>(_settings.Preset, ignoreCase: true);
        _format = _settings.Format;
        SelectInGroup(PresetGroup, _settings.CustomOn ? "custom" : _settings.Preset);
        SelectInGroup(FormatGroup, _settings.Format);
        SettingsButton.IsVisible = _settings.CustomOn;
        ResizeToggle.IsVisible = !_settings.CustomOn;
        SyncCustomRange();
        ResizeToggle.IsChecked = _settings.ResizeOn;
        ResizeValue.Value = _settings.MaxDim;
        _loading = false;
    }

    /// <summary>
    /// Checks the toggle whose Tag matches <paramref name="tag"/>, unchecks the rest.
    /// </summary>
    private static void SelectInGroup(StackPanel group, string tag)
    {
        foreach (var t in group.Children.OfType<ToggleButton>())
            t.IsChecked = (string)t.Tag! == tag;
    }

    /// <summary>
    /// Points the custom-value spinner at the range/value for the current format (JPG 1-100, PNG 0-9).
    /// </summary>
    private void SyncCustomRange()
    {
        bool prev = _loading; _loading = true; // range/value churn here must not persist
        // JPG and WebP are quality-based (1-100); only PNG uses a zlib level (0-9).
        if (_format == "png") { CustomValue.Minimum = 0; CustomValue.Maximum = 9; CustomValue.Value = _settings.CustomPng; }
        else { CustomValue.Minimum = 1; CustomValue.Maximum = 100; CustomValue.Value = _settings.CustomJpg; }
        _loading = prev;
    }

    /// <summary>
    /// Persists the current UI choices.
    /// </summary>
    private void Save()
    {
        if (_loading) return;
        _settings.Preset = _preset.ToString().ToLowerInvariant();
        _settings.Format = _format;
        SettingsStore.Save(_settings);
    }

    /// <summary>
    /// Stores the custom quality/level for the active format and persists it.
    /// </summary>
    private void OnCustomValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_loading) return;
        var v = (int)(CustomValue.Value ?? 0);
        if (_format == "png") _settings.CustomPng = v; else _settings.CustomJpg = v;
        Save();
    }

    /// <summary>
    /// Persists the resize toggle: caps the longest side to the active preset's (or Custom's) max size.
    /// </summary>
    private void OnResizeToggled(object? sender, RoutedEventArgs e)
    {
        _settings.ResizeOn = ResizeToggle.IsChecked == true;
        Save();
    }

    /// <summary>
    /// Stores the max-dimension cap and persists it.
    /// </summary>
    private void OnResizeValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.MaxDim = (int)(ResizeValue.Value ?? _settings.MaxDim);
        Save();
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
        if (group.Name == "PresetGroup")
        {
            _settings.CustomOn = value == "custom";
            if (!_settings.CustomOn) _preset = Enum.Parse<Preset>(value, ignoreCase: true);
            SettingsButton.IsVisible = _settings.CustomOn;
            ResizeToggle.IsVisible = !_settings.CustomOn;
        }
        else { _format = value; SyncCustomRange(); }
        Save();
    }

    /// <summary>
    /// Opens the custom quality/resize settings page.
    /// </summary>
    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        MainPage.IsVisible = false;
        SettingsPage.IsVisible = true;
        BackButton.IsVisible = true;
    }

    /// <summary>
    /// Returns from the settings page to the main page.
    /// </summary>
    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        SettingsPage.IsVisible = false;
        BackButton.IsVisible = false;
        MainPage.IsVisible = true;
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
        IReadOnlyList<IStorageFile> picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = "Choose images",
            FileTypeFilter = [new FilePickerFileType("Images")
                { Patterns = [.. Compressor.SupportedTypes.Select(x => "*" + x)] }],
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

        // captured at drop: custom quality/size override the preset when enabled
        (string fmt, PresetSettings preset) = (_format, _settings.CustomOn
            ? new PresetSettings(_settings.CustomJpg, _settings.CustomJpg, _settings.CustomPng, _settings.MaxDim)
            : Presets.Values[_preset]);
        int maxDim = _settings.ResizeOn ? preset.MaxDim : 0;
        string? outDir = null;
        try
        {
            Task<Compressor.BatchResult> job = Task.Run(() => Compressor.CompressBatch(files, fmt, preset, maxDim,
                p => Volatile.Write(ref _latestProgress, p), // hot path: no dispatcher, timer picks it up
                err => Dispatcher.UIThread.Post(() => AppendError(err)),
                _cts.Token));
            _job = job;
            Compressor.BatchResult result = await job;
            outDir = result.OutDir;
            _lastResults = result.Files;
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
    /// Opens a scrollable per-file results list (original/compressed size, ratio; failed/skipped flagged).
    /// </summary>
    private void OnViewResultsClick(object? sender, RoutedEventArgs e)
    {
        new Window
        {
            Title = "Results",
            Width = 560,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                Padding = new Thickness(16),
                Content = new SelectableTextBlock
                {
                    Text = BuildResultsText(),
                    FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                    FontSize = 12,
                },
            },
        }.Show(this);
    }

    /// <summary>
    /// Formats the last batch's per-file results as an aligned monospace table.
    /// </summary>
    private string BuildResultsText()
    {
        if (_lastResults.Count == 0) return "No results.";
        int nameW = Math.Min(40, _lastResults.Max(r => Path.GetFileName(r.Input).Length));
        IEnumerable<string> lines = _lastResults.Select(r =>
        {
            string name = Path.GetFileName(r.Input).PadRight(nameW);
            return r.Status switch
            {
                Compressor.FileStatus.Ok =>
                    $"{name}  {HumanBytes(r.OriginalBytes),9} → {HumanBytes(r.CompressedBytes),9}  ({Ratio(r)})",
                Compressor.FileStatus.Failed => $"{name}  failed: {r.Error}",
                _ => r.Error != null ? $"{name}  skipped: {r.Error}" : $"{name}  skipped",
            };
        });
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Compressed-to-original ratio as a percentage, or "—" when the original size is unknown.
    /// </summary>
    private static string Ratio(Compressor.FileResult r) =>
        r.OriginalBytes > 0 ? $"{100.0 * r.CompressedBytes / r.OriginalBytes:0}%" : "—";

    /// <summary>
    /// Human-readable byte size (B/KB/MB/GB).
    /// </summary>
    private static string HumanBytes(long b)
    {
        string[] u = ["B", "KB", "MB", "GB"];
        double v = b; var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.#} {u[i]}";
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
