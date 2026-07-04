using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace InstantCompress;

/// <summary>
/// Selectable compression preset.
/// </summary>
public enum Preset { Low, Medium, High }

/// <summary>
/// Encoder settings for a preset: JPEG quality (0-100) and PNG zlib level (0-9).
/// </summary>
public readonly record struct PresetSettings(int JpgQuality, int PngLevel);

/// <summary>
/// UI-free compression engine — no dispatcher, no Avalonia types — so <see cref="SelfCheck"/> runs it headless.
/// </summary>
public static class Compressor
{
    /// <summary>
    /// Encoder settings for each <see cref="Preset"/>.
    /// </summary>
    public static readonly Dictionary<Preset, PresetSettings> Presets = new()
    {
        [Preset.Low] = new PresetSettings(60, 6),
        [Preset.Medium] = new PresetSettings(75, 8),
        [Preset.High] = new PresetSettings(90, 9),
    };

    /// <summary>
    /// The one supported-input whitelist used everywhere (drop, folders, file picker). No TIFF: Skia ships no codec.
    /// </summary>
    public static readonly string[] SupportedExts = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"];

    /// <summary>Whether <paramref name="path"/> has a supported image extension.</summary>
    public static bool IsSupported(string path) =>
        SupportedExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Expands any folders (recursively) and keeps only supported images, deduped by full path, input order preserved.
    /// </summary>
    public static List<string> Gather(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        void Add(string f) { if (IsSupported(f) && seen.Add(Path.GetFullPath(f))) result.Add(f); }
        foreach (string p in paths)
        {
            if (Directory.Exists(p))
                foreach (string f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)) Add(f);
            else if (File.Exists(p)) Add(p);
        }
        return result;
    }

    /// <summary>
    /// Batch progress snapshot passed to the progress callback.
    /// </summary>
    public readonly record struct Progress(int Done, int Total, string CurrentFile, long BytesDone, long BytesTotal);

    /// <summary>
    /// Compresses <paramref name="files"/> into a new timestamped folder next to the first input.
    /// </summary>
    /// <remarks>
    /// Callbacks fire on worker threads — the caller marshals to its UI thread. Throws on whole-batch
    /// failure (e.g. output-dir creation); per-file failures go to <paramref name="onError"/>.
    /// </remarks>
    /// <returns>The directory, returned even when cancelled (partial output is kept).</returns>
    public static string CompressBatch(IReadOnlyList<string> files, string format, PresetSettings preset,
                                       Action<Progress> onProgress, Action<string> onError,
                                       CancellationToken ct)
    {
        string baseDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(files[0]))!,
                                  $"compressed_{DateTime.Now:yyyyMMdd_HHmmss}");
        // Two runs in the same second would otherwise reuse one folder and mix/overwrite outputs.
        // ponytail: tiny TOCTOU window between Exists and Create — single-user desktop app, not worth a lock.
        string outDir = baseDir;
        for (var n = 1; Directory.Exists(outDir); n++) outDir = $"{baseDir}_{n}";
        Directory.CreateDirectory(outDir); // throws: aborts whole batch

        long[] sizes = files.Select(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }).ToArray();
        long bytesTotal = sizes.Sum();

        // Dedupe same-stem inputs (a.jpg + a.png -> a.jpg, a_1.jpg) up front: deriving the name
        // per-worker let one file truncate/delete another's finished output.
        var outPaths = new string[files.Count];
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count; i++)
        {
            string stem = Path.GetFileNameWithoutExtension(files[i]);
            var name = $"{stem}.{format}";
            for (var n = 1; !taken.Add(name); n++) name = $"{stem}_{n}.{format}";
            outPaths[i] = Path.Combine(outDir, name);
        }

        var done = 0;
        long bytesDone = 0;
        var po = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxWorkers(),
            CancellationToken = ct,
        };
        try
        {
            // NoBuffering: the default partitioner hands out growing chunks, so one worker can sit on
            // several large files at the batch tail while other cores idle. One index per grab.
            Parallel.ForEach(Partitioner.Create(Enumerable.Range(0, files.Count),
                                                EnumerablePartitionerOptions.NoBuffering), po, i =>
            {
                try { CompressOne(files[i], outPaths[i], format, preset, ct); }
                catch (OperationCanceledException) { throw; }                // stop the loop
                catch (Exception e) { onError($"{files[i]}: {e.Message}"); } // per-file: collect, continue
                onProgress(new Progress(Interlocked.Increment(ref done), files.Count,
                                        Path.GetFileName(files[i]),
                                        Interlocked.Add(ref bytesDone, sizes[i]), bytesTotal));
            });
        }
        catch (OperationCanceledException) { }                           // cancelled: partial output stays
        catch (AggregateException) when (ct.IsCancellationRequested) { } // worker OCEs, same thing
        return outDir;
    }

    // A decoded bitmap + native encode buffers for one worker: 50MP photos decode to ~200-400MB.
    // ponytail: one fixed number for every image size, not measured per-file — raise if this proves
    // too conservative on small-image batches.
    private const long BytesPerWorker = 400L * 1024 * 1024;

    /// <summary>
    /// Worker cap: as many cores as available RAM can back without paging.
    /// </summary>
    /// <returns>Between 1 and <see cref="Environment.ProcessorCount"/>.</returns>
    private static int MaxWorkers()
    {
        int cores = Environment.ProcessorCount;
        long avail = AvailablePhysicalMemoryBytes();
        if (avail <= 0) return cores; // memory info unreadable: cap by core count only
        return (int)Math.Clamp(avail / BytesPerWorker, 1, cores);
    }

    /// <summary>
    /// Available physical memory, or (on macOS / read failure) total memory as a best-effort fallback.
    /// </summary>
    private static long AvailablePhysicalMemoryBytes()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return AvailableMemoryWindows();
            if (OperatingSystem.IsLinux()) return AvailableMemoryLinux();
        }
        catch { /* fall through */ }
        // No free-RAM API on macOS (or the read above failed) without a native dependency, so fall
        // back to *total* memory. Overestimates if other apps are using RAM — best effort until measured.
        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    }

    /// <summary>
    /// Win32 <c>MEMORYSTATUSEX</c> for <see cref="GlobalMemoryStatusEx"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    /// <summary>
    /// Physical RAM free right now (Windows), excluding the page file.
    /// </summary>
    private static long AvailableMemoryWindows()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new InvalidOperationException("GlobalMemoryStatusEx failed");
        return (long)status.ullAvailPhys;
    }

    /// <summary>
    /// Available RAM from <c>/proc/meminfo</c> (Linux).
    /// </summary>
    private static long AvailableMemoryLinux()
    {
        // MemAvailable (not MemFree) already accounts for reclaimable cache, matching what the kernel
        // would actually hand a new allocation before it needs to swap.
        foreach (string line in File.ReadLines("/proc/meminfo"))
        {
            if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
            return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024; // kB
        }
        throw new InvalidOperationException("MemAvailable not found in /proc/meminfo");
    }

    /// <summary>
    /// Decodes an image and applies its EXIF orientation, so pixels land in display order before the
    /// re-encode drops the metadata. Without this, phone photos with a non-TopLeft orientation flag come
    /// out rotated. Returns null if the file can't be decoded.
    /// </summary>
    private static SKBitmap? DecodeOriented(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec == null) return null;
        var raw = SKBitmap.Decode(codec);
        if (raw == null) return null;
        if (codec.EncodedOrigin == SKEncodedOrigin.TopLeft) return raw;
        using (raw) return ApplyOrigin(raw, codec.EncodedOrigin);
    }

    /// <summary>
    /// Draws <paramref name="src"/> through the transform that maps its stored pixels to display order.
    /// Origins 5-8 (LeftTop..LeftBottom) transpose the axes, so the output swaps width and height.
    /// </summary>
    internal static SKBitmap ApplyOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        bool swap = origin >= SKEncodedOrigin.LeftTop; // 5..8 rotate/transpose -> swapped dimensions
        var dst = new SKBitmap(swap ? src.Height : src.Width, swap ? src.Width : src.Height,
                               src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.SetMatrix(OriginMatrix(origin, src.Width, src.Height));
        canvas.DrawBitmap(src, 0, 0);
        canvas.Flush();
        return dst;
    }

    // Skia's SkEncodedOriginToMatrix: (w,h) are the source dimensions; the translation terms place the
    // rotated/flipped image back into the positive quadrant.
    private static SKMatrix OriginMatrix(SKEncodedOrigin o, int w, int h) => o switch
    {
        SKEncodedOrigin.TopRight    => M(-1, 0, w, 0, 1, 0),  // mirror horizontal
        SKEncodedOrigin.BottomRight => M(-1, 0, w, 0, -1, h), // rotate 180
        SKEncodedOrigin.BottomLeft  => M(1, 0, 0, 0, -1, h),  // mirror vertical
        SKEncodedOrigin.LeftTop     => M(0, 1, 0, 1, 0, 0),   // transpose
        SKEncodedOrigin.RightTop    => M(0, -1, h, 1, 0, 0),  // rotate 90 CW
        SKEncodedOrigin.RightBottom => M(0, -1, h, -1, 0, w), // transverse
        SKEncodedOrigin.LeftBottom  => M(0, 1, 0, -1, 0, w),  // rotate 90 CCW
        _                           => SKMatrix.CreateIdentity(),
    };

    private static SKMatrix M(float sx, float kx, float tx, float ky, float sy, float ty) =>
        new() { ScaleX = sx, SkewX = kx, TransX = tx, SkewY = ky, ScaleY = sy, TransY = ty, Persp2 = 1 };

    /// <summary>
    /// Decodes one image and re-encodes it to <paramref name="outPath"/>; deletes the output on error or cancel.
    /// </summary>
    private static void CompressOne(string path, string outPath, string format, PresetSettings preset, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var bmp = DecodeOriented(path)
            ?? throw new Exception("Could not decode (unsupported or corrupt)"); // returns null, never throws
        ct.ThrowIfCancellationRequested();
        try
        {
            // 64KB buffer: Skia pushes many small blocks; File.Create's 4KB default = syscall storm.
            using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            using var ws = new SKManagedWStream(fs);
            using var pix = bmp.PeekPixels();
            // ponytail: cancel is checked between stages only; Skia's native encode isn't safely abortable
            // mid-stream — worst-case cancel latency is one file's encode.
            bool ok = format == "jpg"
                // BlendOnBlack flattens alpha inside the native encoder (JPEG has no alpha).
                ? pix.Encode(ws, new SKJpegEncoderOptions(preset.JpgQuality,
                      SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.BlendOnBlack))
                // SKPngEncoderOptions is the only Skia PNG path honoring a zlib level;
                // SKBitmap.Encode(Png, quality) ignores quality entirely.
                : pix.Encode(ws, new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, preset.PngLevel));
            if (!ok) throw new Exception("Encode failed");
            ct.ThrowIfCancellationRequested(); // cancelled during encode: deleted below
        }
        catch
        {
            try { File.Delete(outPath); } catch { } // never leave half-written output
            throw;
        }
    }
}
