using System.Collections.Immutable;
using SoundOff.Core;
using Xunit;

namespace SoundOff.Tests;

public sealed class PlaybackCursorTests
{
    private static Transcript Document()
    {
        var source = SyntheticFixture.Create(Guid.NewGuid(), 0) with { Provenance = Provenance.Model("whisperx 3.8.6") };
        return source with
        {
            Blocks = source.Blocks
                // 0: timed with words, one of them unaligned and a gap between words
                .SetItem(0, source.Blocks[0] with { Timing = new TimeRange(1_000_000, 5_000_000), Words =
                    [new("Hello", new TimeRange(1_000_000, 1_500_000)), new("unaligned", null), new("later", new TimeRange(4_000_000, 4_500_000))] })
                // 1: overlaps block 0 (a second speaker talking at the same time), no word evidence
                .SetItem(1, source.Blocks[1] with { Timing = new TimeRange(4_200_000, 6_000_000) })
                // 2: untimed
                .SetItem(2, source.Blocks[2] with { Timing = null })
        };
    }

    [Fact] public void Position_maps_to_paragraphs_with_half_open_intervals_and_preserves_overlap()
    {
        var document = Document(); var a = document.Blocks[0].Id; var b = document.Blocks[1].Id;
        Assert.Empty(PlaybackCursor.Locate(document, 0));
        Assert.Empty(PlaybackCursor.Locate(document, -5));
        Assert.Empty(PlaybackCursor.Locate(document, 999_999));
        Assert.Equal([new ActiveSpan(a, 0)], PlaybackCursor.Locate(document, 1_000_000));          // start is inclusive
        Assert.Equal([new ActiveSpan(a, -1)], PlaybackCursor.Locate(document, 1_500_000));          // word end is exclusive: gap, paragraph only
        Assert.Equal([new ActiveSpan(a, -1)], PlaybackCursor.Locate(document, 3_000_000));          // between words
        Assert.Equal([new ActiveSpan(a, 2)], PlaybackCursor.Locate(document, 4_100_000));
        // Both speakers are active at 4.3 s; neither is dropped.
        Assert.Equal([new ActiveSpan(a, 2), new ActiveSpan(b, -1)], PlaybackCursor.Locate(document, 4_300_000));
        Assert.Equal([new ActiveSpan(b, -1)], PlaybackCursor.Locate(document, 5_000_000));          // paragraph end is exclusive
        Assert.Empty(PlaybackCursor.Locate(document, 6_000_000));
        Assert.Empty(PlaybackCursor.Locate(document, 99_000_000));
    }

    [Fact] public void Unaligned_words_never_receive_invented_timing()
    {
        var document = Document(); var block = document.Blocks[0];
        Assert.Equal(-1, PlaybackCursor.WordAt(block, 2_000_000));
        Assert.Equal(1, block.Words.Count(w => w.Timing is null));
        // The unaligned word is never reported as active at any instant of its paragraph.
        for (var t = 1_000_000L; t < 5_000_000L; t += 50_000) Assert.NotEqual(1, PlaybackCursor.WordAt(block, t));
    }

    [Fact] public void Seek_targets_fall_back_from_word_to_paragraph_and_report_what_was_used()
    {
        var document = Document(); var timed = document.Blocks[0]; var untimed = document.Blocks[2];
        Assert.Equal((SeekAvailability.Available, 1_000_000L), PlaybackCursor.SeekTarget(timed));
        Assert.Equal((SeekAvailability.Untimed, 0L), PlaybackCursor.SeekTarget(untimed));
        Assert.Equal((SeekAvailability.Available, 4_000_000L), PlaybackCursor.SeekTarget(timed, 2));
        Assert.Equal((SeekAvailability.Unaligned, 1_000_000L), PlaybackCursor.SeekTarget(timed, 1));   // word has no timing: paragraph start
        Assert.Equal((SeekAvailability.Unaligned, 1_000_000L), PlaybackCursor.SeekTarget(timed, 99));  // out of range behaves the same way
        Assert.Equal((SeekAvailability.Untimed, 0L), PlaybackCursor.SeekTarget(untimed, 0));
    }

    [Fact] public void A_document_without_any_timing_never_reports_a_position()
    {
        var untimed = SyntheticFixture.Create(Guid.NewGuid(), 0);
        Assert.All(untimed.Blocks, b => Assert.Null(b.Timing));
        for (var t = 0L; t < 10_000_000L; t += 250_000) Assert.Empty(PlaybackCursor.Locate(untimed, t));
    }
}
