using System.Diagnostics;
using System.Text.Json.Nodes;
using SoundOff.Core;
using SoundOff.Protocol;
using Xunit;

namespace SoundOff.Tests;

public sealed class InferenceTests
{
    private static InferenceRuntime FakeRuntime(TestDirectory folder) =>
        new(Path.Combine(folder.Root, "no-python.exe"), typeof(InferenceTests).Assembly.Location, Path.Combine(folder.Root, "models"), Path.Combine(folder.Root, "runtime.json"));
    private static InferenceWorkerClient Fake(TestDirectory folder, string mode, TimeSpan? liveness = null, TimeSpan? grace = null) =>
        new(() => ProtocolTests.Helper(mode), FakeRuntime(folder), liveness, grace);
    private static string Clip => Path.Combine(AppContext.BaseDirectory, "fixtures", "tts-english.wav");
    // Progress<T> posts through a synchronization context or the thread pool; tests need the callback right away.
    private sealed class SyncProgress(Action<InferenceProgress> handler) : IProgress<InferenceProgress> { public void Report(InferenceProgress value) => handler(value); }

    [Fact] public void Engine_segments_become_speaker_grouped_turns_with_microsecond_timing_and_word_evidence()
    {
        var node = InferenceAdversary.Artifact("C:/audio.wav", "small", "cpu");
        node["segments"]!.AsArray().Add(new JsonObject { ["start"] = 8.8, ["end"] = 9.3, ["text"] = "Reply.", ["speaker"] = "SPEAKER_01", ["words"] = new JsonArray() });
        node["segments"]!.AsArray().Add(new JsonObject { ["start"] = 9.3, ["end"] = 9.3, ["text"] = "Zero length.", ["speaker"] = "SPEAKER_01", ["words"] = new JsonArray() });
        node["segments"]!.AsArray().Add(new JsonObject { ["start"] = 9.4, ["end"] = 9.5, ["text"] = "   ", ["speaker"] = "SPEAKER_01", ["words"] = new JsonArray() });
        var artifact = DocumentJson.ReadStrict<InferenceArtifact>(node.ToJsonString(), 1 << 20);
        var id = Guid.NewGuid(); var document = InferenceImport.ToTranscript(artifact, id, 0, "Imported");
        Assert.Equal(id, document.ProjectId); Assert.True(document.Provenance.IsModel); Assert.Equal("whisperx 3.8.6", document.Provenance.Provider);
        Assert.Equal(["Speaker 1", "Speaker 2"], document.Speakers.Select(s => s.Name).ToArray());
        Assert.Equal(3, document.Blocks.Length);
        // Segments 1 and 2 share a speaker but the gap (3.9 -> 5.5) exceeds the 1.5 s paragraph threshold, so they stay separate turns.
        Assert.Equal("Hello. This is a synthetic English test clip.", document.Blocks[0].Text); Assert.Equal(new TimeRange(500_000, 3_900_000), document.Blocks[0].Timing);
        Assert.Equal(["Hello.", "This", "42"], document.Blocks[0].Words.Select(w => w.Text).ToArray());
        Assert.Equal(new TimeRange(500_000, 900_000), document.Blocks[0].Words[0].Timing); Assert.Equal(0.91, document.Blocks[0].Words[0].Score);
        Assert.Null(document.Blocks[0].Words[2].Timing);
        Assert.Equal(document.Speakers[0].Id, document.Blocks[1].SpeakerId); Assert.Equal(new TimeRange(5_500_000, 8_700_000), document.Blocks[1].Timing);
        // The zero-length segment merges into the second speaker's turn and poisons its timing rather than inventing an interval.
        Assert.Equal("Reply. Zero length.", document.Blocks[2].Text); Assert.Null(document.Blocks[2].Timing); Assert.Empty(document.Blocks[2].Words);
        Assert.Equal(document.Speakers[1].Id, document.Blocks[2].SpeakerId);
        var close = node.DeepClone().AsObject(); close["segments"]![1]!["start"] = 4.0; // within 1.5 s: same paragraph
        var merged = InferenceImport.ToTranscript(DocumentJson.ReadStrict<InferenceArtifact>(close.ToJsonString(), 1 << 20), id, 0, "t");
        Assert.StartsWith("Hello. This is a synthetic English test clip. The quick brown fox", merged.Blocks[0].Text);
        Assert.Equal(new TimeRange(500_000, 8_700_000), merged.Blocks[0].Timing); Assert.Equal(4, merged.Blocks[0].Words.Length);
        var unlabelled = node.DeepClone().AsObject(); foreach (var segment in unlabelled["segments"]!.AsArray()) segment!["speaker"] = null;
        var single = InferenceImport.ToTranscript(DocumentJson.ReadStrict<InferenceArtifact>(unlabelled.ToJsonString(), 1 << 20), id, 0, "t");
        Assert.Single(single.Speakers); Assert.Equal("Speaker", single.Speakers[0].Name);
        Assert.Equal(1_234_568, InferenceImport.Micro(1.2345678));
    }

    [Fact] public void Long_monologues_are_split_into_bounded_paragraphs()
    {
        var node = InferenceAdversary.Artifact("C:/audio.wav", "small", "cpu"); var segments = node["segments"]!.AsArray(); segments.Clear();
        for (var i = 0; i < 400; i++)
            segments.Add(new JsonObject { ["start"] = i * 1.0, ["end"] = i * 1.0 + 0.9, ["text"] = "Sentence number " + i + " of a long uninterrupted monologue.", ["speaker"] = "SPEAKER_00", ["words"] = new JsonArray() });
        var document = InferenceImport.ToTranscript(DocumentJson.ReadStrict<InferenceArtifact>(node.ToJsonString(), 1 << 22), Guid.NewGuid(), 0, "t");
        Assert.InRange(document.Blocks.Length, 8, 20); Assert.All(document.Blocks, b => Assert.True(b.Text.Length <= InferenceImport.ParagraphTargetLength));
        Assert.All(document.Blocks, b => Assert.NotNull(b.Timing)); Assert.Single(document.Speakers);
    }

    [Fact] public async Task Hello_and_prepare_are_validated_and_prepare_records_a_pack_manifest()
    {
        using var folder = new TestDirectory();
        var hello = await Fake(folder, "inf-hello").HelloAsync(false, CancellationToken.None);
        Assert.Equal("3.8.6", hello.ProviderVersion); Assert.Equal(["en", "tl"], hello.Languages); Assert.False(hello.Cuda);
        var client = Fake(folder, "inf-prepare-ok"); var progress = new List<InferenceProgress>();
        var manifest = await client.PrepareAsync("small", ["en", "tl"], false, null, Path.Combine(folder.Root, "prepare.log"), new SyncProgress(progress.Add), CancellationToken.None);
        Assert.Equal("small", manifest.Model); Assert.Equal(123456789, manifest.Bytes); Assert.True(client.Runtime.IsPackReady("small"));
        Assert.Contains("\"model\":\"small\"", File.ReadAllText(client.Runtime.PackManifestPath("small")));
        Assert.Contains(progress, p => p.Stage == "download-asr" && p.Fraction == 0.1);
        Assert.False(client.Runtime.IsPackReady("large-v3"));
    }

    [Fact] public async Task Valid_completion_is_verified_against_the_artifact_and_converted()
    {
        using var folder = new TestDirectory(); var output = Path.Combine(folder.Root, "run.json"); var progress = new List<InferenceProgress>();
        var completion = await Fake(folder, "inf-ok").TranscribeAsync(Clip, output, Path.Combine(folder.Root, "run.log"), "small", "cpu", "en", false, null,
            new SyncProgress(progress.Add), CancellationToken.None);
        Assert.Equal(2, completion.Artifact.Segments.Length); Assert.Equal(Path.GetFullPath(output), completion.ArtifactPath); Assert.True(File.Exists(output));
        Assert.Equal("whisperx 3.8.6", completion.Artifact.ProviderLabel);
        Assert.Contains(progress, p => p.Stage == "load-model"); Assert.DoesNotContain(progress, p => p.Stage == "heartbeat");
        var document = InferenceImport.ToTranscript(completion.Artifact, Guid.NewGuid(), 0, "t"); Assert.Equal(2, document.Blocks.Length);
        await Assert.ThrowsAsync<IOException>(() => Fake(folder, "inf-ok").TranscribeAsync(Clip, output, Path.Combine(folder.Root, "run.log"), "small", "cpu", "en", false, null, null, CancellationToken.None));
    }

    [Theory]
    [InlineData("inf-wrong-job")][InlineData("inf-sequence-gap")][InlineData("inf-failed")][InlineData("inf-missing-artifact")]
    [InlineData("inf-bad-sha")][InlineData("inf-bad-schema")][InlineData("inf-wrong-audio")][InlineData("inf-extra-after-completed")]
    [InlineData("inf-wrong-digest")][InlineData("inf-outside-audio")][InlineData("inf-word-outside-segment")]
    public async Task Invalid_worker_behaviour_is_refused_and_leaves_no_artifact(string mode)
    {
        using var folder = new TestDirectory(); var output = Path.Combine(folder.Root, "run.json");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Fake(folder, mode).TranscribeAsync(Clip, output, Path.Combine(folder.Root, "run.log"), "small", "cpu", null, false, null, null, CancellationToken.None));
        if (mode == "inf-failed") Assert.Contains("model files are missing", error.Message);
        Assert.False(File.Exists(output));
    }

    [Fact] public async Task Silent_worker_times_out_and_cancellation_is_honoured_or_enforced()
    {
        using var folder = new TestDirectory(); var output = Path.Combine(folder.Root, "run.json");
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => Fake(folder, "inf-hang", liveness: TimeSpan.FromSeconds(2)).TranscribeAsync(Clip, output, Path.Combine(folder.Root, "run.log"), "small", "cpu", null, false, null, null, CancellationToken.None));
        Assert.InRange(watch.Elapsed.TotalSeconds, 1.5, 30);
        foreach (var mode in new[] { "inf-cancel-honoured", "inf-cancel-ignored" })
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            watch.Restart();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fake(folder, mode, liveness: TimeSpan.FromSeconds(30), grace: TimeSpan.FromSeconds(2))
                .TranscribeAsync(Clip, output, Path.Combine(folder.Root, "run.log"), "small", "cpu", null, false, null, null, cancel.Token));
            Assert.InRange(watch.Elapsed.TotalSeconds, 0.5, 20); Assert.False(File.Exists(output));
        }
    }

    [Fact] public async Task Missing_runtime_is_reported_before_any_process_starts()
    {
        using var folder = new TestDirectory();
        var client = new InferenceWorkerClient(FakeRuntime(folder));
        Assert.False(client.Runtime.IsInstalled); Assert.Contains("setup_runtime.py", client.Runtime.MissingReason);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.HelloAsync(false, CancellationToken.None));
        Assert.Contains("not installed", error.Message);
    }

    // Real inference when this machine has the private runtime and a prepared small pack; otherwise the honest failure path is asserted.
    [Fact] public async Task Real_whisperx_transcribes_the_tts_clip_when_the_runtime_and_pack_exist()
    {
        using var folder = new TestDirectory(); var runtime = InferenceRuntime.Default();
        var client = new InferenceWorkerClient(runtime, liveness: TimeSpan.FromMinutes(5));
        var output = Path.Combine(folder.Root, "real.json");
        if (!runtime.IsInstalled || !runtime.IsPackReady("small"))
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => client.TranscribeAsync(Clip, output, Path.Combine(folder.Root, "real.log"), "small", "cpu", "en", false, null, null, CancellationToken.None));
            Assert.True(error is InvalidOperationException or InvalidDataException, error.ToString()); Assert.False(File.Exists(output));
            AdapterEvidence.Write("inference", false, "Runtime or small pack unavailable; only the failure path was asserted.");
            return;
        }
        var stages = new List<string>();
        var completion = await client.TranscribeAsync(Clip, output, Path.Combine(folder.Root, "real.log"), "small", "cpu", "en", false, null,
            new SyncProgress(p => stages.Add(p.Stage)), CancellationToken.None, threads: 4);
        var document = InferenceImport.ToTranscript(completion.Artifact, Guid.NewGuid(), 0, "Real run");
        var text = string.Join(" ", document.Blocks.Select(b => b.Text));
        Assert.Contains("quick brown fox", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("en", completion.Artifact.Engine.Language); Assert.InRange(completion.Artifact.Audio.DurationSeconds, 9.0, 9.6);
        Assert.Contains(document.Blocks, b => b.Timing is not null && b.Words.Any(w => w.Timing is not null));
        Assert.All(document.Blocks.SelectMany(b => b.Words).Where(w => w.Timing is not null), w => Assert.True(w.Timing!.EndMicroseconds <= 9_600_000));
        Assert.True(document.Provenance.IsModel);
        Assert.Contains("transcribe", stages); Assert.Contains("align", stages);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "real-inference-evidence.json"), File.ReadAllText(output));
        AdapterEvidence.Write("inference", true, "Real WhisperX recognition and alignment on the repository's synthetic English TTS audio, not a corpus benchmark.", new { completion.Artifact.ProviderLabel, completion.Artifact.Engine, completion.Artifact.Timings });
    }
}
