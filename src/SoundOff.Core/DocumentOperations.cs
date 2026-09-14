using System.Collections.Immutable;
using System.Globalization;

namespace SoundOff.Core;

// Structural edits over the app-owned snapshot. Every operation returns a validated document; removed block IDs
// disappear from the current projection but remain in revision history. Split/merge/insert text is explicitly
// untimed (null), never apportioned or invented; merge keeps only the union of two already-timed intervals.
public abstract record DocumentOperation
{
    public abstract string Name { get; }
    internal abstract Transcript ApplyTo(Transcript document);

    private protected static int BlockIndex(Transcript document, Guid blockId)
    {
        for (var i = 0; i < document.Blocks.Length; i++) if (document.Blocks[i].Id == blockId) return i;
        throw new InvalidDataException("The operation targets a paragraph that is not in the current document.");
    }
    private protected static void RequireSpeaker(Transcript document, Guid speakerId)
    {
        if (!document.Speakers.Any(s => s.Id == speakerId)) throw new InvalidDataException("The operation targets a speaker that is not in the current document.");
    }
    private protected static Transcript Validated(Transcript document) { DocumentRules.Validate(document); return document; }

    // Coordinate system: UTF-16 code-unit offsets that fall on an extended grapheme cluster boundary strictly inside the text.
    // Splitting between surrogate halves, before a combining mark or inside an emoji ZWJ sequence is refused.
    public static bool IsInteriorGraphemeBoundary(string text, int offset)
    {
        if (offset <= 0 || offset >= text.Length) return false;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            if (elements.ElementIndex == offset) return true;
            if (elements.ElementIndex > offset) return false;
        }
        return false;
    }
}

public sealed record SplitBlock(Guid BlockId, int Utf16Offset, Guid NewBlockId) : DocumentOperation
{
    public override string Name => "split-block";
    internal override Transcript ApplyTo(Transcript document)
    {
        var index = BlockIndex(document, BlockId); var block = document.Blocks[index];
        if (!IsInteriorGraphemeBoundary(block.Text, Utf16Offset))
            throw new InvalidDataException("Place the cursor inside the paragraph, between whole characters, before splitting.");
        var left = block with { Text = block.Text[..Utf16Offset], Timing = null, Words = default, ManuallyEdited = true };
        var right = new TranscriptBlock(NewBlockId, block.SpeakerId, block.Text[Utf16Offset..], null, true);
        return Validated(document with { Blocks = document.Blocks.SetItem(index, left).Insert(index + 1, right) });
    }
}

public sealed record MergeWithNext(Guid BlockId) : DocumentOperation
{
    public override string Name => "merge-blocks";
    internal override Transcript ApplyTo(Transcript document)
    {
        var index = BlockIndex(document, BlockId);
        if (index + 1 >= document.Blocks.Length) throw new InvalidDataException("The last paragraph has no following paragraph to merge with.");
        var first = document.Blocks[index]; var second = document.Blocks[index + 1];
        var timing = Union(first.Timing, second.Timing);
        // Word evidence survives only when both sides were timed (so the union is meaningful) and both carried words.
        var words = timing is not null && first.WordsOrEmpty.Length > 0 && second.WordsOrEmpty.Length > 0 ? first.Words.AddRange(second.Words) : default;
        var merged = first with { Text = Join(first.Text, second.Text), Timing = timing, Words = words, ManuallyEdited = true };
        return Validated(document with { Blocks = document.Blocks.SetItem(index, merged).RemoveAt(index + 1) });
    }
    // One space is inserted only when both sides have text and neither boundary already has whitespace.
    internal static string Join(string first, string second)
    {
        if (first.Length == 0 || second.Length == 0) return first + second;
        if (char.IsWhiteSpace(first[^1]) || char.IsWhiteSpace(second[0])) return first + second;
        return first + " " + second;
    }
    internal static TimeRange? Union(TimeRange? a, TimeRange? b) => a is null || b is null ? null
        : new(Math.Min(a.StartMicroseconds, b.StartMicroseconds), Math.Max(a.EndMicroseconds, b.EndMicroseconds));
}

// AfterBlockId null inserts at the beginning of the document.
public sealed record InsertBlock(Guid? AfterBlockId, Guid NewBlockId, Guid SpeakerId, string Text) : DocumentOperation
{
    public override string Name => "insert-block";
    internal override Transcript ApplyTo(Transcript document)
    {
        RequireSpeaker(document, SpeakerId);
        var index = AfterBlockId is { } after ? BlockIndex(document, after) + 1 : 0;
        var block = new TranscriptBlock(NewBlockId, SpeakerId, Text, null, true);
        return Validated(document with { Blocks = document.Blocks.Insert(index, block) });
    }
}

public sealed record DeleteBlock(Guid BlockId) : DocumentOperation
{
    public override string Name => "delete-block";
    internal override Transcript ApplyTo(Transcript document) =>
        Validated(document with { Blocks = document.Blocks.RemoveAt(BlockIndex(document, BlockId)) });
}

public sealed record AddSpeaker(Guid SpeakerId, string DisplayName) : DocumentOperation
{
    public override string Name => "add-speaker";
    internal override Transcript ApplyTo(Transcript document) =>
        Validated(document with { Speakers = document.Speakers.Add(new Speaker(SpeakerId, DisplayName)) });
}

public sealed record RemoveSpeaker(Guid SpeakerId) : DocumentOperation
{
    public override string Name => "remove-speaker";
    internal override Transcript ApplyTo(Transcript document)
    {
        RequireSpeaker(document, SpeakerId);
        if (document.Blocks.Any(b => b.SpeakerId == SpeakerId))
            throw new InvalidDataException("Reassign this speaker's paragraphs before removing the speaker.");
        return Validated(document with { Speakers = document.Speakers.RemoveAll(s => s.Id == SpeakerId) });
    }
}
