using System.Collections.Immutable;
using SoundOff.Core;
using Xunit;

namespace SoundOff.Tests;

public sealed class OperationTests
{
    private static Transcript Fixture() => SyntheticFixture.Create(Guid.NewGuid(), 0);
    private static Transcript Apply(Transcript source, params DocumentOperation[] operations) => TranscriptEdits.Apply(source, EditBatch.None, operations);

    [Theory]
    [InlineData("José 中文", 0, false)][InlineData("José 中文", 7, false)]        // ends of the text
    [InlineData("José 中文", 4, true)][InlineData("José 中文", 5, true)]          // around the space
    [InlineData("Jose\u0301 x", 4, false)][InlineData("Jose\u0301 x", 5, true)]  // before/after a combining acute
    [InlineData("a👩🏽‍💻b", 2, false)][InlineData("a👩🏽‍💻b", 3, false)][InlineData("a👩🏽‍💻b", 5, false)][InlineData("a👩🏽‍💻b", 8, true)] // inside the ZWJ sequence
    [InlineData("a🇵🇭b", 3, false)][InlineData("a🇵🇭b", 5, true)]              // regional-indicator pair
    public void Split_offsets_must_be_interior_grapheme_boundaries(string text, int offset, bool allowed)
    {
        var source = Fixture(); source = source with { Blocks = [source.Blocks[0] with { Text = text }] };
        var split = new SplitBlock(source.Blocks[0].Id, offset, Guid.NewGuid());
        Assert.Equal(allowed, DocumentOperation.IsInteriorGraphemeBoundary(text, offset));
        if (allowed) Assert.Equal(text, string.Concat(Apply(source, split).Blocks.Select(b => b.Text)));
        else Assert.Throws<InvalidDataException>(() => Apply(source, split));
    }

    [Fact] public void Split_keeps_the_left_id_untimes_both_halves_and_leaves_other_blocks_alone()
    {
        var source = Fixture();
        source = source with { Blocks = source.Blocks.Select(b => b with { Timing = new TimeRange(10, 20) }).ToImmutableArray() };
        var target = source.Blocks[1]; var newId = Guid.NewGuid(); var offset = target.Text.IndexOf("Halimbawang", StringComparison.Ordinal);
        var result = Apply(source, new SplitBlock(target.Id, offset, newId));
        Assert.Equal(4, result.Blocks.Length);
        Assert.Equal(target.Id, result.Blocks[1].Id); Assert.Equal(newId, result.Blocks[2].Id);
        Assert.Equal("Kumusta! ", result.Blocks[1].Text); Assert.StartsWith("Halimbawang", result.Blocks[2].Text);
        Assert.Null(result.Blocks[1].Timing); Assert.Null(result.Blocks[2].Timing);
        Assert.True(result.Blocks[1].ManuallyEdited); Assert.True(result.Blocks[2].ManuallyEdited);
        Assert.Equal(target.SpeakerId, result.Blocks[2].SpeakerId);
        Assert.Equal(source.Blocks[0], result.Blocks[0]); Assert.Equal(source.Blocks[2], result.Blocks[3]);
        Assert.Throws<InvalidDataException>(() => Apply(source, new SplitBlock(target.Id, offset, source.Blocks[0].Id)));
        Assert.Throws<InvalidDataException>(() => Apply(source, new SplitBlock(Guid.NewGuid(), offset, newId)));
    }

    [Theory]
    [InlineData("First.", "Second.", "First. Second.")][InlineData("First. ", "Second.", "First. Second.")]
    [InlineData("First.", "\nSecond.", "First.\nSecond.")][InlineData("", "Second.", "Second.")][InlineData("First.", "", "First.")]
    public void Merge_joins_with_at_most_one_inserted_space(string first, string second, string expected) => Assert.Equal(expected, MergeWithNext.Join(first, second));

    [Fact] public void Merge_removes_the_second_block_keeps_the_first_speaker_and_unions_only_known_timing()
    {
        var source = Fixture();
        var blocks = source.Blocks.SetItem(0, source.Blocks[0] with { Timing = new TimeRange(100, 200) }).SetItem(1, source.Blocks[1] with { Timing = new TimeRange(150, 400) });
        source = source with { Blocks = blocks };
        var merged = Apply(source, new MergeWithNext(source.Blocks[0].Id));
        Assert.Equal(2, merged.Blocks.Length); Assert.Equal(source.Blocks[0].Id, merged.Blocks[0].Id);
        Assert.Equal(source.Blocks[0].SpeakerId, merged.Blocks[0].SpeakerId);
        Assert.Equal(source.Blocks[0].Text + " " + source.Blocks[1].Text, merged.Blocks[0].Text);
        Assert.Equal(new TimeRange(100, 400), merged.Blocks[0].Timing); Assert.True(merged.Blocks[0].ManuallyEdited);
        Assert.Equal(source.Blocks[2], merged.Blocks[1]); Assert.DoesNotContain(merged.Blocks, b => b.Id == source.Blocks[1].Id);
        var untimed = Apply(source, new MergeWithNext(source.Blocks[1].Id)); Assert.Null(untimed.Blocks[1].Timing);
        Assert.Throws<InvalidDataException>(() => Apply(source, new MergeWithNext(source.Blocks[2].Id)));
    }

    [Fact] public void Insert_and_delete_are_positional_untimed_and_bounded()
    {
        var source = Fixture(); var speaker = source.Speakers[1].Id;
        var first = Guid.NewGuid(); var last = Guid.NewGuid();
        var result = Apply(source, new InsertBlock(null, first, speaker, ""), new InsertBlock(source.Blocks[2].Id, last, speaker, "Inserted 👩🏽‍💻"));
        Assert.Equal(5, result.Blocks.Length); Assert.Equal(first, result.Blocks[0].Id); Assert.Equal(last, result.Blocks[4].Id);
        Assert.Equal("", result.Blocks[0].Text); Assert.Null(result.Blocks[4].Timing); Assert.True(result.Blocks[4].ManuallyEdited);
        Assert.Equal(source.Blocks.ToArray(), result.Blocks.Skip(1).Take(3).ToArray());
        Assert.Throws<InvalidDataException>(() => Apply(source, new InsertBlock(null, Guid.NewGuid(), Guid.NewGuid(), "no such speaker")));
        Assert.Throws<InvalidDataException>(() => Apply(source, new InsertBlock(Guid.NewGuid(), Guid.NewGuid(), speaker, "no such anchor")));
        var deleted = Apply(source, new DeleteBlock(source.Blocks[1].Id));
        Assert.Equal([source.Blocks[0], source.Blocks[2]], deleted.Blocks.ToArray());
        var emptied = Apply(source, source.Blocks.Select(b => new DeleteBlock(b.Id)).ToArray<DocumentOperation>());
        Assert.Empty(emptied.Blocks); Assert.Equal(2, emptied.Speakers.Length); Assert.Equal(Provenance.Synthetic, emptied.Provenance);
        var full = Apply(source, Enumerable.Range(0, DocumentRules.MaxBlocks - 3).Select(_ => new InsertBlock(null, Guid.NewGuid(), speaker, "x")).ToArray<DocumentOperation>());
        Assert.Equal(DocumentRules.MaxBlocks, full.Blocks.Length);
        Assert.Throws<InvalidDataException>(() => Apply(full, new InsertBlock(null, Guid.NewGuid(), speaker, "one too many")));
        Assert.Throws<InvalidDataException>(() => Apply(full, new SplitBlock(full.Blocks[0].Id, 1, Guid.NewGuid())));
    }

    [Fact] public void Speakers_can_be_added_reassigned_and_removed_only_when_unused()
    {
        var source = Fixture(); var added = Guid.NewGuid();
        var result = Apply(source, new AddSpeaker(added, "Élodie"));
        Assert.Equal(3, result.Speakers.Length); Assert.Equal("Élodie", result.Speakers[2].Name);
        Assert.Throws<InvalidDataException>(() => Apply(source, new AddSpeaker(source.Speakers[0].Id, "duplicate id")));
        Assert.Throws<InvalidDataException>(() => Apply(source, new AddSpeaker(Guid.NewGuid(), " ")));
        Assert.Throws<InvalidDataException>(() => Apply(source, new RemoveSpeaker(source.Speakers[0].Id)));
        Assert.Throws<InvalidDataException>(() => Apply(source, new RemoveSpeaker(Guid.NewGuid())));
        var timed = source with { Blocks = source.Blocks.Select(b => b with { Timing = new TimeRange(1, 2) }).ToImmutableArray() };
        var reassigned = TranscriptEdits.Apply(timed, new EditBatch(new Dictionary<Guid, string>(), new Dictionary<Guid, string>(),
            new Dictionary<Guid, Guid> { [timed.Blocks[0].Id] = timed.Speakers[1].Id, [timed.Blocks[2].Id] = timed.Speakers[1].Id }));
        Assert.All(reassigned.Blocks, b => Assert.Equal(timed.Speakers[1].Id, b.SpeakerId));
        Assert.All(reassigned.Blocks, b => Assert.Equal(new TimeRange(1, 2), b.Timing)); Assert.All(reassigned.Blocks, b => Assert.False(b.ManuallyEdited));
        var removed = Apply(reassigned, new RemoveSpeaker(timed.Speakers[0].Id));
        Assert.Single(removed.Speakers); Assert.Equal(timed.Speakers[1].Id, removed.Speakers[0].Id);
        Assert.Throws<InvalidDataException>(() => TranscriptEdits.Apply(source, new EditBatch(new Dictionary<Guid, string>(), new Dictionary<Guid, string>(),
            new Dictionary<Guid, Guid> { [source.Blocks[0].Id] = Guid.NewGuid() })));
        var crowded = Apply(source, Enumerable.Range(0, DocumentRules.MaxSpeakers - 2).Select(i => new AddSpeaker(Guid.NewGuid(), "Speaker " + i)).ToArray<DocumentOperation>());
        Assert.Equal(DocumentRules.MaxSpeakers, crowded.Speakers.Length);
        Assert.Throws<InvalidDataException>(() => Apply(crowded, new AddSpeaker(Guid.NewGuid(), "one too many")));
    }

    [Fact] public void Title_is_part_of_the_draft_and_validated_like_other_text()
    {
        var source = Fixture();
        var renamed = TranscriptEdits.Apply(source, EditBatch.None with { Title = "Renamed 👩🏽‍💻 title" });
        Assert.Equal("Renamed 👩🏽‍💻 title", renamed.Title); Assert.Equal(source.Blocks.ToArray(), renamed.Blocks.ToArray());
        Assert.Equal(DocumentJson.Serialize(source), DocumentJson.Serialize(TranscriptEdits.Apply(source, EditBatch.None with { Title = source.Title })));
        Assert.Throws<InvalidDataException>(() => TranscriptEdits.Apply(source, EditBatch.None with { Title = " " }));
        Assert.Throws<InvalidDataException>(() => TranscriptEdits.Apply(source, EditBatch.None with { Title = new string('t', 201) }));
        Assert.StartsWith("Draft title\n", TextExport.RenderDraft(source, EditBatch.None with { Title = "Draft title" }));
        Assert.StartsWith("\n", TextExport.RenderDraft(source, EditBatch.None with { Title = "" }));
        Assert.False(EditBatch.None.IsEmpty == (EditBatch.None with { Title = "x" }).IsEmpty);
    }

    [Fact] public void Draft_export_reflects_reassigned_speakers_without_committing()
    {
        var source = Fixture(); var before = DocumentJson.Serialize(source);
        var draft = new EditBatch(new Dictionary<Guid, string> { [source.Speakers[1].Id] = "Renamed B" }, new Dictionary<Guid, string>(),
            new Dictionary<Guid, Guid> { [source.Blocks[0].Id] = source.Speakers[1].Id });
        var text = TextExport.RenderDraft(source, draft);
        Assert.StartsWith("Renamed B:\nThis is an authored synthetic example", text.Split("\n\n")[1]);
        Assert.Equal(before, DocumentJson.Serialize(source));
    }

    [Fact] public void Draft_and_structural_operations_commit_as_one_labelled_undoable_revision()
    {
        using var folder = new TestDirectory(); using var store = ProjectStore.Create(folder.Project);
        var empty = store.Read(); var loaded = store.LoadFixture(empty.Revision, SyntheticFixture.Create(empty.ProjectId, empty.Revision));
        var newId = Guid.NewGuid(); var speakerId = Guid.NewGuid();
        var draft = new EditBatch(new Dictionary<Guid, string>(), new Dictionary<Guid, string> { [loaded.Blocks[0].Id] = "Left half. Right half." });
        var committed = store.Apply(loaded.Revision, draft, new SplitBlock(loaded.Blocks[0].Id, 11, newId), new AddSpeaker(speakerId, "Added"),
            new DeleteBlock(loaded.Blocks[2].Id));
        Assert.Equal(loaded.Revision + 1, committed.Revision);
        Assert.Equal(["Left half. ", "Right half.", loaded.Blocks[1].Text], committed.Blocks.Select(b => b.Text).ToArray());
        Assert.Equal(3, committed.Speakers.Length);
        using (var sql = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={folder.Project};Pooling=False;Mode=ReadOnly"))
        {
            sql.Open(); using var command = sql.CreateCommand();
            command.CommandText = "SELECT operation FROM revision_history WHERE revision=$r"; command.Parameters.AddWithValue("$r", committed.Revision);
            Assert.Equal("manual-edit+split-block+add-speaker+delete-block", (string)command.ExecuteScalar()!);
        }
        var undone = store.Undo(committed.Revision); Assert.Equal(DocumentJson.Serialize(loaded with { Revision = undone.Revision }), DocumentJson.Serialize(undone));
        var redone = store.Redo(undone.Revision); Assert.Equal(DocumentJson.Serialize(committed with { Revision = redone.Revision }), DocumentJson.Serialize(redone));
        var beforeFailure = DocumentJson.Serialize(store.Read());
        Assert.Throws<InvalidDataException>(() => store.Apply(redone.Revision, EditBatch.None, new MergeWithNext(redone.Blocks[0].Id), new SplitBlock(redone.Blocks[2].Id, 0, Guid.NewGuid())));
        Assert.Equal(beforeFailure, DocumentJson.Serialize(store.Read()));
        Assert.Equal(redone.Revision, store.Apply(redone.Revision, EditBatch.None).Revision);
    }
}
