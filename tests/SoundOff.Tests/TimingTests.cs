using System.Collections.Immutable;
using SoundOff.Core;
using Xunit;

namespace SoundOff.Tests;

public sealed class TimingTests
{
    [Theory]
    [InlineData(0, "0:00:00.000000")][InlineData(1, "0:00:00.000001")][InlineData(1_500_000, "0:00:01.500000")]
    [InlineData(3_661_250_000, "1:01:01.250000")][InlineData(360_000_000_000, "100:00:00.000000")]
    public void Formatting_is_exact_and_invariant(long microseconds, string expected)
    {
        Assert.Equal(expected, TimeText.Format(microseconds)); Assert.Equal(microseconds, TimeText.Parse(expected));
    }

    [Theory]
    [InlineData("0", 0L)][InlineData("5", 5_000_000L)][InlineData("5.25", 5_250_000L)][InlineData("5.000001", 5_000_001L)]
    [InlineData("1:05", 65_000_000L)][InlineData("01:05.5", 65_500_000L)][InlineData("1:02:03.250", 3_723_250_000L)][InlineData("  90  ", 90_000_000L)]
    [InlineData("100:00:00", 360_000_000_000L)]
    public void Parsing_accepts_seconds_minutes_and_hours_forms(string text, long expected) => Assert.Equal(expected, TimeText.Parse(text));

    [Theory]
    [InlineData("")][InlineData("   ")]
    public void Blank_means_untimed(string text) => Assert.Null(TimeText.Parse(text));

    [Theory]
    [InlineData("1:2:3:4", "H:MM:SS")][InlineData("-1", "plain digits")][InlineData("1,5", "plain digits")][InlineData("abc", "plain digits")]
    [InlineData("1.", "one to six digits")][InlineData("1.1234567", "one to six digits")][InlineData("1:60:00", "Minutes must be 0-59")][InlineData("1:60", "Seconds must be 0-59")][InlineData("0:00:60", "Seconds must be 0-59")]
    [InlineData("1001:00:00", "1000 hours")][InlineData("12345678", "plain digits")]
    public void Invalid_timing_text_is_refused_with_a_reason(string text, string reason)
    {
        var error = Assert.Throws<InvalidDataException>(() => TimeText.Parse(text)); Assert.Contains(reason, error.Message);
    }

    [Fact] public void Ranges_require_both_ends_and_a_positive_length()
    {
        Assert.Null(TimeText.ParseRange("", null));
        Assert.Equal(new TimeRange(1_000_000, 2_500_000), TimeText.ParseRange("1", "2.5"));
        Assert.Contains("both", Assert.Throws<InvalidDataException>(() => TimeText.ParseRange("1", "")).Message);
        Assert.Contains("both", Assert.Throws<InvalidDataException>(() => TimeText.ParseRange(null, "2")).Message);
        Assert.Contains("later than", Assert.Throws<InvalidDataException>(() => TimeText.ParseRange("2", "2")).Message);
        Assert.Contains("later than", Assert.Throws<InvalidDataException>(() => TimeText.ParseRange("3", "2")).Message);
    }

    [Fact] public void Manual_timing_in_a_draft_is_an_explicit_anchor_that_survives_a_text_edit_and_can_be_cleared()
    {
        var source = SyntheticFixture.Create(Guid.NewGuid(), 0);
        var timed = TranscriptEdits.Apply(source, EditBatch.None with { BlockTimings = new Dictionary<Guid, TimeRange?> { [source.Blocks[0].Id] = new TimeRange(1, 2) } });
        Assert.Equal(new TimeRange(1, 2), timed.Blocks[0].Timing); Assert.False(timed.Blocks[0].ManuallyEdited); Assert.Null(timed.Blocks[1].Timing);
        var both = TranscriptEdits.Apply(timed, new EditBatch(new Dictionary<Guid, string>(), new Dictionary<Guid, string> { [timed.Blocks[0].Id] = "new words" },
            BlockTimings: new Dictionary<Guid, TimeRange?> { [timed.Blocks[0].Id] = new TimeRange(5, 9) }));
        Assert.Equal("new words", both.Blocks[0].Text); Assert.Equal(new TimeRange(5, 9), both.Blocks[0].Timing); Assert.True(both.Blocks[0].ManuallyEdited);
        var textOnly = TranscriptEdits.Apply(timed, new EditBatch(new Dictionary<Guid, string>(), new Dictionary<Guid, string> { [timed.Blocks[0].Id] = "other words" }));
        Assert.Null(textOnly.Blocks[0].Timing);
        var cleared = TranscriptEdits.Apply(timed, EditBatch.None with { BlockTimings = new Dictionary<Guid, TimeRange?> { [timed.Blocks[0].Id] = null } });
        Assert.Null(cleared.Blocks[0].Timing);
        Assert.Throws<InvalidDataException>(() => TranscriptEdits.Apply(source, EditBatch.None with { BlockTimings = new Dictionary<Guid, TimeRange?> { [Guid.NewGuid()] = new TimeRange(1, 2) } }));
        Assert.Throws<InvalidDataException>(() => TranscriptEdits.Apply(source, EditBatch.None with { BlockTimings = new Dictionary<Guid, TimeRange?> { [source.Blocks[0].Id] = new TimeRange(2, 2) } }));
        Assert.False((EditBatch.None with { BlockTimings = new Dictionary<Guid, TimeRange?> { [source.Blocks[0].Id] = null } }).IsEmpty);
        var legacy = source with { Provenance = Provenance.LegacySynthetic };
        Assert.Throws<InvalidDataException>(() => TranscriptEdits.Apply(legacy, EditBatch.None with { BlockTimings = new Dictionary<Guid, TimeRange?> { [source.Blocks[0].Id] = new TimeRange(1, 2) } }));
    }
}
