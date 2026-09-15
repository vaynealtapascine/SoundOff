using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

// Synthetic adversarial projects only. These tests never open the user's settings, media or model packs.
public sealed class BaselineAuditTests
{
    private static MediaAsset Asset(string path = "media/sample.wav") =>
        new("asset", "sample.wav", path, new string('a', 64), 10, 1_000_000, "{}", "2026-09-15T00:00:00Z");
    private static void Sql(string path, string statement)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = statement; command.ExecuteNonQuery();
    }

    [AvaloniaTheory]
    [InlineData("{\"version\":1,\"projects\":null}")]
    [InlineData("{\"version\":1,\"projects\":[null]}")]
    public void Null_recent_data_cannot_crash_dark_first_launch(string json)
    {
        using var folder = new TestDirectory(); var recent = folder.Settings.RecentProjects;
        Directory.CreateDirectory(Path.GetDirectoryName(recent.PathName)!); File.WriteAllText(recent.PathName, json);
        Assert.NotNull(recent.Load().Problem);
        var window = new MainWindow(null, folder.Settings, playbackEngine: new FakePlaybackEngine(), captureEngine: new FakeCaptureEngine());
        try
        {
            Assert.False(window.IsVisible); Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            window.Show();
            Assert.Contains(window.FindControl<StackPanel>("RecentHost")!.Children.OfType<TextBlock>(), t => t.Text!.Contains("was ignored"));
            Assert.False(File.Exists(folder.SettingsPath)); Assert.Equal(json, File.ReadAllText(recent.PathName));
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData("started_utc")][InlineData("finished_utc")][InlineData("options_json")]
    [InlineData("artifact_relative_path")][InlineData("artifact_sha256")][InlineData("error")][InlineData("provider")]
    public void Incomplete_run_schema_is_refused_on_open_before_ui_replacement(string column)
    {
        using var folder = new TestDirectory(); using (ProjectStore.Create(folder.Project)) { }
        Sql(folder.Project, "ALTER TABLE processing_runs DROP COLUMN " + column);
        var before = File.ReadAllBytes(folder.Project);
        Assert.Throws<SqliteException>(() => { using var rejected = ProjectStore.Open(folder.Project); });
        Assert.Equal(before, File.ReadAllBytes(folder.Project));
        using (File.Open(folder.Project + ".writer.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    [Theory]
    [InlineData("../outside.wav")][InlineData("media/../../outside.wav")][InlineData("media\\..\\..\\outside.wav")]
    [InlineData("C:/outside.wav")][InlineData("/outside.wav")][InlineData("media/file.wav:stream")]
    public void Unsafe_persisted_asset_paths_fail_before_media_is_opened(string path)
    {
        using var folder = new TestDirectory();
        using (var store = ProjectStore.Create(folder.Project)) store.AddMediaAsset(Asset());
        using (var connection = new SqliteConnection($"Data Source={folder.Project};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE media_assets SET relative_path=$path"; command.Parameters.AddWithValue("$path", path); command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => { using var rejected = ProjectStore.Open(folder.Project); });
    }

    [Fact]
    public void Unsafe_asset_and_run_paths_are_refused_on_write_but_missing_owned_media_is_readable()
    {
        using var folder = new TestDirectory(); using var store = ProjectStore.Create(folder.Project);
        Assert.Throws<InvalidDataException>(() => store.AddMediaAsset(Asset("../outside.wav")));
        Assert.Empty(store.MediaAssets());
        store.AddMediaAsset(Asset()); // no file required: text must stay readable if a managed copy is missing
        var run = new ProcessingRun("run", "asset", "2026-09-15T00:00:00Z", null, "running", "{}", null, null, null, null);
        Assert.Throws<InvalidDataException>(() => store.AddRun(run with { ArtifactRelativePath = "../outside.json" }));
        store.AddRun(run);
        Assert.Throws<InvalidDataException>(() => store.FinishRun("run", "completed", "../outside.json", null, null, null));
        Assert.Equal("running", Assert.Single(store.Runs()).Status);
        Assert.Single(store.MediaAssets()); Assert.Equal(Provenance.Empty, store.Read().Provenance);
    }

    [Fact]
    public void Database_only_bundle_cannot_silently_drop_owned_media()
    {
        using var folder = new TestDirectory(); using var store = ProjectStore.Create(folder.Project);
        store.AddMediaAsset(Asset());
        var destination = Path.Combine(folder.Root, "existing.soundoff.zip"); File.WriteAllText(destination, "keep previous export");
        Assert.Contains("media", Assert.Throws<InvalidDataException>(() => ProjectBundle.Export(store, destination, overwrite: true)).Message);
        Assert.Equal("keep previous export", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(folder.Root, ".*.tmp*"));
    }

    [Fact]
    public void Old_database_only_bundle_with_media_rows_is_refused_without_creating_broken_project()
    {
        using var folder = new TestDirectory(); byte[] database; Transcript document;
        using (var store = ProjectStore.Create(folder.Project))
        {
            store.AddMediaAsset(Asset()); document = store.Read();
            var backup = folder.Project + ".backup"; store.BackupTo(backup); database = File.ReadAllBytes(backup);
        }
        var bundle = Path.Combine(folder.Root, "old.soundoff.zip");
        var manifest = new BundleManifest(1, BundleManifest.FormatName, document.ProjectId, document.Title, document.Revision,
            ProjectStore.SchemaVersion, BundleManifest.DatabaseEntry, database.Length, Convert.ToHexString(SHA256.HashData(database)));
        using (var zip = ZipFile.Open(bundle, ZipArchiveMode.Create))
        {
            using (var entry = zip.CreateEntry(BundleManifest.ManifestEntry).Open()) JsonSerializer.Serialize(entry, manifest, DocumentJson.Options);
            using (var entry = zip.CreateEntry(BundleManifest.DatabaseEntry).Open()) entry.Write(database);
        }
        var destination = Path.Combine(folder.Root, "imported.soundoff.sqlite");
        Assert.Contains("media", Assert.Throws<InvalidDataException>(() => ProjectBundle.Import(bundle, destination)).Message);
        Assert.False(File.Exists(destination)); Assert.Empty(Directory.GetFiles(folder.Root, ".imported.*"));
    }

    [Fact]
    public void Changed_manual_interval_discards_stale_word_alignment_and_undo_restores_it()
    {
        using var folder = new TestDirectory();
        var source = SyntheticFixture.Create(Guid.NewGuid(), 0);
        source = source with { Blocks = [source.Blocks[0] with { Text = "hello", Timing = new(1_000_000, 2_000_000), Words = [new Word("hello", new(1_000_000, 2_000_000))] }] };
        using var store = ProjectStore.Create(folder.Project, source);
        var id = source.Blocks[0].Id;
        var noChange = store.Apply(0, EditBatch.None with { BlockTimings = new Dictionary<Guid, TimeRange?> { [id] = new(1_000_000, 2_000_000) } });
        Assert.Equal(0, noChange.Revision); Assert.Single(noChange.Blocks[0].Words);
        var edited = store.Apply(0, EditBatch.None with { BlockTimings = new Dictionary<Guid, TimeRange?> { [id] = new(5_000_000, 6_000_000) } });
        Assert.Empty(edited.Blocks[0].Words); Assert.Equal(new TimeRange(5_000_000, 6_000_000), edited.Blocks[0].Timing);
        var undone = store.Undo(edited.Revision); Assert.Single(undone.Blocks[0].Words); Assert.Equal(source.Blocks[0].Timing, undone.Blocks[0].Timing);
        Assert.Empty(store.Redo(undone.Revision).Blocks[0].Words);
    }

    [Fact]
    public void Srt_blank_author_lines_do_not_terminate_cues_or_lose_later_text()
    {
        var source = SyntheticFixture.Create(Guid.NewGuid(), 0);
        source = source with { Blocks = [source.Blocks[0] with { Text = "first\r\n\r\nsecond\rthird", Timing = new(0, 1_000_000) }] };
        var before = DocumentJson.Serialize(source);
        var result = SubtitleExport.RenderSrt(source, new SubtitleOptions(IncludeSpeakerLabels: false));
        Assert.Single(result.Srt.TrimEnd('\n').Split("\n\n"));
        Assert.Contains("first\nsecond\nthird\n", result.Srt); Assert.DoesNotContain('\r', result.Srt);
        Assert.Equal(before, DocumentJson.Serialize(source));
    }

    [Fact]
    public void Srt_overlap_policy_applies_after_millisecond_quantization()
    {
        var source = SyntheticFixture.Create(Guid.NewGuid(), 0);
        source = source with { Blocks = source.Blocks.Take(2).Select((b, i) => b with
            { Timing = i == 0 ? new(0, 1500) : new(1500, 3000) }).ToImmutableArray() };
        Assert.Throws<InvalidDataException>(() => SubtitleExport.RenderSrt(source));
        var result = SubtitleExport.RenderSrt(source, new SubtitleOptions(Overlap: OverlapPolicy.Combine));
        Assert.Equal(1, result.CueCount); Assert.Equal(1, result.CombinedOverlaps);
        Assert.Contains("00:00:00,000 --> 00:00:00,003", result.Srt);
    }

    [Fact]
    public void Unrenderable_timing_is_rejected_before_it_can_be_saved()
    {
        var source = SyntheticFixture.Create(Guid.NewGuid(), 0);
        var end = TimeText.MaxMicroseconds + 1;
        Assert.Throws<InvalidDataException>(() => DocumentRules.Validate(source with
            { Blocks = [source.Blocks[0] with { Timing = new(0, end) }] }));
        Assert.Throws<InvalidDataException>(() => DocumentRules.Validate(source with
            { Blocks = [source.Blocks[0] with { Timing = new(0, 1), Words = [new Word("bad", new(0, end))] }] }));
        using var folder = new TestDirectory(); using var store = ProjectStore.Create(folder.Project);
        Assert.Throws<InvalidDataException>(() => store.AddMediaAsset(Asset() with { DurationMicroseconds = end }));
        Assert.Empty(store.MediaAssets());
    }

    [AvaloniaFact]
    public void Short_run_ids_can_be_rendered_without_losing_the_open_project()
    {
        using var folder = new TestDirectory();
        using (var store = ProjectStore.Create(folder.Project, SyntheticFixture.Create(Guid.NewGuid(), 0)))
        {
            store.AddMediaAsset(Asset());
            store.AddRun(new("r1", "asset", "2026-09-15T00:00:00Z", null, "running", "{}", null, null, null, null));
        }
        var window = new MainWindow(null, folder.Settings, folder.Project, playbackEngine: new FakePlaybackEngine(), captureEngine: new FakeCaptureEngine());
        try
        {
            window.Show();
            Assert.DoesNotContain("Operation failed", window.FindControl<TextBlock>("StatusText")!.Text);
            Assert.Contains(window.FindControl<StackPanel>("RunHost")!.GetVisualDescendants().OfType<TextBlock>(), t => t.Text!.StartsWith("Run r1 ·"));
            window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("title")).Text = "Still editable";
            window.FindControl<Button>("SaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("Saved · revision 1", window.FindControl<TextBlock>("StatusText")!.Text);
        }
        finally { window.Close(); }
        using var reopened = ProjectStore.Open(folder.Project); Assert.Equal("Still editable", reopened.Read().Title);
    }

    [Fact]
    public void Owned_path_refuses_a_junction_without_reading_its_target()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows junction adapter; lexical tests above are portable.
        using var folder = new TestDirectory();
        var owned = Path.Combine(folder.Root, "owned"); var outside = Path.Combine(folder.Root, "outside");
        Directory.CreateDirectory(owned); Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "sample.wav"); File.WriteAllText(target, "untouched synthetic sentinel");
        var link = Path.Combine(owned, "media");
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, outside }) info.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error);
        try
        {
            Assert.Throws<InvalidDataException>(() => OwnedProjectPath.Resolve(owned, "media/sample.wav"));
            Assert.Equal("untouched synthetic sentinel", File.ReadAllText(target));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData("60:00", 3_600_000_000L)][InlineData("90:00.000001", 5_400_000_001L)]
    public void Minutes_only_input_accepts_recordings_longer_than_an_hour(string text, long microseconds)
        => Assert.Equal(microseconds, TimeText.Parse(text));
}
