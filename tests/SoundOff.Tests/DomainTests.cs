using System.Collections.Immutable;
using System.Text;
using SoundOff.Core;
using Xunit;

namespace SoundOff.Tests;

public sealed class DomainTests
{
    [Fact] public void Empty_is_honest_and_has_no_synthetic_data()
    { var empty = Transcript.CreateEmpty(); Assert.Empty(empty.Blocks); Assert.Empty(empty.Speakers); Assert.Equal(Provenance.Empty, empty.Provenance); DocumentRules.Validate(empty); }

    [Fact] public void Fixture_is_deterministic_untimed_and_explicitly_synthetic()
    {
        var id = Guid.NewGuid(); var first = SyntheticFixture.Create(id, 0); var second = SyntheticFixture.Create(id, 0);
        Assert.Equal(DocumentJson.Serialize(first), DocumentJson.Serialize(second));
        Assert.All(first.Blocks, b => Assert.Null(b.Timing)); Assert.Contains("not a recording or model output", first.Provenance.Notice);
    }

    [Fact] public void Unicode_roundtrip_preserves_combining_sequences_emoji_and_newlines()
    {
        var source = SyntheticFixture.Create(Guid.NewGuid(), 0);
        const string unicode = "José piña ñ 👩🏽‍💻 🇵🇭 中文\n\t第二行";
        var edited = TranscriptEdits.Apply(source, new(new Dictionary<Guid, string> { [source.Speakers[0].Id] = "Élodie 👩🏽‍💻" }, new Dictionary<Guid, string> { [source.Blocks[0].Id] = unicode }));
        var roundtrip = DocumentJson.Deserialize(DocumentJson.Serialize(edited));
        Assert.Equal(unicode, roundtrip.Blocks[0].Text); Assert.Equal(source.Blocks[0].Id, roundtrip.Blocks[0].Id);
        Assert.Equal(source.Speakers[0].Id, roundtrip.Speakers[0].Id); Assert.True(roundtrip.Blocks[0].ManuallyEdited);
    }

    [Fact] public void Editing_invalidates_only_affected_timing_and_keeps_overlap_representable()
    {
        var source = SyntheticFixture.Create(Guid.NewGuid(), 0);
        source = source with { Blocks = source.Blocks.Select((b, i) => b with { Timing = new TimeRange(1_000_001 + i, 2_000_009 + i) }).ToImmutableArray() };
        DocumentRules.Validate(source); // overlapping half-open intervals are valid
        var edited = TranscriptEdits.Apply(source, new(new Dictionary<Guid, string>(), new Dictionary<Guid, string> { [source.Blocks[0].Id] = "Inserted untimed words" }));
        Assert.Null(edited.Blocks[0].Timing); Assert.Equal(source.Blocks[1].Timing, edited.Blocks[1].Timing);
        Assert.Equal(source.Blocks[2].Timing, edited.Blocks[2].Timing);
        var renamed = TranscriptEdits.Apply(source, new(new Dictionary<Guid, string> { [source.Speakers[0].Id] = "New name" }, new Dictionary<Guid, string>()));
        Assert.Equal(source.Blocks[0].Timing, renamed.Blocks[0].Timing);
    }

    [Theory][InlineData(-1, 2)][InlineData(1, 1)][InlineData(2, 1)]
    public void Invalid_intervals_are_rejected(long start, long end)
    {
        var source = SyntheticFixture.Create(Guid.NewGuid(), 0);
        Assert.Throws<InvalidDataException>(() => DocumentRules.Validate(source with { Blocks = [source.Blocks[0] with { Timing = new(start, end) }] }));
    }

    [Fact] public void Identity_references_limits_and_unicode_are_validated()
    {
        var source = SyntheticFixture.Create(Guid.NewGuid(), 0);
        Assert.Throws<InvalidDataException>(() => DocumentRules.Validate(source with { Blocks = [source.Blocks[0], source.Blocks[0]] }));
        Assert.Throws<InvalidDataException>(() => DocumentRules.Validate(source with { Blocks = [source.Blocks[0] with { SpeakerId = Guid.NewGuid() }] }));
        Assert.Throws<InvalidDataException>(() => DocumentRules.Validate(source with { Blocks = [source.Blocks[0] with { Text = new string('a', DocumentRules.MaxBlockLength + 1) }] }));
        Assert.Throws<InvalidDataException>(() => DocumentRules.Validate(source with { Title = "\ud800" }));
        Assert.Throws<InvalidDataException>(() => DocumentRules.Validate(source with { Provenance = new("model", "WhisperX", "fake") }));
        Assert.Throws<InvalidDataException>(() => TranscriptEdits.Apply(source, new(new Dictionary<Guid, string> { [Guid.NewGuid()] = "missing" }, new Dictionary<Guid, string>())));
    }

    [Fact] public void Model_provenance_and_word_evidence_are_accepted_validated_and_dropped_on_text_change()
    {
        var source = SyntheticFixture.Create(Guid.NewGuid(), 0) with { Provenance = Provenance.Model("whisperx 3.8.6") };
        ImmutableArray<Word> words = [new("Kumusta!", new TimeRange(1_000_000, 1_400_000), 0.91), new("Halimbawang", new TimeRange(1_450_000, 2_000_000), null), new("teksto", null, 0.2)];
        source = source with { Blocks = source.Blocks.SetItem(1, source.Blocks[1] with { Timing = new TimeRange(1_000_000, 3_000_000), Words = words }) };
        DocumentRules.Validate(source);
        var roundtrip = DocumentJson.Deserialize(DocumentJson.Serialize(source));
        Assert.Equal(words.ToArray(), roundtrip.Blocks[1].Words.ToArray()); Assert.True(roundtrip.Blocks[0].Words.IsEmpty); Assert.Empty(roundtrip.Blocks[0].WordsOrEmpty);
        Assert.True(roundtrip.Provenance.IsModel); Assert.Contains("MODEL OUTPUT", roundtrip.Provenance.Notice);
        // Older documents without a words property still load; words are optional, everything else stays required.
        var json = System.Text.Json.Nodes.JsonNode.Parse(DocumentJson.Serialize(source))!;
        json["blocks"]![1]!.AsObject().Remove("words"); Assert.True(DocumentJson.Deserialize(json.ToJsonString()).Blocks[1].Words.IsEmpty);
        Assert.Throws<InvalidDataException>(() => DocumentRules.Validate(source with { Provenance = new("model-inference", " ", "x") }));
        Assert.Throws<InvalidDataException>(() => DocumentRules.Validate(source with { Provenance = new("oracle", "x", "y") }));
        Assert.Throws<InvalidDataException>(() => DocumentRules.Validate(source with { Blocks = [source.Blocks[1] with { Timing = null }] })); // words on an untimed paragraph
        Assert.Throws<InvalidDataException>(() => DocumentRules.Validate(source with { Blocks = [source.Blocks[1] with { Words = [new("x", new TimeRange(5, 5))] }] }));
        Assert.Throws<InvalidDataException>(() => DocumentRules.Validate(source with { Blocks = [source.Blocks[1] with { Words = [new("x", null, double.NaN)] }] }));
        Assert.Throws<InvalidDataException>(() => DocumentRules.Validate(source with { Blocks = [source.Blocks[1] with { Words = [new(" ", null)] }] }));
        var edited = TranscriptEdits.Apply(source, new(new Dictionary<Guid, string>(), new Dictionary<Guid, string> { [source.Blocks[1].Id] = "changed" }));
        Assert.Null(edited.Blocks[1].Timing); Assert.True(edited.Blocks[1].Words.IsEmpty);
        var retimed = TranscriptEdits.Apply(source, EditBatch.None with { BlockTimings = new Dictionary<Guid, TimeRange?> { [source.Blocks[1].Id] = new TimeRange(0, 9_000_000) } });
        Assert.Equal(3, retimed.Blocks[1].WordsOrEmpty.Length); // re-anchoring the paragraph keeps its word evidence
        var cleared = TranscriptEdits.Apply(source, EditBatch.None with { BlockTimings = new Dictionary<Guid, TimeRange?> { [source.Blocks[1].Id] = null } });
        Assert.True(cleared.Blocks[1].Words.IsEmpty);
        var split = TranscriptEdits.Apply(source, EditBatch.None, [new SplitBlock(source.Blocks[1].Id, 9, Guid.NewGuid())]);
        Assert.True(split.Blocks[1].Words.IsEmpty); Assert.True(split.Blocks[2].Words.IsEmpty);
        var both = source with { Blocks = source.Blocks.SetItem(2, source.Blocks[2] with { Timing = new TimeRange(3_000_000, 4_000_000), Words = [new("No", new TimeRange(3_000_000, 3_100_000))] }) };
        var merged = TranscriptEdits.Apply(both, EditBatch.None, [new MergeWithNext(both.Blocks[1].Id)]);
        Assert.Equal(4, merged.Blocks[1].WordsOrEmpty.Length); Assert.Equal(new TimeRange(1_000_000, 4_000_000), merged.Blocks[1].Timing);
        var mergedUntimed = TranscriptEdits.Apply(source, EditBatch.None, [new MergeWithNext(source.Blocks[1].Id)]);
        Assert.True(mergedUntimed.Blocks[1].Words.IsEmpty);
    }

    [Fact] public void Frozen_export_preserves_unicode_provenance_and_never_mutates_source()
    {
        using var folder = new TestDirectory(); var source = SyntheticFixture.Create(Guid.NewGuid(), 17);
        var before = DocumentJson.Serialize(source); var text = TextExport.Render(source);
        var path = Path.Combine(folder.Root, "Unicode export.txt"); TextExport.WriteAtomic(path, text);
        Assert.Equal(text, new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)));
        Assert.Contains("SYNTHETIC DEMO", text); Assert.Contains("piña", text); Assert.Contains("👩🏽‍💻", text); Assert.Contains("Saved revision 17", text);
        Assert.Equal(before, DocumentJson.Serialize(source));
        Assert.Throws<IOException>(() => TextExport.WriteAtomic(path, "replacement")); Assert.Equal(text, File.ReadAllText(path));
        Assert.Contains("UNSAVED DRAFT", TextExport.Render(source, true));
        Assert.Single(Directory.GetFiles(folder.Root));
    }
}
