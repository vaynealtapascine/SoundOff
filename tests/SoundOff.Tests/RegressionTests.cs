using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SoundOff.Core;
using SoundOff.Desktop;
using SoundOff.Protocol;
using Xunit;

namespace SoundOff.Tests;

public sealed class RegressionTests
{
    [Fact] public async Task Desktop_self_test_returns_failure_for_unwritable_output_and_usage_errors()
    {
        using var folder = new TestDirectory(); var file = Path.Combine(folder.Root, "not-a-directory");
        File.WriteAllText(file, "preserve this file");
        Assert.Equal(1, await SmokeTest.RunAsync(["--output", file]));
        Assert.Equal("preserve this file", File.ReadAllText(file));
        Assert.Equal(2, await SmokeTest.RunAsync(["--unknown"]));
    }

    [Fact] public void Earlier_schema_one_fixture_projects_still_reopen_with_honest_provenance()
    {
        using var folder = new TestDirectory();
        var earlier = SyntheticFixture.Create(Guid.NewGuid(), 0) with { Provenance = Provenance.LegacySynthetic };
        using (var store = ProjectStore.Create(folder.Project, earlier)) { }
        using var reopened = ProjectStore.Open(folder.Project);
        Assert.Equal(DocumentJson.Serialize(earlier), DocumentJson.Serialize(reopened.Read()));
        Assert.Contains("All timing is unknown", TextExport.Render(reopened.Read()));
        Assert.Throws<InvalidDataException>(() => DocumentRules.Validate(earlier with { Blocks = [earlier.Blocks[0] with { Timing = new TimeRange(1, 2) }] }));
    }

    [Fact] public void Missing_nullable_timing_and_zero_revision_fields_are_not_silently_defaulted()
    {
        var json = JsonNode.Parse(DocumentJson.Serialize(SyntheticFixture.Create(Guid.NewGuid(), 0)))!;
        json["blocks"]![0]!.AsObject().Remove("timing");
        Assert.Throws<InvalidDataException>(() => DocumentJson.Deserialize(json.ToJsonString()));
        json["blocks"]![0]!["timing"] = null;
        json.AsObject().Remove("revision");
        Assert.Throws<InvalidDataException>(() => DocumentJson.Deserialize(json.ToJsonString()));
    }

    [Fact] public async Task Missing_request_fields_and_duplicate_json_properties_are_rejected()
    {
        var request = WorkerProtocol.Start(Guid.NewGuid(), Guid.NewGuid(), 0);
        var json = JsonSerializer.Serialize(request, DocumentJson.Options);
        var missing = JsonNode.Parse(json)!; missing.AsObject().Remove("baseRevision");
        foreach (var malformed in new[] { missing.ToJsonString(), "{\"version\":99," + json[1..] })
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(malformed + "\n"));
            await Assert.ThrowsAsync<InvalidDataException>(() => new JsonLineReader(stream).ReadAsync(CancellationToken.None));
        }
        var document = DocumentJson.Serialize(SyntheticFixture.Create(Guid.NewGuid(), 0));
        Assert.Throws<InvalidDataException>(() => DocumentJson.Deserialize("{\"revision\":1," + document[1..]));
    }

    [Fact] public async Task Already_cancelled_request_never_starts_a_worker()
    {
        var client = new FixtureWorkerClient(() => throw new InvalidOperationException("Must not spawn"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.LoadAsync(Guid.NewGuid(), 0, new CancellationToken(true)));
    }

    [Fact] public void Blank_speaker_draft_can_be_rescued_without_corrupting_the_saved_document()
    {
        var source = SyntheticFixture.Create(Guid.NewGuid(), 3); var before = DocumentJson.Serialize(source);
        var draft = new EditBatch(new Dictionary<Guid, string> { [source.Speakers[0].Id] = "" },
            new Dictionary<Guid, string> { [source.Blocks[0].Id] = "Rescue piña 👩🏽‍💻\n中文" });
        Assert.Throws<InvalidDataException>(() => TranscriptEdits.Apply(source, draft));
        var rescued = TextExport.RenderDraft(source, draft);
        Assert.Contains("UNSAVED DRAFT based on revision 3", rescued); Assert.Contains("SYNTHETIC DEMO", rescued);
        Assert.Contains(":\nRescue piña 👩🏽‍💻\n中文", rescued); Assert.Equal(before, DocumentJson.Serialize(source));
    }

    [Fact] public void Failed_export_preserves_previous_file_and_removes_staging_file()
    {
        using var folder = new TestDirectory(); var path = Path.Combine(folder.Root, "existing.txt");
        File.WriteAllText(path, "Keep original 👩🏽‍💻");
        Assert.Throws<EncoderFallbackException>(() => TextExport.WriteAtomic(path, "bad \ud800", overwrite: true));
        Assert.Equal("Keep original 👩🏽‍💻", File.ReadAllText(path)); Assert.Single(Directory.GetFiles(folder.Root));
    }

    [Fact] public void Overlap_null_timing_and_stable_ids_survive_sqlite_edits_and_undo()
    {
        using var folder = new TestDirectory();
        var fixture = SyntheticFixture.Create(Guid.NewGuid(), 0);
        fixture = fixture with { Blocks = fixture.Blocks.Select((b, i) => b with { Timing = i == 2 ? null : new TimeRange(1 + i, 10 + i) }).ToImmutableArray() };
        using (var store = ProjectStore.Create(folder.Project, fixture))
        {
            var edited = store.Apply(0, new(new Dictionary<Guid, string>(), new Dictionary<Guid, string> { [fixture.Blocks[0].Id] = "Edited without shifting other intervals" }));
            Assert.Null(edited.Blocks[0].Timing); Assert.Equal(fixture.Blocks[1].Timing, edited.Blocks[1].Timing);
        }
        using var reopen = ProjectStore.Open(folder.Project);
        var restored = reopen.Undo(1);
        Assert.Equal(fixture.Blocks.ToArray(), restored.Blocks.ToArray()); Assert.Equal(fixture.ProjectId, restored.ProjectId);
        Assert.Contains("Any timing is synthetic, not measured", TextExport.Render(restored));
    }

    [Theory][InlineData("undo_stack")][InlineData("revision_history")]
    public void Incomplete_schema_is_refused_before_ui_replacement_and_releases_lock(string table)
    {
        using var folder = new TestDirectory(); using (var store = ProjectStore.Create(folder.Project)) { }
        using (var sql = new SqliteConnection($"Data Source={folder.Project};Pooling=False"))
        {
            sql.Open(); using var command = sql.CreateCommand();
            command.CommandText = "DROP TABLE " + table; command.ExecuteNonQuery();
        }
        var bytes = File.ReadAllBytes(folder.Project);
        ProjectStore? unexpected = null;
        try { Assert.Throws<SqliteException>(() => unexpected = ProjectStore.Open(folder.Project)); }
        finally { unexpected?.Dispose(); }
        Assert.Equal(bytes, File.ReadAllBytes(folder.Project));
        using var lease = new FileStream(folder.Project + ".writer.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact] public async Task Worker_self_test_is_real_and_invalid_input_never_produces_a_transcript()
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "worker", "SoundOff.Worker.dll");
        foreach (var mode in new[] { "--self-test", "invalid-input" })
        {
            var info = FixtureWorkerClient.ForAssembly(assembly);
            if (mode == "--self-test") info.ArgumentList.Add(mode);
            info.UseShellExecute = false; info.CreateNoWindow = true;
            info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
            using var child = Process.Start(info)!;
            try
            {
                if (mode == "invalid-input") await child.StandardInput.WriteLineAsync("{}");
                child.StandardInput.Close();
                var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (mode == "--self-test")
                {
                    Assert.Equal(0, child.ExitCode); Assert.Equal("", await error);
                    using var report = JsonDocument.Parse(await output);
                    Assert.Equal("passed", report.RootElement.GetProperty("status").GetString());
                    Assert.False(report.RootElement.GetProperty("inference").GetBoolean());
                }
                else { Assert.Equal(1, child.ExitCode); Assert.Equal("", await output); Assert.Contains("no transcript was produced", await error); }
            }
            finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); }
        }
    }
}
