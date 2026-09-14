using System.Collections.Immutable;
using SoundOff.Core;
using Xunit;

namespace SoundOff.Tests;

public sealed class SubtitleTests
{
    private static Transcript Timed(params (long Start, long End)?[] timings)
    {
        var source = SyntheticFixture.Create(Guid.NewGuid(), 5);
        return source with { Blocks = source.Blocks.Select((b, i) => b with { Timing = timings[i] is { } t ? new TimeRange(t.Start, t.End) : null }).ToImmutableArray() };
    }

    [Theory]
    [InlineData(0, false, "00:00:00,000")][InlineData(1_500, false, "00:00:00,001")][InlineData(1_500, true, "00:00:00,002")]
    [InlineData(1_000, true, "00:00:00,001")][InlineData(3_661_001_000, false, "01:01:01,001")][InlineData(359_999_999_999, true, "100:00:00,000")]
    public void Timestamps_round_starts_down_and_ends_up(long microseconds, bool roundUp, string expected) => Assert.Equal(expected, SubtitleExport.Timestamp(microseconds, roundUp));

    [Fact] public void Wrapping_is_soft_keeps_words_whole_and_honours_author_line_breaks()
    {
        Assert.Equal(["one two", "three", "four"], SubtitleExport.Wrap("one two three four", 9));
        Assert.Equal(["supercalifragilistic", "x"], SubtitleExport.Wrap("supercalifragilistic x", 8));
        Assert.Equal(["first line", "second 👩🏽‍💻"], SubtitleExport.Wrap("first line\nsecond 👩🏽‍💻", 42));
        Assert.Equal(["a", "", "b"], SubtitleExport.Wrap("a\n\nb", 42));
        Assert.Empty(SubtitleExport.Wrap("   ", 42));
    }

    [Fact] public void Untimed_paragraphs_are_refused_unless_explicitly_excluded()
    {
        var fixture = SyntheticFixture.Create(Guid.NewGuid(), 0);
        var error = Assert.Throws<InvalidDataException>(() => SubtitleExport.RenderSrt(fixture));
        Assert.Contains("Paragraph(s) 1, 2, 3 have no timing", error.Message);
        var excluded = SubtitleExport.RenderSrt(fixture, new SubtitleOptions(ExcludeUntimed: true));
        Assert.Equal("", excluded.Srt); Assert.Equal(0, excluded.CueCount); Assert.Equal([1, 2, 3], excluded.ExcludedUntimedParagraphs);
        var partial = SubtitleExport.RenderSrt(Timed((1_000_000, 2_000_000), null, (4_000_000, 5_000_000)), new SubtitleOptions(ExcludeUntimed: true));
        Assert.Equal(2, partial.CueCount); Assert.Equal([2], partial.ExcludedUntimedParagraphs);
    }

    [Fact] public void Cues_are_numbered_ordered_by_start_labelled_and_rounded_outward()
    {
        var document = Timed((4_000_000, 5_000_000), (1_000_001, 1_000_999), (2_000_000, 4_000_000)); // out of document order; cues 2 and 3 touch
        var result = SubtitleExport.RenderSrt(document);
        Assert.Equal(3, result.CueCount); Assert.Equal(0, result.CombinedOverlaps); Assert.Empty(result.ExcludedUntimedParagraphs);
        var lines = result.Srt.Split('\n');
        Assert.Equal("1", lines[0]); Assert.Equal("00:00:01,000 --> 00:00:01,001", lines[1]); Assert.StartsWith("Demo speaker B: Kumusta! Halimbawang", lines[2]);
        Assert.Contains("00:00:02,000 --> 00:00:04,000", result.Srt); Assert.Contains("00:00:04,000 --> 00:00:05,000", result.Srt);
        Assert.Contains("中文, 👩🏽‍💻.", result.Srt); Assert.EndsWith("\n\n", result.Srt);
        Assert.All(lines.Where(l => l.Length > 0 && !l.Contains("-->") && !int.TryParse(l, out _)), l => Assert.True(l.Length <= 42, l));
        var unlabelled = SubtitleExport.RenderSrt(document, new SubtitleOptions(IncludeSpeakerLabels: false, MaxLineLength: 200));
        Assert.DoesNotContain("Demo speaker", unlabelled.Srt); Assert.Contains("\nKumusta! Halimbawang teksto lang ito.", unlabelled.Srt);
        Assert.Throws<ArgumentOutOfRangeException>(() => SubtitleExport.RenderSrt(document, new SubtitleOptions(MaxLineLength: 7)));
    }

    [Fact] public void Overlaps_are_refused_by_default_and_combined_into_shared_cues_on_request()
    {
        var document = Timed((1_000_000, 3_500_000), (3_000_000, 5_000_000), (6_000_000, 7_000_000));
        Assert.Contains("Overlapping paragraphs", Assert.Throws<InvalidDataException>(() => SubtitleExport.RenderSrt(document)).Message);
        var combined = SubtitleExport.RenderSrt(document, new SubtitleOptions(Overlap: OverlapPolicy.Combine, MaxLineLength: 200));
        Assert.Equal(2, combined.CueCount); Assert.Equal(1, combined.CombinedOverlaps);
        var cues = combined.Srt.TrimEnd('\n').Split("\n\n");
        Assert.StartsWith("1\n00:00:01,000 --> 00:00:05,000\nDemo speaker A: This is an authored", cues[0]);
        Assert.Contains("\nDemo speaker B: Kumusta!", cues[0]);
        Assert.StartsWith("2\n00:00:06,000 --> 00:00:07,000\nDemo speaker A: No words", cues[1]);
        var chain = Timed((1, 10), (5, 20), (15, 30)); // a chain collapses into one cue spanning the union
        var one = SubtitleExport.RenderSrt(chain, new SubtitleOptions(Overlap: OverlapPolicy.Combine));
        Assert.Equal(1, one.CueCount); Assert.Equal(2, one.CombinedOverlaps); Assert.Contains("00:00:00,000 --> 00:00:00,001", one.Srt);
    }

    [Fact] public void Empty_timed_paragraphs_are_skipped_and_the_snapshot_is_never_mutated()
    {
        var document = Timed((1_000_000, 2_000_000), (2_000_000, 3_000_000), (3_000_000, 4_000_000));
        document = document with { Blocks = document.Blocks.SetItem(1, document.Blocks[1] with { Text = "   " }) };
        var before = DocumentJson.Serialize(document);
        var result = SubtitleExport.RenderSrt(document);
        Assert.Equal(2, result.CueCount); Assert.Equal(1, result.SkippedEmpty); Assert.Equal(before, DocumentJson.Serialize(document));
    }
}
