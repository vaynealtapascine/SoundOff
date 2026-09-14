using System.Text.Json.Serialization;

namespace SoundOff.Core;

// What the user chose to capture. Per-app capture is deliberately absent rather than silently widened: Windows
// process-loopback needs an API this build does not use, so the app offers the whole computer and says so.
public enum CaptureMode { Microphone, SystemAudio }

// Idle -> Ready -> Recording <-> Paused -> Stopping -> Completed, with Failed and Interrupted as terminal problems.
// Interrupted means capture or writing failed: retain the file for finalization/probing; it is not guaranteed usable.
public enum RecordingState { Idle, Ready, Recording, Paused, Stopping, Completed, Failed, Interrupted }

public sealed record CaptureDevice(string Id, string Name, CaptureMode Mode);

// A pause excludes its own duration. These markers are returned for the current session only;
// they are not persisted in the project schema or embedded in the WAV.
public sealed record CaptureGap([property: JsonRequired] long AtMicroseconds, [property: JsonRequired] string ResumedUtc);

public sealed record RecordingResult(string Path, long DurationMicroseconds, CaptureMode Mode, string DeviceName,
    IReadOnlyList<CaptureGap> Gaps, bool Interrupted, string? InterruptionReason);

public interface ICaptureEngine : IDisposable
{
    RecordingState State { get; }
    string? FailureReason { get; }
    // Monotonic and derived from bytes actually written, so it never counts paused time or wall-clock changes.
    long RecordedMicroseconds { get; }
    double PeakLevel { get; }
    IReadOnlyList<CaptureDevice> Devices(CaptureMode mode);
    void Start(CaptureMode mode, string? deviceId, string destinationPath);
    void Pause();
    void Resume();
    RecordingResult Stop();
    event EventHandler? Changed;
}

public static class RecordingRules
{
    // Refuse to start without room for a sensible recording rather than filling the disk and failing mid-sentence.
    public const long MinimumFreeBytes = 512L * 1024 * 1024;
    // 16-bit mono at 44.1 kHz is about 5 MB per minute; the real rate is device-dependent and measured as it records.
    public const long WarnBelowBytes = 2L * 1024 * 1024 * 1024;

    public static void RequireWritableSpace(string destinationPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        Directory.CreateDirectory(directory);
        var free = TryFreeBytes(directory);
        if (free is { } bytes && bytes < MinimumFreeBytes)
            throw new IOException($"Only {bytes / 1_048_576} MiB is free where the recording would be written; at least {MinimumFreeBytes / 1_048_576} MiB is required.");
        // Prove the location is actually writable now, not at the first buffer.
        var probe = Path.Combine(directory, "." + Guid.NewGuid().ToString("N")[..8] + ".probe");
        try { File.WriteAllBytes(probe, [0]); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }

    public static long? TryFreeBytes(string directory)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    public static long BytesPerSecond(int sampleRate, int channels, int bitsPerSample) => (long)sampleRate * channels * bitsPerSample / 8;

    public static string DescribeCapacity(long freeBytes, long bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "unknown";
        var seconds = freeBytes / bytesPerSecond;
        return seconds >= 3600 ? $"about {seconds / 3600} h {(seconds % 3600) / 60} min" : $"about {seconds / 60} min";
    }
}
