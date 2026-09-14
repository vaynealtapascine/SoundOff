using System.Text;
using System.Text.Json;
using SoundOff.Core;
using SoundOff.Protocol;

namespace SoundOff.Desktop;

public static class SmokeTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--output"))
        { Console.Error.WriteLine("Usage: --self-test [--output directory]"); return 2; }
        string? directory = null;
        var checks = new List<string>();
        try
        {
            directory = Path.GetFullPath(Path.Combine(args.Length == 2 ? args[1] : "artifacts/self-test", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory);
            var project = Path.Combine(directory, "Synthetic smoke.soundoff.sqlite");
            var export = Path.Combine(directory, "Synthetic Unicode.txt");
            long savedRevision; Guid speakerId; Guid blockId;
            using (var store = ProjectStore.Create(project))
            {
                var empty = store.Read(); Require(empty.Blocks.IsEmpty && empty.Revision == 0, "honest empty state"); checks.Add("empty-state");
                var proposal = await new FixtureWorkerClient().LoadAsync(empty.ProjectId, empty.Revision);
                var loaded = store.LoadFixture(empty.Revision, proposal);
                Require(loaded.Provenance == Provenance.Synthetic && loaded.Blocks.All(b => b.Timing is null), "synthetic untimed provenance"); checks.Add("real-child-worker-fixture");
                try { using var duplicate = ProjectStore.Open(project); throw new Exception("Second writer incorrectly admitted."); }
                catch (ProjectLockedException) { checks.Add("writer-lock"); }
                speakerId = loaded.Speakers[0].Id; blockId = loaded.Blocks[0].Id;
                var edited = store.Apply(loaded.Revision, new(new Dictionary<Guid, string> { [speakerId] = "José 👩🏽‍💻" },
                    new Dictionary<Guid, string> { [blockId] = "Kumusta, piña! 中文 — edited synthetic text.\nSecond line." }));
                try { store.Apply(loaded.Revision, new(new Dictionary<Guid, string>(), new Dictionary<Guid, string>())); throw new Exception("Stale edit admitted."); }
                catch (RevisionConflictException) { checks.Add("stale-edit-rejected"); }
                savedRevision = edited.Revision; checks.Add("transactional-unicode-edit");
            }
            using (var reopened = ProjectStore.Open(project))
            {
                var saved = reopened.Read(); Require(saved.Revision == savedRevision && saved.Speakers[0].Id == speakerId && saved.Blocks[0].Id == blockId, "stable IDs/reopen");
                var text = TextExport.Render(saved); TextExport.WriteAtomic(export, text);
                Require(File.ReadAllText(export, new UTF8Encoding(false, true)) == text && text.Contains("José 👩🏽‍💻"), "Unicode export read-back");
                checks.Add("save-reopen-export-readback");
                var undone = reopened.Undo(saved.Revision); Require(undone.Revision > saved.Revision && undone.Speakers[0].Name == "Demo speaker A", "persistent undo"); checks.Add("persistent-undo");
            }
            using (var final = ProjectStore.Open(project))
            {
                var current = final.Read(); Require(current.Speakers[0].Name == "Demo speaker A" && final.CanRedo, "undo durability");
                checks.Add("undo-reopen");
                var redone = final.Redo(current.Revision); Require(redone.Speakers[0].Name == "José 👩🏽‍💻" && !final.CanRedo, "persistent redo"); checks.Add("persistent-redo");
                var offset = redone.Blocks[0].Text.IndexOf('\n') + 1; var split = final.Apply(redone.Revision, EditBatch.None, new SplitBlock(blockId, offset, Guid.NewGuid()));
                Require(split.Blocks.Length == 4 && split.Blocks[0].Id == blockId && split.Blocks[1].Text == "Second line." && split.Blocks[1].Timing is null, "grapheme-safe split");
                var merged = final.Apply(split.Revision, EditBatch.None, new MergeWithNext(blockId));
                Require(merged.Blocks.Length == 3 && merged.Blocks[0].Text == redone.Blocks[0].Text && final.Undo(merged.Revision).Blocks.Length == 4, "merge and undo of structural edit");
                checks.Add("structural-split-merge");
            }
            var result = JsonSerializer.Serialize(new { status = "passed", checks, project, export,
                coverage = "Non-GUI .NET/SQLite/real fixture subprocess only; no media, models or clipboard API exercised." }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(directory, "result.json"), result, new UTF8Encoding(false)); Console.WriteLine(result); return 0;
        }
        catch (Exception e)
        { Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "failed", checks, error = e.ToString(), directory })); return 1; }
    }
    private static void Require(bool condition, string assertion) { if (!condition) throw new InvalidOperationException("Smoke assertion failed: " + assertion); }
}
