using Avalonia;

namespace InstantCompress;

/// <summary>
/// Process entry point: routes to the headless self-check or launches the GUI.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Runs <c>--selfcheck</c> or starts the Avalonia desktop app.
    /// </summary>
    /// <returns>Process exit code; 0 on normal GUI exit.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run before any Avalonia init. Exit code is the contract — WinExe stdout may be invisible on Windows.
        if (args.Contains("--selfcheck")) return SelfCheck.Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>
    /// Configures the Avalonia app builder.
    /// </summary>
    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
