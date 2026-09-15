using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NAudio.Wave;
using SoundOff.Core;
using SoundOff.Desktop;
using SoundOff.Protocol;
using Xunit;

namespace SoundOff.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class CurrentReviewTests
{
    private static string Clip => Path.Combine(AppContext.BaseDirectory, "fixtures", "tts-english.wav");
    private sealed class Input : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public bool Disposed { get; private set; }
        public bool FailStart { get; set; }
        public bool Joined { get; private set; }
        public void StartRecording() { if (FailStart) throw new UnauthorizedAccessException("permission denied"); }
        public void StopRecording() { }
        public void Emit() => DataAvailable?.Invoke(this, new WaveInEventArgs(new byte[3200], 3200));
        public void EmitInvalidBuffer() => DataAvailable?.Invoke(this, new WaveInEventArgs(new byte[1], 3200));
        public void Interrupt() => RecordingStopped?.Invoke(this, new StoppedEventArgs(null));
        public Action? BeforeDispose { get; set; }
        public void Dispose() { BeforeDispose?.Invoke(); Disposed = true; }
        public void JoinCallback(Action callback)
        {
            var producer = new Thread(() => callback()); producer.IsBackground = true; producer.Start();
            Joined = producer.Join(TimeSpan.FromSeconds(2));
        }
    }

    [Fact] public void Capture_refuses_overwrite_and_keeps_permission_failure_recoverable()
    {
        using var folder = new TestDirectory(); var target = Path.Combine(folder.Root, "take.wav");
        var input = new Input { FailStart = true }; var opened = 0;
        using var engine = new NAudioCaptureEngine((_, _) => { opened++; return (input, "test", null); });
        File.WriteAllText(target, "original");
        Assert.Throws<IOException>(() => engine.Start(CaptureMode.Microphone, null, target));
        Assert.Equal(0, opened); Assert.Equal("original", File.ReadAllText(target));
        File.Delete(target);
        Assert.Contains("permission", Assert.Throws<IOException>(() => engine.Start(CaptureMode.Microphone, null, target)).Message);
        Assert.Equal(RecordingState.Failed, engine.State); Assert.True(input.Disposed);
        using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        input = new Input();
        engine.Start(CaptureMode.Microphone, null, Path.Combine(folder.Root, "retry.wav")); input.Emit();
        Assert.True(engine.Stop().DurationMicroseconds > 0);
    }

    [Fact] public void Unexpected_clean_stop_is_an_interruption_and_finalization_does_not_lock_out_callbacks()
    {
        using var folder = new TestDirectory(); var input = new Input();
        using var engine = new NAudioCaptureEngine((_, _) => (input, "test", null));
        var target = Path.Combine(folder.Root, "take.wav");
        engine.Start(CaptureMode.SystemAudio, null, target); input.Emit(); input.Interrupt();
        Assert.Equal(RecordingState.Interrupted, engine.State);
        Assert.Throws<InvalidOperationException>(() => engine.Start(CaptureMode.SystemAudio, null, Path.Combine(folder.Root, "lost.wav")));
        input.BeforeDispose = () => input.JoinCallback(() => _ = engine.RecordedMicroseconds);
        var result = engine.Stop();
        Assert.True(input.Joined, "Capture disposal held the lock while joining a producer callback.");
        Assert.True(result.Interrupted); Assert.NotNull(result.InterruptionReason);
        Assert.Equal(result.DurationMicroseconds, engine.RecordedMicroseconds);
        using var wave = new WaveFileReader(target); Assert.True(wave.Length > 0);
    }

    [Fact] public void A_failed_capture_write_retains_previous_samples_and_reports_an_interruption()
    {
        using var folder = new TestDirectory(); var input = new Input();
        using var engine = new NAudioCaptureEngine((_, _) => (input, "test", null));
        var target = Path.Combine(folder.Root, "retained.wav");
        engine.Start(CaptureMode.Microphone, null, target); input.Emit(); var recorded = engine.RecordedMicroseconds;
        input.EmitInvalidBuffer();
        Assert.Equal(RecordingState.Interrupted, engine.State); Assert.Contains("Writing the recording failed", engine.FailureReason);
        Assert.Equal(recorded, engine.RecordedMicroseconds);
        var result = engine.Stop(); Assert.True(result.Interrupted); Assert.Equal(recorded, result.DurationMicroseconds);
        using var wave = new WaveFileReader(target); Assert.True(wave.Length > 0);
    }

    [Fact] public void Level_meter_interprets_float_samples_as_float_not_sixteen_bit_pcm()
    {
        var samples = new[] { 0f, -0.25f, 0.5f }.SelectMany(BitConverter.GetBytes).ToArray();
        Assert.Equal(0.5, NAudioCaptureEngine.Peak(samples, samples.Length, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
        Assert.Equal(0.5, NAudioCaptureEngine.Peak(samples, samples.Length, new WaveFormatExtensible(48000, 32, 2)));
        Assert.Equal(0.5, NAudioCaptureEngine.Peak(BitConverter.GetBytes((short)-16384), 2, new WaveFormat(16000, 16, 1)));
    }

    [Theory] [InlineData("microphon")][InlineData("both")][InlineData("app")]
    public async Task Unknown_cli_capture_modes_never_start_whole_computer_capture(string mode)
    {
        using var folder = new TestDirectory(); var target = Path.Combine(folder.Root, "should-not-exist.wav");
        Assert.Equal(2, await RuntimeCli.RunAsync(["--record", "1", target, mode])); Assert.False(File.Exists(target));
        Assert.Equal(2, await RuntimeCli.RunAsync(["--record", "NaN", target, "microphone"])); Assert.False(File.Exists(target));
    }

    [Fact] public async Task Adoption_rejects_prefix_siblings_and_never_deletes_its_own_destination()
    {
        using var folder = new TestDirectory(); using var store = ProjectStore.Create(folder.Project);
        var sibling = store.MediaDirectory + "-not-owned"; Directory.CreateDirectory(sibling);
        var original = Path.Combine(sibling, "original.wav"); File.Copy(Clip, original);
        await Assert.ThrowsAsync<InvalidDataException>(() => MediaImport.AdoptAsync(store, original, "x.wav", CancellationToken.None));
        Assert.True(File.Exists(original));
        var imported = await MediaImport.ImportAsync(store, Clip, CancellationToken.None);
        var owned = Path.Combine(store.MediaDirectory, imported.RelativePath);
        var again = await MediaImport.AdoptAsync(store, owned, "again.wav", CancellationToken.None);
        Assert.Equal(imported.RelativePath, again.RelativePath); Assert.True(File.Exists(owned));
    }

    [Fact] public async Task Adoption_restores_take_after_database_failure_and_detects_corrupt_deduplication()
    {
        using var folder = new TestDirectory(); using var store = ProjectStore.Create(folder.Project);
        var recordings = Path.Combine(store.MediaDirectory, "recordings"); Directory.CreateDirectory(recordings);
        var take = Path.Combine(recordings, "take.wav"); File.Copy(Clip, take);
        // A real SQLite abort at the media insert, not a fake import implementation.
        using (var sql = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={folder.Project};Pooling=False"))
        {
            sql.Open(); using var cmd = sql.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER fail_asset BEFORE INSERT ON media_assets BEGIN SELECT RAISE(ABORT, 'test storage failure'); END"; cmd.ExecuteNonQuery();
        }
        await Assert.ThrowsAnyAsync<Exception>(() => MediaImport.AdoptAsync(store, take, "take.wav", CancellationToken.None));
        Assert.True(File.Exists(take)); Assert.Empty(store.MediaAssets());
        using (var sql = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={folder.Project};Pooling=False"))
        { sql.Open(); using var cmd = sql.CreateCommand(); cmd.CommandText = "DROP TRIGGER fail_asset"; cmd.ExecuteNonQuery(); }
        var saved = await MediaImport.AdoptAsync(store, take, "take.wav", CancellationToken.None);
        var destination = Path.Combine(store.MediaDirectory, saved.RelativePath);
        var bytes = File.ReadAllBytes(destination); bytes[^1] ^= 1; File.WriteAllBytes(destination, bytes);
        File.Copy(Clip, take);
        await Assert.ThrowsAsync<InvalidDataException>(() => MediaImport.AdoptAsync(store, take, "take.wav", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => MediaImport.ImportAsync(store, Clip, CancellationToken.None));
        Assert.True(File.Exists(take)); Assert.Single(store.MediaAssets());
    }

    [Theory] [InlineData(false)][InlineData(true)]
    public async Task Completed_worker_must_exit_and_remains_cancellable(bool cancel)
    {
        using var folder = new TestDirectory(); var runtime = InferenceRuntime.ForRoot(folder.Root);
        var client = new InferenceWorkerClient(() => ProtocolTests.Helper("inf-completed-hang"), runtime, TimeSpan.FromSeconds(2));
        using var cts = new CancellationTokenSource(); if (cancel) cts.CancelAfter(800);
        var target = Path.Combine(folder.Root, "result.json"); var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => client.TranscribeAsync(Clip, target, "", "small", "cpu", "en", false, null, null, cts.Token));
        Assert.True(cancel ? error is OperationCanceledException : error is TimeoutException, error.ToString());
        Assert.InRange(watch.Elapsed.TotalSeconds, 0, 10); Assert.False(File.Exists(target));
    }

    [Fact] public async Task Diagnostic_flood_is_drained_without_deadlocking_completion()
    {
        using var folder = new TestDirectory();
        var client = new InferenceWorkerClient(() => ProtocolTests.Helper("inf-stderr-flood"), InferenceRuntime.ForRoot(folder.Root), TimeSpan.FromSeconds(4));
        var completed = await client.TranscribeAsync(Clip, Path.Combine(folder.Root, "result.json"), "", "small", "cpu", "en", false, null, null, CancellationToken.None);
        Assert.Equal(2, completed.Artifact.Segments.Length);
    }

    [Fact] public void Out_of_order_same_speaker_segments_preserve_their_own_intervals_and_unicode()
    {
        var node = InferenceAdversary.Artifact("unused", "small", "cpu");
        node["segments"]![1]!["start"] = 0.1; node["segments"]![1]!["text"] = "José 👩🏽‍💻 e\u0301";
        var doc = InferenceImport.ToTranscript(DocumentJson.ReadStrict<InferenceArtifact>(node.ToJsonString(), 1 << 20), Guid.NewGuid(), 0, "t");
        Assert.Equal(2, doc.Blocks.Length); Assert.Equal(100000, doc.Blocks[1].Timing!.StartMicroseconds);
        Assert.Equal("José 👩🏽‍💻 e\u0301", DocumentJson.Deserialize(DocumentJson.Serialize(doc)).Blocks[1].Text);
    }

    [Theory] [InlineData(false)][InlineData(true)]
    public async Task Late_playback_load_cannot_repopulate_an_unloaded_or_disposed_engine(bool dispose)
    {
        using var folder = new TestDirectory(); var mp3 = Path.Combine(folder.Root, "clip.mp3");
        await MediaTools.DecodeToMp3ForTestAsync(Clip, mp3);
        using var engine = new NAudioPlaybackEngine(Path.Combine(folder.Root, "cache"));
        var load = engine.LoadAsync(mp3, CancellationToken.None);
        if (dispose) engine.Dispose(); else engine.Unload();
        await load;
        Assert.Equal(PlaybackStatus.Empty, engine.Status); Assert.Equal(0, engine.DurationMicroseconds);
        foreach (var file in Directory.GetFiles(folder.Root, "*.wav", SearchOption.AllDirectories))
            using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    private sealed class Picker(string project) : IProjectPicker
    {
        public string Project { get; set; } = project;
        public Task<string?> CreateProjectAsync() => Task.FromResult<string?>(Project);
        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(Project);
        public Task<string?> ExportTextAsync(bool isDraft) => Task.FromResult<string?>(null);
        public Task<string?> PickMediaAsync() => Task.FromResult<string?>(Clip);
    }
    private static Button Button(MainWindow w, string name) => w.FindControl<Button>(name)!;
    private static void Click(MainWindow w, string name) => UiDriver.Click(w, name);
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(10);
        Dispatcher.UIThread.RunJobs(); Assert.True(condition(), "UI did not reach the expected state.");
    }

    [AvaloniaFact] public async Task Interrupted_capture_is_not_replaceable_and_failed_adoption_can_be_retried()
    {
        using var folder = new TestDirectory(); var capture = new FakeCaptureEngine { SourceClip = Clip }; var playback = new FakePlaybackEngine();
        var w = new MainWindow(new Picker(folder.Project), folder.Settings, null, null, playback, capture); w.Show();
        try
        {
            await playback.LoadAsync(Clip, CancellationToken.None); playback.Play();
            w.FindControl<ComboBox>("CaptureModeChoice")!.SelectedIndex = 1;
            Click(w, "RecordButton"); await Until(() => Button(w, "StopRecordButton").IsEnabled);
            Assert.Equal(PlaybackStatus.Paused, playback.Status); Click(w, "PlayPauseButton"); Assert.Equal(PlaybackStatus.Paused, playback.Status);
            capture.Capture(1_000_000); capture.Interrupt("lost"); Dispatcher.UIThread.RunJobs();
            foreach (var name in new[] { "RecordButton", "OpenProjectItem", "DemoItem", "ImportBundleItem", "ImportMediaButton", "PreparePackButton", "TranscribeButton" }) Assert.False(UiDriver.Named(w, name).IsEnabled, name);
            Click(w, "OpenProjectItem"); Assert.Equal(RecordingState.Interrupted, capture.State);
            // A broken file is recoverable after ffprobe refuses it. No extra capture is started.
            File.WriteAllText(capture.Destination!, "broken");
            Click(w, "StopRecordButton"); await Until(() => Button(w, "StopRecordButton").IsEnabled);
            Assert.Contains("not added", w.FindControl<TextBlock>("RecordText")!.Text);
            Assert.False(Button(w, "RecordButton").IsEnabled); Assert.True(File.Exists(capture.Destination));
            w.Close(); var close = Assert.Single(w.OwnedWindows); close.Close(true);
            await Until(() => w.OwnedWindows.Count == 0 && Button(w, "StopRecordButton").IsEnabled);
            Assert.True(w.IsVisible); // failed save must cancel close
            File.Copy(Clip, capture.Destination!, true);
            Click(w, "StopRecordButton"); await Until(() => Button(w, "RecordButton").IsEnabled);
            Assert.StartsWith("Recorded ", w.FindControl<TextBlock>("RecordText")!.Text);
            Assert.Contains("Computer audio", w.FindControl<TextBlock>("MediaText")!.Text);
        }
        finally { foreach (var child in w.OwnedWindows.ToArray()) child.Close(false); w.Close(); }
        using var store = ProjectStore.Open(folder.Project); Assert.Single(store.MediaAssets());
    }

    [AvaloniaFact] public async Task Running_job_owns_its_project_and_cancelling_apply_retains_the_pending_result()
    {
        using var folder = new TestDirectory(); var picker = new Picker(folder.Project); var mode = "inf-delayed-ok";
        var runtime = new InferenceRuntime(typeof(CurrentReviewTests).Assembly.Location, typeof(CurrentReviewTests).Assembly.Location, Path.Combine(folder.Root, "models"), "");
        Directory.CreateDirectory(Path.Combine(runtime.ModelsDir, "packs")); File.WriteAllText(runtime.PackManifestPath("small"), "{}");
        var client = new InferenceWorkerClient(() => ProtocolTests.Helper(mode), runtime, TimeSpan.FromSeconds(10));
        var w = new MainWindow(picker, folder.Settings, null, client, new FakePlaybackEngine(), new FakeCaptureEngine()); w.Show();
        try
        {
            Click(w, "ImportMediaButton"); await Until(() => Button(w, "TranscribeButton").IsEnabled);
            Click(w, "TranscribeButton");
            Assert.False(UiDriver.Item(w, "OpenProjectItem").IsEnabled); Assert.False(UiDriver.Item(w, "DemoItem").IsEnabled);
            Assert.False(UiDriver.Item(w, "ImportBundleItem").IsEnabled); Assert.False(w.FindControl<StackPanel>("RecentHost")!.IsEnabled);
            Click(w, "OpenProjectItem"); // direct event routing must also refuse switching
            await Until(() => Button(w, "ImportMediaButton").IsEnabled);
            Assert.Contains(w.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("provenance")); // the result became the transcript
            // A job that began on a nonempty revision must not auto-apply after Undo creates a newer empty one.
            Click(w, "TranscribeButton"); Click(w, "UndoButton");
            await Until(() => Button(w, "ApplyResultButton").IsEnabled);
            Assert.DoesNotContain(w.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("provenance")); // undo reached the empty revision
            Click(w, "RedoButton");
            mode = "inf-ok"; Click(w, "TranscribeButton"); await Until(() => Button(w, "ApplyResultButton").IsEnabled);
            Click(w, "ApplyResultButton"); Assert.Single(w.OwnedWindows).Close(false);
            await Until(() => Button(w, "ApplyResultButton").IsEnabled);
            // Retain an unsaved Unicode correction while another result arrives.
            var text = w.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("transcript"));
            text.Text = "José 👩🏽‍💻 e\u0301 corrected";
            Click(w, "TranscribeButton"); await Until(() => Button(w, "ImportMediaButton").IsEnabled);
            Assert.Equal("José 👩🏽‍💻 e\u0301 corrected", text.Text); Assert.True(Button(w, "SaveButton").IsEnabled);
            Click(w, "SaveButton"); await Until(() => !Button(w, "SaveButton").IsEnabled);
            picker.Project = Path.Combine(folder.Root, "other.soundoff.sqlite"); using (ProjectStore.Create(picker.Project)) { }
            Click(w, "OpenProjectItem"); await Until(() => UiDriver.Item(w, "OpenProjectItem").IsEnabled);
            Assert.False(Button(w, "ApplyResultButton").IsEnabled); // no proposal leaks across projects
        }
        finally { foreach (var child in w.OwnedWindows.ToArray()) child.Close(false); w.Close(); }
        using var saved = ProjectStore.Open(folder.Project);
        Assert.Equal("José 👩🏽‍💻 e\u0301 corrected", saved.Read().Blocks[0].Text); Assert.Null(saved.Read().Blocks[0].Timing);
    }
}
