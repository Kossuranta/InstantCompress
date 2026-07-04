using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace InstantCompress;

/// <summary>
/// Thrown when an input can't be decoded (unsupported format or corrupt file) — a skip, not a failure.
/// </summary>
public sealed class UnsupportedImageException(string message) : Exception(message);

/// <summary>
/// UI-free compression engine — no dispatcher, no Avalonia types — so <see cref="SelfCheck"/> runs it headless.
/// </summary>
public static partial class Compressor
{
    /// <summary>
    /// The one supported-input whitelist used everywhere (drop, folders, file picker). No TIFF: Skia ships no codec.
    /// </summary>
    public static readonly FrozenSet<string> SupportedTypes =
        new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="path"/> has a supported image extension.
    /// </summary>
    public static bool IsSupported(string path) =>
        SupportedTypes.Contains(Path.GetExtension(path));

    /// <summary>
    /// Hard cap on files a single <see cref="Gather"/> call collects, so a huge folder tree can't exhaust memory.
    /// </summary>
    public const int MaxGatherFiles = 1_000_000;

    /// <summary>
    /// Expands any folders (recursively) and keeps only supported images, deduped by full path, input order
    /// preserved, up to <paramref name="limit"/> files.
    /// </summary>
    public static List<string> Gather(IEnumerable<string> paths, int limit = MaxGatherFiles)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string p in paths)
        {
            if (result.Count >= limit) break;
            if (Directory.Exists(p))
            {
                foreach (string f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                {
                    Add(f);
                    if (result.Count >= limit) break;
                }
            }
            else if (File.Exists(p)) Add(p);
        }
        return result;

        void Add(string f)
        {
            if (IsSupported(f) && seen.Add(Path.GetFullPath(f)))
                result.Add(f);
        }
    }

    /// <summary>
    /// Batch progress snapshot passed to the progress callback.
    /// </summary>
    public readonly record struct Progress(int Done, int Total, string CurrentFile, long BytesDone, long BytesTotal);

    /// <summary>
    /// Outcome of one file. Skipped = never processed (batch was cancelled before reaching it).
    /// </summary>
    public enum FileStatus { Ok, Failed, Skipped }

    /// <summary>
    /// Per-file result for the results view.
    /// </summary>
    public readonly record struct FileResult(
        string Input, long OriginalBytes, long CompressedBytes, FileStatus Status, string? Error);

    /// <summary>
    /// What a batch produced: the output folder and one <see cref="FileResult"/> per input.
    /// </summary>
    public sealed record BatchResult(string OutDir, IReadOnlyList<FileResult> Files);

    /// <summary>
    /// Compresses <paramref name="files"/> into a new timestamped folder next to the first input.
    /// </summary>
    /// <remarks>
    /// Callbacks fire on worker threads — the caller marshals to its UI thread. Throws on whole-batch
    /// failure (e.g. output-dir creation); per-file failures go to <paramref name="onError"/>.
    /// </remarks>
    /// <returns>The output folder and per-file results, returned even when cancelled (partial output is kept).</returns>
    public static BatchResult CompressBatch(IReadOnlyList<string> files, string format, PresetSettings preset,
                                       int maxDim, Action<Progress> onProgress, Action<string> onError,
                                       CancellationToken ct)
    {
        string baseDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(files[0]))!,
                                  $"compressed_{DateTime.Now:yyyyMMdd_HHmmss}");
        // Two runs in the same second would otherwise reuse one folder and mix outputs. Tiny
        // TOCTOU window between Exists and Create is acceptable for a single-user desktop app.
        string outDir = baseDir;
        for (int n = 1; Directory.Exists(outDir); n++)
            outDir = $"{baseDir}_{n}";
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
            for (var n = 1; !taken.Add(name); n++)
                name = $"{stem}_{n}.{format}";
            outPaths[i] = Path.Combine(outDir, name);
        }

        // Pre-seed as Skipped: any index a worker never reaches (cancelled batch) keeps that status.
        var results = new FileResult[files.Count];
        for (var i = 0; i < files.Count; i++)
            results[i] = new FileResult(files[i], sizes[i], 0, FileStatus.Skipped, null);

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
                try
                {
                    long outBytes = CompressOne(files[i], outPaths[i], format, preset, maxDim, ct);
                    results[i] = new FileResult(files[i], sizes[i], outBytes, FileStatus.Ok, null);
                }
                catch (OperationCanceledException) { throw; }                // stop the loop (index stays Skipped)
                catch (UnsupportedImageException e)                          // undecodable: skip, not a failure
                {
                    results[i] = new FileResult(files[i], sizes[i], 0, FileStatus.Skipped, e.Message);
                }
                catch (Exception e)                                          // real per-file failure: record, continue
                {
                    onError($"{files[i]}: {e.Message}");
                    results[i] = new FileResult(files[i], sizes[i], 0, FileStatus.Failed, e.Message);
                }
                onProgress(new Progress(Interlocked.Increment(ref done), files.Count,
                                        Path.GetFileName(files[i]),
                                        Interlocked.Add(ref bytesDone, sizes[i]), bytesTotal));
            });
        }
        catch (OperationCanceledException) { }                           // cancelled: partial output stays
        catch (AggregateException) when (ct.IsCancellationRequested) { } // worker OCEs, same thing
        return new BatchResult(outDir, results);
    }

    // Fixed RAM budget per worker: a 50MP photo plus native encode buffers decode to ~200-400MB.
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

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
    /// Returns <paramref name="src"/> scaled so its longest side is <paramref name="maxDim"/>, aspect preserved.
    /// </summary>
    private static SKBitmap Downscale(SKBitmap src, int maxDim)
    {
        double scale = (double)maxDim / Math.Max(src.Width, src.Height);
        int w = Math.Max(1, (int)Math.Round(src.Width * scale));
        int h = Math.Max(1, (int)Math.Round(src.Height * scale));
        // SKFilterQuality.High = Lanczos-ish downsample; fine here, and it's the 2.88 API (pinned, see PLAN).
        return src.Resize(new SKImageInfo(w, h), SKFilterQuality.High) ?? throw new Exception("Resize failed");
    }

    /// <summary>
    /// Decodes one image (orientation applied), optionally downscales to <paramref name="maxDim"/> (0 = off),
    /// and re-encodes it to <paramref name="outPath"/>; deletes the output on error or cancel.
    /// </summary>
    /// <returns>Size of the written output file in bytes.</returns>
    private static long CompressOne(string path, string outPath, string format, PresetSettings preset, int maxDim, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var decoded = DecodeOriented(path)
            ?? throw new UnsupportedImageException("unsupported or corrupt"); // returns null, never throws
        ct.ThrowIfCancellationRequested();
        using var resized = maxDim > 0 && Math.Max(decoded.Width, decoded.Height) > maxDim
            ? Downscale(decoded, maxDim) : null;
        var bmp = resized ?? decoded;
        try
        {
            // 64KB buffer: Skia pushes many small blocks; File.Create's 4KB default = syscall storm.
            using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var ws = new SKManagedWStream(fs))
            using (var pix = bmp.PeekPixels())
            {
                // Native encode isn't safely abortable mid-stream, so cancel is checked between
                // stages only; worst-case cancel latency is one file's encode.
                bool ok = format switch
                {
                    // BlendOnBlack flattens alpha inside the native encoder (JPEG has no alpha).
                    "jpg" => pix.Encode(ws, new SKJpegEncoderOptions(preset.JpgQuality,
                                 SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.BlendOnBlack)),
                    // Lossy WebP; quality is on the same 0-100 scale as JPEG but tuned separately.
                    "webp" => pix.Encode(ws, new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, preset.WebpQuality)),
                    // SKPngEncoderOptions is the only Skia PNG path honoring a zlib level;
                    // SKBitmap.Encode(Png, quality) ignores quality entirely.
                    _ => pix.Encode(ws, new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, preset.PngLevel)),
                };
                if (!ok) throw new Exception("Encode failed");
                ct.ThrowIfCancellationRequested(); // cancelled during encode: deleted below
            }
            return new FileInfo(outPath).Length; // stream closed above, so the length is final
        }
        catch
        {
            try { File.Delete(outPath); } catch { } // never leave half-written output
            throw;
        }
    }
}
