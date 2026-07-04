using SkiaSharp;

namespace InstantCompress;

/// <summary>
/// Headless pipeline test: generates temp images, runs the production
/// <see cref="Compressor.CompressBatch"/>, and asserts outputs exist and are non-empty.
/// Exit code is the contract — WinExe stdout is invisible on Windows unless redirected.
/// </summary>
public static class SelfCheck
{
    /// <summary>
    /// Runs the self-check.
    /// </summary>
    /// <returns>0 on success, 1 on failure.</returns>
    public static int Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "instantcompress_selfcheck_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);

            using var bmp = new SKBitmap(1024, 1024, SKColorType.Bgra8888, SKAlphaType.Opaque);
            for (var y = 0; y < 1024; y++)
                for (var x = 0; x < 1024; x++)
                    bmp.SetPixel(x, y, new SKColor((byte)(x % 256), (byte)(y % 256), (byte)(x * y % 256)));

            // grad_png.png + grad_png.webp share a stem: exercises output-name dedupe (grad_png_1.*)
            string pngIn = Path.Combine(dir, "grad_png.png");
            string jpgIn = Path.Combine(dir, "grad_jpg.jpg");
            string webpIn = Path.Combine(dir, "grad_png.webp");
            Save(bmp, pngIn, SKEncodedImageFormat.Png);
            Save(bmp, jpgIn, SKEncodedImageFormat.Jpeg);
            Save(bmp, webpIn, SKEncodedImageFormat.Webp);

            string[] inputs = new[] { pngIn, jpgIn, webpIn };
            foreach (string format in new[] { "jpg", "png" })
            {
                var errors = new List<string>();
                string outDir = Compressor.CompressBatch(inputs, format, Compressor.Presets[Preset.Medium],
                    _ => { }, e => { lock (errors) errors.Add(e); }, CancellationToken.None);
                if (errors.Count > 0) return Fail("errors: " + string.Join("; ", errors));
                foreach (string name in new[] { $"grad_png.{format}", $"grad_jpg.{format}", $"grad_png_1.{format}" })
                {
                    string outPath = Path.Combine(outDir, name);
                    if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0)
                        return Fail("missing or empty output: " + outPath);
                }
            }
            if (!OrientationOk()) return Fail("EXIF orientation transform wrong");
            if (!GatherOk(dir, jpgIn)) return Fail("Gather did not expand/filter correctly");
            Console.WriteLine("selfcheck: OK");
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex.ToString());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Verifies <see cref="Compressor.ApplyOrigin"/> against a hand-worked case: a 2x2 image rotated 90° CW
    /// (RightTop) must move the bottom-left pixel to top-left and the top-left pixel to top-right.
    /// </summary>
    private static bool OrientationOk()
    {
        var a = new SKColor(10, 0, 0); var b = new SKColor(20, 0, 0);
        var c = new SKColor(30, 0, 0); var d = new SKColor(40, 0, 0);
        using var src = new SKBitmap(2, 2, SKColorType.Bgra8888, SKAlphaType.Opaque);
        src.SetPixel(0, 0, a); src.SetPixel(1, 0, b);
        src.SetPixel(0, 1, c); src.SetPixel(1, 1, d);
        using var rot = Compressor.ApplyOrigin(src, SKEncodedOrigin.RightTop);
        return rot.Width == 2 && rot.Height == 2
            && rot.GetPixel(0, 0) == c && rot.GetPixel(1, 0) == a;
    }

    /// <summary>
    /// Verifies <see cref="Compressor.Gather"/> recurses folders, applies the whitelist, and dedupes:
    /// a subfolder image and an unsupported file are handled, and a path passed twice appears once.
    /// </summary>
    private static bool GatherOk(string dir, string existingImage)
    {
        string sub = Path.Combine(dir, "sub");
        Directory.CreateDirectory(sub);
        string nested = Path.Combine(sub, "nested.png");
        File.Copy(existingImage, nested, overwrite: true);
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "x"); // unsupported: must be skipped

        var got = Compressor.Gather(new[] { dir, existingImage }); // dir recursion + a dupe of existingImage
        return got.Contains(nested)                       // found in subfolder
            && got.Contains(existingImage)                // whitelisted file kept
            && got.Count(p => Path.GetFullPath(p) == Path.GetFullPath(existingImage)) == 1 // deduped
            && got.All(Compressor.IsSupported);           // nothing unsupported slipped through
    }

    /// <summary>
    /// Encodes a test bitmap to disk in the given format.
    /// </summary>
    private static void Save(SKBitmap bmp, string path, SKEncodedImageFormat fmt)
    {
        using var data = bmp.Encode(fmt, 90) ?? throw new Exception("test image encode failed: " + path);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    /// <summary>
    /// Writes a failure message to stderr.
    /// </summary>
    /// <returns>Always 1.</returns>
    private static int Fail(string msg)
    {
        Console.Error.WriteLine("selfcheck: FAIL — " + msg);
        return 1;
    }
}
