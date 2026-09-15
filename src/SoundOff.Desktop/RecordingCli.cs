using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SoundOff.Core;

namespace SoundOff.Desktop;

internal static class RecordingCli
{
    internal const string Usage = "--record devices | --record <seconds> <new.wav> [system|microphone|combined] [--device <id>] | combined: [--mic <id>] [--render <id>]. Per-app capture is unsupported; system audio includes all apps on the selected render endpoint. No echo cancellation.";
    internal static async Task<int> RunAsync(string[] args, Func<ICaptureEngine>? create = null, CancellationToken cancellation = default)
    {
        create ??= CaptureEngines.Create;
        if (args is ["devices"])
        {
            using var devices = create();
            Console.WriteLine(JsonSerializer.Serialize(new { microphones = devices.Devices(CaptureMode.Microphone),
                renderEndpoints = devices.Devices(CaptureMode.SystemAudio), combined = devices is ICombinedCaptureEngine, perApp = false }));
            return 0;
        }
        if (args.Length < 2 || !double.TryParse(args[0], CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds <= 0 || seconds > 3600)
        { Console.Error.WriteLine(Usage); return 2; }
        var mode = args.Length < 3 ? "system" : args[2];
        if (mode is not ("system" or "microphone" or "combined")) { Console.Error.WriteLine(Usage); return 2; }
        var flags = new Dictionary<string, string>();
        for (var i = 3; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal) ||
                (mode == "combined" ? args[i] is not ("--mic" or "--render") : args[i] != "--device") || !flags.TryAdd(args[i], args[i + 1]))
            { Console.Error.WriteLine(Usage); return 2; }
        }
        using var capture = create();
        if (mode == "combined" && capture is not ICombinedCaptureEngine)
        { Console.Error.WriteLine("Combined capture is unavailable on this platform/adapter. No single-source fallback was attempted."); return 1; }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            cancel.Token.ThrowIfCancellationRequested();
            Console.Error.WriteLine($"Recording {mode} for {seconds:0.###}s. {Usage}");
            if (mode == "combined") ((ICombinedCaptureEngine)capture).StartCombined(flags.GetValueOrDefault("--mic"), flags.GetValueOrDefault("--render"), args[1]);
            else capture.Start(mode == "microphone" ? CaptureMode.Microphone : CaptureMode.SystemAudio, flags.GetValueOrDefault("--device"), args[1]);
            var elapsed = Stopwatch.StartNew();
            try
            {
                while (elapsed.Elapsed.TotalSeconds < seconds && capture.State is RecordingState.Recording or RecordingState.Paused)
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(50, Math.Max(1, (seconds - elapsed.Elapsed.TotalSeconds) * 1000))), cancel.Token);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            // Ctrl+C stops both sources and keeps a finalized partial-duration take, instead of killing the writer.
            var recorded = capture.Stop();
            var probe = await MediaTools.ProbeAsync(recorded.Path, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(new { status = recorded.Interrupted ? "interrupted" : cancel.IsCancellationRequested ? "cancelled-kept" : "completed",
                path = recorded.Path, mode = recorded.Mode.ToString(), device = recorded.DeviceName, recordedSeconds = recorded.DurationMicroseconds / 1_000_000.0,
                probedSeconds = probe.DurationSeconds, bytes = new FileInfo(recorded.Path).Length, format = probe.FormatName,
                codec = probe.Streams.FirstOrDefault()?.CodecName, sampleRate = probe.Streams.FirstOrDefault()?.SampleRate, channels = probe.Streams.FirstOrDefault()?.Channels,
                gaps = recorded.Gaps, interrupted = recorded.Interrupted, reason = recorded.InterruptionReason, recoveryDirectory = recorded.RecoveryDirectory }));
            return recorded.Interrupted || recorded.DurationMicroseconds <= 0 ? 1 : cancel.IsCancellationRequested ? 130 : 0;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
