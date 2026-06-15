using Avalonia;

namespace Gomp.App;

internal static class Program
{
    // Avalonia's entry point. Kept slim per Avalonia convention — no app logic
    // here; everything wires up in App.OnFrameworkInitializationCompleted.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
