using System.Collections.Immutable;
using SoundOff.Core;
using Xunit;

namespace SoundOff.Tests;

public sealed class SpeakerOperationTests
{
    private static Transcript Fixture()
    {
        var source = SyntheticFixture.Create(Guid.NewGuid(), 0);
        return source with { Blocks = source.Blocks.Select(b => b with
        {
            Timing = new TimeRange(10, 30), Words = [new Word("Word", new TimeRange(12, 20), 0.8)]
        }).ToImmutableArray() };
    }

    [Fact] public void Split_and_merge_preserve_everything_except_speaker_assignments()
    {
        var source = Fixture(); var id = Guid.NewGuid();
        var split = TranscriptEdits.Apply(source, EditBatch.None, [new SplitSpeaker(source.Speakers[0].Id, id, "José 👋", [source.Blocks[0].Id])]);
        Assert.Equal(3, split.Speakers.Length);
        Assert.Equal(source.Blocks[0] with { SpeakerId = id }, split.Blocks[0]);
        Assert.Equal(source.Blocks.Skip(1), split.Blocks.Skip(1));
        var merged = TranscriptEdits.Apply(split, EditBatch.None, [new MergeSpeakers(id, source.Speakers[0].Id)]);
        Assert.Equal(DocumentJson.Serialize(source), DocumentJson.Serialize(merged));
    }

    [Fact] public void Invalid_operations_are_atomic_and_do_not_commit_the_draft()
    {
        using var folder = new TestDirectory(); using var store = ProjectStore.Create(folder.Project, Fixture());
        var source = store.Read(); var speaker = source.Speakers[0].Id; var b = source.Blocks[0].Id;
        DocumentOperation[] invalid = [
            new SplitSpeaker(speaker, Guid.NewGuid(), "New", []),
            new SplitSpeaker(speaker, Guid.NewGuid(), "New", [b, b]),
            new SplitSpeaker(speaker, Guid.NewGuid(), "New", [Guid.NewGuid()]),
            new SplitSpeaker(speaker, Guid.NewGuid(), "New", [source.Blocks[1].Id]),
            new SplitSpeaker(speaker, Guid.NewGuid(), "New", [b, source.Blocks[2].Id]),
            new SplitSpeaker(speaker, speaker, "New", [b]),
            new SplitSpeaker(speaker, Guid.Empty, "New", [b]),
            new SplitSpeaker(speaker, Guid.NewGuid(), " ", [b]),
            new SplitSpeaker(Guid.NewGuid(), Guid.NewGuid(), "New", [b]),
            new MergeSpeakers(speaker, speaker), new MergeSpeakers(speaker, Guid.NewGuid()),
            new MergeSpeakers(Guid.NewGuid(), speaker)
        ];
        foreach (var operation in invalid)
        {
            Assert.Throws<InvalidDataException>(() => store.Apply(source.Revision, EditBatch.None with { Title = "Unsaved" }, operation));
            Assert.Equal(DocumentJson.Serialize(source), DocumentJson.Serialize(store.Read()));
        }
        var full = source with { Speakers = source.Speakers.AddRange(Enumerable.Range(2, 62).Select(i => new Speaker(Guid.NewGuid(), "Speaker " + i))) };
        Assert.Throws<InvalidDataException>(() => TranscriptEdits.Apply(full, EditBatch.None, [new SplitSpeaker(speaker, Guid.NewGuid(), "New", [b])]));
    }

    [Fact] public void Split_and_merge_are_single_revisions_with_durable_undo_redo()
    {
        using var folder = new TestDirectory();
        Transcript source, split, merged;
        var added = Guid.NewGuid();
        using (var store = ProjectStore.Create(folder.Project, Fixture()))
        {
            source = store.Read();
            split = store.Apply(source.Revision, EditBatch.None with { Title = "Draft title" },
                new SplitSpeaker(source.Speakers[0].Id, added, "New", [source.Blocks[0].Id]));
            Assert.Equal(source.Revision + 1, split.Revision);
            Assert.Equal("manual-edit+split-speaker", store.History(1)[0].Operation);
            merged = store.Apply(split.Revision, EditBatch.None, new MergeSpeakers(added, source.Speakers[1].Id));
            Assert.Equal(split.Revision + 1, merged.Revision);
            Assert.Equal("merge-speakers", store.History(1)[0].Operation);
            Assert.Throws<RevisionConflictException>(() => store.Apply(split.Revision, EditBatch.None, new MergeSpeakers(added, source.Speakers[0].Id)));
        }
        using var reopened = ProjectStore.Open(folder.Project);
        var undo = reopened.Undo(merged.Revision);
        Assert.Equal(DocumentJson.Serialize(split with { Revision = undo.Revision }), DocumentJson.Serialize(undo));
        var redo = reopened.Redo(undo.Revision);
        Assert.Equal(DocumentJson.Serialize(merged with { Revision = redo.Revision }), DocumentJson.Serialize(redo));
    }
}
