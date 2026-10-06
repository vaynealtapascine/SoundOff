using System.Diagnostics;

namespace SoundOff.Desktop;

// The installers put a setup kit beside the app: uv, scripts/setup_runtime.py and its pinned requirements, and a script that
// runs them. The app opens that script in a terminal window so the download's progress is visible and it can be run again
// to repair. A build from source has no kit, and says to run scripts/setup_runtime.py instead.
internal static class TranscriptionSetup
{
    public static string? Script
    {
        get
        {
            var script = Path.Combine(AppContext.BaseDirectory, "setup", OperatingSystem.IsWindows() ? "setup-transcription.cmd" : "setup-transcription.sh");
            return File.Exists(script) ? script : null;
        }
    }

    // False when no terminal could be opened; the caller then shows the script's path to run by hand.
    public static bool Launch(string script)
    {
        if (OperatingSystem.IsWindows()) return TryStart(new ProcessStartInfo(script) { UseShellExecute = true });
        if (OperatingSystem.IsMacOS()) return TryStart(Command("open", "-a", "Terminal", script));
        // Linux has no single terminal; these are the Debian alternative and the common desktops' own, in that order.
        return TryStart(Command("x-terminal-emulator", "-e", script)) || TryStart(Command("gnome-terminal", "--", script))
            || TryStart(Command("konsole", "-e", script)) || TryStart(Command("xfce4-terminal", "-x", script)) || TryStart(Command("xterm", "-e", script));
    }

    private static ProcessStartInfo Command(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static bool TryStart(ProcessStartInfo info)
    {
        try { using var process = Process.Start(info); return process is not null; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
