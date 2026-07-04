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
