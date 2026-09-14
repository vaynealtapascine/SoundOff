using Avalonia;

namespace SoundOff.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test")
            return SmokeTest.RunAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult();
        if (args.Length != 0) { Console.Error.WriteLine("Usage: SoundOff.Desktop [--self-test [--output directory]]"); return 2; }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
