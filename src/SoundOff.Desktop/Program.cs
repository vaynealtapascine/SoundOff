using Avalonia;

namespace SoundOff.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test")
            return SmokeTest.RunAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] is "--runtime-status" or "--prepare-pack" or "--transcribe" or "--record")
            return RuntimeCli.RunAsync(args).GetAwaiter().GetResult();
        // A single project path (file association / drag onto the executable) is opened after the window shows.
        if (args.Length > 1 || (args.Length == 1 && !args[0].EndsWith(".soundoff.sqlite", StringComparison.OrdinalIgnoreCase)))
        { Console.Error.WriteLine("Usage: SoundOff.Desktop [project.soundoff.sqlite] | --self-test [--output directory] | --runtime-status | --prepare-pack <model> [languages] | --transcribe <audio> <output.json> [model] [device] [language] | --record <seconds> <output.wav> [system|microphone]"); return 2; }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
