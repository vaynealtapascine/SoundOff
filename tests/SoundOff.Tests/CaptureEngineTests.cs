using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

public sealed class CaptureEngineTests
{
    [Fact] public void Disk_space_and_writability_are_checked_before_a_recording_starts()
    {
        using var folder = new TestDirectory();
        var target = Path.Combine(folder.Root, "nested", "take.wav");
        RecordingRules.RequireWritableSpace(target);          // creates the directory and proves it is writable
        Assert.True(Directory.Exists(Path.GetDirectoryName(target)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!));   // the probe file is always removed
        Assert.NotNull(RecordingRules.TryFreeBytes(folder.Root));
        Assert.Null(RecordingRules.TryFreeBytes("\0invalid"));
        Assert.Equal(88_200, RecordingRules.BytesPerSecond(44_100, 1, 16));
        Assert.Equal("about 1 h 0 min", RecordingRules.DescribeCapacity(88_200L * 3600, 88_200));
        Assert.Equal("about 10 min", RecordingRules.DescribeCapacity(88_200L * 600, 88_200));
        Assert.Equal("unknown", RecordingRules.DescribeCapacity(1000, 0));
    }

    [Fact] public void Capture_devices_are_enumerated_for_both_modes()
    {
        using var engine = CaptureEngines.Create();
        if (engine is UnavailableCaptureEngine) return;
        var outputs = engine.Devices(CaptureMode.SystemAudio);
        Assert.All(outputs, d => { Assert.NotEmpty(d.Name); Assert.NotEmpty(d.Id); Assert.Equal(CaptureMode.SystemAudio, d.Mode); });
        // Microphones may legitimately be absent on this machine; enumeration must still not throw.
        Assert.All(engine.Devices(CaptureMode.Microphone), d => Assert.Equal(CaptureMode.Microphone, d.Mode));
    }

    // Loopback capture needs no microphone and no permission prompt, so this exercises the real Windows audio stack.
    [Fact] public async Task Recording_whole_computer_audio_writes_a_valid_growing_wave_file()
    {
        using var folder = new TestDirectory();
        using var engine = CaptureEngines.Create();
        if (engine is UnavailableCaptureEngine || engine.Devices(CaptureMode.SystemAudio).Count == 0) return;
        var target = Path.Combine(folder.Root, "system.wav");
        try { engine.Start(CaptureMode.SystemAudio, null, target); }
        catch (IOException) { return; }   // no usable render endpoint on this machine
        Assert.Equal(RecordingState.Recording, engine.State);
        await Task.Delay(900);
        var midway = engine.RecordedMicroseconds;
        Assert.True(midway > 0, "the recording clock did not advance");
        // The file is valid and playable while still recording, not only after a clean stop.
        Assert.True(new FileInfo(target).Length > 44);
        var probe = await MediaTools.ProbeAsync(target, CancellationToken.None);
        Assert.True(probe.HasAudio); Assert.True(probe.DurationSeconds > 0);

        engine.Pause();
        Assert.Equal(RecordingState.Paused, engine.State);
        var paused = engine.RecordedMicroseconds;
        await Task.Delay(400);
        Assert.Equal(paused, engine.RecordedMicroseconds);   // paused time is excluded, not recorded as silence
        engine.Resume();
        await Task.Delay(400);
        Assert.True(engine.RecordedMicroseconds > paused);

        var result = engine.Stop();
        Assert.Equal(RecordingState.Completed, engine.State);
        Assert.Equal(target, result.Path);
        Assert.Equal(CaptureMode.SystemAudio, result.Mode);
        Assert.NotEmpty(result.DeviceName);
        Assert.False(result.Interrupted);
        var gap = Assert.Single(result.Gaps);                // the pause left a visible marker
        Assert.InRange(gap.AtMicroseconds, paused - 50_000, paused + 50_000);
        Assert.EndsWith("Z", gap.ResumedUtc);
        Assert.True(result.DurationMicroseconds > midway);
        var final = await MediaTools.ProbeAsync(target, CancellationToken.None);
        Assert.InRange(final.DurationSeconds * 1_000_000, result.DurationMicroseconds * 0.85, result.DurationMicroseconds * 1.15);
        Assert.Throws<InvalidOperationException>(() => engine.Stop());
    }

    [Fact] public void Starting_twice_is_refused_and_an_unusable_destination_fails_before_any_device_is_opened()
    {
        using var folder = new TestDirectory();
        using var engine = CaptureEngines.Create();
        if (engine is UnavailableCaptureEngine) return;
        var blocked = Path.Combine(folder.Root, "blocked.wav");
        Directory.CreateDirectory(blocked);   // a directory where the file should go
        Assert.ThrowsAny<Exception>(() => engine.Start(CaptureMode.SystemAudio, null, blocked));
        Assert.True(engine.State is RecordingState.Failed or RecordingState.Idle);
        if (engine.Devices(CaptureMode.SystemAudio).Count == 0) return;
        var target = Path.Combine(folder.Root, "take.wav");
        try { engine.Start(CaptureMode.SystemAudio, null, target); }
        catch (IOException) { return; }
        try
        {
            Assert.Throws<InvalidOperationException>(() => engine.Start(CaptureMode.SystemAudio, null, Path.Combine(folder.Root, "second.wav")));
            Assert.False(File.Exists(Path.Combine(folder.Root, "second.wav")));
        }
        finally { engine.Stop(); }
    }

    [Fact] public void An_unknown_device_id_is_refused_by_name()
    {
        using var folder = new TestDirectory();
        using var engine = CaptureEngines.Create();
        if (engine is UnavailableCaptureEngine) return;
        var error = Assert.ThrowsAny<Exception>(() => engine.Start(CaptureMode.SystemAudio, "not-a-real-device-id", Path.Combine(folder.Root, "x.wav")));
        Assert.Contains("no longer available", error.Message);
        Assert.Equal(RecordingState.Failed, engine.State);
    }
}
