using Microsoft.Data.Sqlite;
using SoundOff.Core;
using Xunit;

namespace SoundOff.Tests;

public sealed class MediaTests
{
    private static string Clip => Path.Combine(AppContext.BaseDirectory, "fixtures", "tts-english.wav");

    [Fact] public void Probe_json_is_parsed_strictly_enough_to_reject_silent_or_broken_files()
    {
        var probe = MediaTools.Parse("{\"streams\":[{\"codec_type\":\"audio\",\"codec_name\":\"pcm_s16le\",\"sample_rate\":\"16000\",\"channels\":1}],\"format\":{\"format_name\":\"wav\",\"duration\":\"9.335000\"}}");
        Assert.Equal("wav", probe.FormatName); Assert.Equal(9.335, probe.DurationSeconds); Assert.True(probe.HasAudio); Assert.False(probe.HasVideo);
        Assert.Throws<InvalidDataException>(() => MediaTools.Parse("{\"streams\":[{\"codec_type\":\"video\"}],\"format\":{\"format_name\":\"png\",\"duration\":\"0.04\"}}"));
        Assert.Throws<InvalidDataException>(() => MediaTools.Parse("{\"streams\":[{\"codec_type\":\"audio\"}],\"format\":{\"format_name\":\"wav\"}}"));
        Assert.Throws<InvalidDataException>(() => MediaTools.Parse("{\"streams\":[],\"format\":{\"format_name\":\"wav\",\"duration\":\"1\"}}"));
    }

    [Fact] public async Task Real_ffprobe_reads_the_tts_clip_and_refuses_a_text_file()
    {
        var probe = await MediaTools.ProbeAsync(Clip, CancellationToken.None);
        Assert.InRange(probe.DurationSeconds, 9.0, 9.6); Assert.Equal("pcm_s16le", probe.Streams[0].CodecName); Assert.Equal(1, probe.Streams[0].Channels);
        using var folder = new TestDirectory(); var text = Path.Combine(folder.Root, "not-media.wav"); File.WriteAllText(text, "just text");
        await Assert.ThrowsAsync<InvalidDataException>(() => MediaTools.ProbeAsync(text, CancellationToken.None));
    }

    [Fact] public async Task Import_copies_media_beside_the_project_with_a_verified_digest_and_leaves_the_original_untouched()
    {
        using var folder = new TestDirectory(); var originalBytes = File.ReadAllBytes(Clip);
        using var store = ProjectStore.Create(folder.Project);
        Assert.Equal(Path.Combine(folder.Root, "Test project.soundoff.media"), store.MediaDirectory);
        var asset = await MediaImport.ImportAsync(store, Clip, CancellationToken.None);
        Assert.Equal("tts-english.wav", asset.OriginalName); Assert.Equal(originalBytes.Length, asset.Bytes); Assert.Equal(64, asset.Sha256.Length);
        Assert.Equal(Path.Combine("media", asset.Sha256[..16] + ".wav"), asset.RelativePath); Assert.InRange(asset.DurationMicroseconds!.Value, 9_000_000, 9_600_000);
        var copy = Path.Combine(store.MediaDirectory, asset.RelativePath);
        Assert.Equal(originalBytes, File.ReadAllBytes(copy)); Assert.Equal(originalBytes, File.ReadAllBytes(Clip));
        Assert.Contains("pcm_s16le", asset.ProbeJson);
        var listed = Assert.Single(store.MediaAssets()); Assert.Equal(asset, listed);
        var again = await MediaImport.ImportAsync(store, Clip, CancellationToken.None); // identical bytes: one owned copy, two asset rows
        Assert.Equal(asset.RelativePath, again.RelativePath); Assert.NotEqual(asset.Id, again.Id); Assert.Equal(2, store.MediaAssets().Count);
        Assert.Single(Directory.GetFiles(Path.Combine(store.MediaDirectory, "media")));
        var text = Path.Combine(folder.Root, "bogus.mp3"); File.WriteAllText(text, "not audio");
        await Assert.ThrowsAsync<InvalidDataException>(() => MediaImport.ImportAsync(store, text, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(Path.Combine(store.MediaDirectory, "media"), ".*.importing")); Assert.Equal(2, store.MediaAssets().Count);
        await Assert.ThrowsAsync<FileNotFoundException>(() => MediaImport.ImportAsync(store, Path.Combine(folder.Root, "missing.wav"), CancellationToken.None));
    }

    [Fact] public void Runs_are_recorded_finished_once_and_their_result_imported_as_a_new_revision()
    {
        using var folder = new TestDirectory(); using var store = ProjectStore.Create(folder.Project);
        var asset = new MediaAsset("a1", "x.wav", "media/x.wav", new string('a', 64), 10, 9_000_000, "{}", "2026-09-14T12:00:00Z"); store.AddMediaAsset(asset);
        var run = new ProcessingRun("r1", "a1", "2026-09-14T12:00:01Z", null, "running", "{\"model\":\"small\"}", null, null, null, null); store.AddRun(run);
        Assert.Equal(run, Assert.Single(store.Runs()));
        store.FinishRun("r1", "completed", "runs/r1.json", new string('b', 64), null, "whisperx 3.8.6");
        var finished = Assert.Single(store.Runs()); Assert.Equal("completed", finished.Status); Assert.NotNull(finished.FinishedUtc); Assert.Equal("runs/r1.json", finished.ArtifactRelativePath);
        Assert.Throws<InvalidOperationException>(() => store.FinishRun("r1", "failed", null, null, "twice", null));
        Assert.Throws<SqliteException>(() => store.AddRun(run with { Id = "r2", AssetId = "no-such-asset" }));
        var proposal = SyntheticFixture.Create(Guid.NewGuid(), 0) with { Provenance = Provenance.Model("whisperx 3.8.6") };
        var imported = store.ImportInference(0, proposal, "r1");
        Assert.Equal(1, imported.Revision); Assert.Equal(store.Read().ProjectId, imported.ProjectId); Assert.True(imported.Provenance.IsModel); Assert.Equal(3, imported.Blocks.Length);
        Assert.Equal("import-inference:r1", store.History(1)[0].Operation);
        Assert.Throws<InvalidDataException>(() => store.ImportInference(1, SyntheticFixture.Create(Guid.NewGuid(), 0), "r1"));
        Assert.Empty(store.Undo(1).Blocks); Assert.Equal(3, store.Redo(2).Blocks.Length);
    }

    [Fact] public void Schema_two_projects_upgrade_to_three_with_a_backup_and_schema_one_upgrades_through_both_steps()
    {
        using var folder = new TestDirectory();
        using (var store = ProjectStore.Create(folder.Project)) { var doc = store.Read(); store.LoadFixture(0, SyntheticFixture.Create(doc.ProjectId, 0)); }
        using (var sql = new SqliteConnection($"Data Source={folder.Project};Pooling=False"))
        { sql.Open(); using var command = sql.CreateCommand(); command.CommandText = "DROP TABLE processing_runs; DROP TABLE media_assets; PRAGMA user_version=2;"; command.ExecuteNonQuery(); }
        using (var store = ProjectStore.Open(folder.Project))
        {
            Assert.NotNull(store.MigrationBackupPath); Assert.Contains(".schema2-", store.MigrationBackupPath);
            Assert.Equal(2L, StorageTests.UserVersion(store.MigrationBackupPath!)); Assert.Empty(store.MediaAssets()); Assert.Empty(store.Runs()); Assert.True(store.CanUndo);
        }
        Assert.Equal(3L, StorageTests.UserVersion(folder.Project));
        StorageTests.DowngradeToSchemaOne(folder.Project);
        using (var store = ProjectStore.Open(folder.Project)) { Assert.Contains(".schema1-", store.MigrationBackupPath); Assert.False(store.CanRedo); Assert.Empty(store.Runs()); }
        Assert.Equal(3L, StorageTests.UserVersion(folder.Project));
    }
}
