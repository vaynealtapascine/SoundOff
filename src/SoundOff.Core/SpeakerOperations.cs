using System.Collections.Immutable;

namespace SoundOff.Core;

public sealed record SplitSpeaker(Guid SourceSpeakerId, Guid NewSpeakerId, string DisplayName,
    ImmutableArray<Guid> BlockIds) : DocumentOperation
{
    public override string Name => "split-speaker";

    internal override Transcript ApplyTo(Transcript document)
    {
        RequireSpeaker(document, SourceSpeakerId);
        if (BlockIds.IsDefaultOrEmpty) throw new InvalidDataException("Choose at least one paragraph to move.");
        var selected = BlockIds.ToHashSet();
        if (selected.Count != BlockIds.Length) throw new InvalidDataException("A paragraph was selected more than once.");
        var assigned = document.Blocks.Where(b => b.SpeakerId == SourceSpeakerId).Select(b => b.Id).ToHashSet();
        if (!selected.IsSubsetOf(assigned)) throw new InvalidDataException("Every selected paragraph must belong to the speaker being split.");
        if (selected.Count == assigned.Count) throw new InvalidDataException("Leave at least one paragraph with the original speaker. To rename everyone, edit the speaker's name instead.");
        return Validated(document with
        {
            Speakers = document.Speakers.Add(new Speaker(NewSpeakerId, DisplayName)),
            Blocks = document.Blocks.Select(b => selected.Contains(b.Id) ? b with { SpeakerId = NewSpeakerId } : b).ToImmutableArray()
        });
    }
}

public sealed record MergeSpeakers(Guid SourceSpeakerId, Guid TargetSpeakerId) : DocumentOperation
{
    public override string Name => "merge-speakers";

    internal override Transcript ApplyTo(Transcript document)
    {
        RequireSpeaker(document, SourceSpeakerId);
        RequireSpeaker(document, TargetSpeakerId);
        if (SourceSpeakerId == TargetSpeakerId) throw new InvalidDataException("Choose a different speaker to merge into.");
        return Validated(document with
        {
            Speakers = document.Speakers.RemoveAll(s => s.Id == SourceSpeakerId),
            Blocks = document.Blocks.Select(b => b.SpeakerId == SourceSpeakerId ? b with { SpeakerId = TargetSpeakerId } : b).ToImmutableArray()
        });
    }
}
