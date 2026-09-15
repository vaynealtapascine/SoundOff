using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoundOff.Core;

// App-owned schema. Block-level identities, no engine response types or character offsets.
public sealed record Provenance([property: JsonRequired] string Kind, [property: JsonRequired] string Provider,
    [property: JsonRequired] string Notice)
{
    public const string SyntheticKind = "synthetic-fixture";
    public const string EmptyKind = "empty";
    public const string ModelKind = "model-inference";
    public static readonly Provenance Synthetic = new(SyntheticKind, "soundoff-demo-v1",
        "SYNTHETIC DEMO — authored fixture, not a recording or model output. Any timing is synthetic, not measured.");
    // Retain readability of schema-1 projects made by the interrupted fixture implementation.
    internal static readonly Provenance LegacySynthetic = new(SyntheticKind, "soundoff-demo-v1",
        "SYNTHETIC DEMO — authored fixture, not a recording or model output. All timing is unknown.");
    public static readonly Provenance Empty = new(EmptyKind, "none", "No transcript has been loaded.");
    // Real local model output. Provider names the engine and exact version; the notice keeps the estimate status visible.
    public static Provenance Model(string provider) => new(ModelKind, provider,
        "MODEL OUTPUT — proposed by a local " + provider + " run. Text, timing and speaker labels are machine estimates that need review; timing is measured by alignment, not verified.");
    public bool IsModel => Kind == ModelKind;
}

// Exact integer microseconds; half-open interval. Null means unknown, never zero.
public sealed record TimeRange([property: JsonRequired] long StartMicroseconds, [property: JsonRequired] long EndMicroseconds);
public sealed record Speaker([property: JsonRequired] Guid Id, [property: JsonRequired] string Name);
// Word-level evidence from alignment. Timing null means the aligner could not place the word; Score is the aligner's
// own number where it gave one and is not a calibrated probability that the word is right.
public sealed record Word([property: JsonRequired] string Text, [property: JsonRequired] TimeRange? Timing, double? Score = null);
public sealed record TranscriptBlock([property: JsonRequired] Guid Id, [property: JsonRequired] Guid SpeakerId,
    [property: JsonRequired] string Text, [property: JsonRequired] TimeRange? Timing, bool ManuallyEdited = false,
    ImmutableArray<Word> Words = default)
{
    private readonly ImmutableArray<Word> words = Words.IsDefault ? ImmutableArray<Word>.Empty : Words;
    // Optional in JSON (older documents omit it); a default array from construction or a with-expression becomes empty.
    public ImmutableArray<Word> Words { get => words; init => words = value.IsDefault ? ImmutableArray<Word>.Empty : value; }
    public ImmutableArray<Word> WordsOrEmpty => words;
}
public sealed record Transcript([property: JsonRequired] Guid ProjectId, [property: JsonRequired] string Title,
    [property: JsonRequired] long Revision, [property: JsonRequired] Provenance Provenance,
    [property: JsonRequired] ImmutableArray<Speaker> Speakers, [property: JsonRequired] ImmutableArray<TranscriptBlock> Blocks)
{
    public static Transcript CreateEmpty() => new(Guid.NewGuid(), "Untitled fixture project", 0,
        Provenance.Empty, [], []);
}

public static class DocumentRules
{
    // Sized for a multi-hour, many-speaker recording: turns, not words, are blocks.
    public const int MaxBlocks = 20_000;
    public const int MaxSpeakers = 64;
    public const int MaxWordsPerBlock = 4_096;
    public const int MaxBlockLength = 16_384; // UTF-16 code units; no positional anchors are exposed.
    public const int MaxSnapshotBytes = 64 * 1024 * 1024;
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);

    public static void Validate(Transcript document)
    {
        if (document.ProjectId == Guid.Empty || document.Revision < 0) Fail("Invalid project identity/revision.");
        Text(document.Title, 200, false);
        var provenance = document.Provenance;
        if (provenance is null) Fail("Missing provenance.");
        if (provenance != Provenance.Synthetic && provenance != Provenance.LegacySynthetic && provenance != Provenance.Empty)
        {
            if (provenance.Kind != Provenance.ModelKind) Fail("Unsupported provenance kind; only the synthetic fixture, empty and model-inference are known.");
            Text(provenance.Provider, 200, false); Text(provenance.Notice, 1000, false);
        }
        if (document.Speakers.IsDefault || document.Blocks.IsDefault || document.Speakers.Length > MaxSpeakers || document.Blocks.Length > MaxBlocks)
            Fail("Document exceeds the editor limits.");
        var ids = new HashSet<Guid> { document.ProjectId };
        var speakers = new HashSet<Guid>();
        foreach (var speaker in document.Speakers)
        {
            if (speaker is null || speaker.Id == Guid.Empty || !ids.Add(speaker.Id)) Fail("Duplicate/empty speaker ID.");
            speakers.Add(speaker.Id);
            Text(speaker.Name, 100, false);
        }
        foreach (var block in document.Blocks)
        {
            if (block is null || block.Id == Guid.Empty || !ids.Add(block.Id) || !speakers.Contains(block.SpeakerId))
                Fail("Invalid block identity or speaker reference.");
            Text(block.Text, MaxBlockLength, true);
            Interval(block.Timing);
            if (!block.Words.IsDefault)
            {
                if (block.Words.Length > MaxWordsPerBlock) Fail("A paragraph carries too many words.");
                if (block.Words.Length > 0 && block.Timing is null) Fail("Word timing cannot exist on an untimed paragraph.");
                foreach (var word in block.Words)
                {
                    if (word is null) Fail("Missing word.");
                    Text(word.Text, 200, false); Interval(word.Timing);
                    if (word.Score is { } score && (double.IsNaN(score) || double.IsInfinity(score))) Fail("Word score must be a finite number.");
                }
            }
        }
        if (document.Provenance == Provenance.Empty && (document.Blocks.Length != 0 || document.Speakers.Length != 0))
            Fail("An empty document cannot contain transcript data.");
        if (document.Provenance == Provenance.LegacySynthetic && document.Blocks.Any(b => b.Timing is not null))
            Fail("The legacy fixture declares unknown timing; it cannot contain timed blocks.");
    }

    private static void Interval(TimeRange? time)
    {
        if (time is not null && (time.StartMicroseconds < 0 || time.EndMicroseconds <= time.StartMicroseconds || time.EndMicroseconds > TimeText.MaxMicroseconds))
            Fail("Timing must be a positive half-open microsecond interval within 1000 hours.");
    }

    // General text rule shared by document fields and the desktop's own small JSON files.
    public static void Text(string? text, int limit, bool allowEmpty)
    {
        if (text is null || text.Length > limit || (!allowEmpty && string.IsNullOrWhiteSpace(text)) || text.Contains('\0'))
            Fail("Text is empty, too long, or contains a null character.");
        try { StrictUtf8.GetByteCount(text); }
        catch (System.Text.EncoderFallbackException) { Fail("Text contains an unpaired Unicode surrogate."); }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string message) => throw new InvalidDataException(message);
}

public static class DocumentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };
    public static string Serialize(Transcript document)
    {
        DocumentRules.Validate(document);
        var json = JsonSerializer.Serialize(document, Options);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > DocumentRules.MaxSnapshotBytes)
            throw new InvalidDataException("Project snapshot exceeds the size limit.");
        return json;
    }
    public static Transcript Deserialize(string json)
    {
        var document = ReadStrict<Transcript>(json, DocumentRules.MaxSnapshotBytes);
        DocumentRules.Validate(document);
        return document;
    }

    public static T ReadStrict<T>(string json, int maxBytes)
    {
        try
        {
            if (new System.Text.UTF8Encoding(false, true).GetByteCount(json) > maxBytes)
                throw new InvalidDataException("JSON exceeds the size limit.");
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = Options.MaxDepth });
            CheckProperties(parsed.RootElement);
            return parsed.RootElement.Deserialize<T>(Options) ?? throw new InvalidDataException("Missing JSON value.");
        }
        catch (Exception e) when (e is JsonException or System.Text.EncoderFallbackException)
        { throw new InvalidDataException("Invalid JSON schema or Unicode.", e); }
    }

    private static void CheckProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                string name;
                try { name = property.Name; }
                catch (InvalidOperationException error)
                { throw new InvalidDataException("JSON property name contains invalid Unicode.", error); }
                if (!names.Add(name)) throw new InvalidDataException("Duplicate JSON property.");
                CheckProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckProperties(item);
    }
}

public sealed class RevisionConflictException(long expected, long actual)
    : InvalidOperationException($"Revision conflict: expected {expected}, found {actual}. Your draft was not applied.");

// A draft: renamed speakers, replaced paragraph texts and reassigned paragraph speakers, all keyed by stable ID.
// Reassigning a speaker is a speaker correction, not a text change: timing and the manual-text flag are untouched.
// BlockTimings are explicit manual anchors (or null to clear); they apply after any text change, so a user who edits text
// AND enters timing in the same draft keeps the timing they typed. Manually entered timing is still synthetic, never measured.
public sealed record EditBatch(IReadOnlyDictionary<Guid, string> SpeakerNames, IReadOnlyDictionary<Guid, string> BlockTexts,
    IReadOnlyDictionary<Guid, Guid>? BlockSpeakers = null, string? Title = null, IReadOnlyDictionary<Guid, TimeRange?>? BlockTimings = null)
{
    public static EditBatch None { get; } = new(ImmutableDictionary<Guid, string>.Empty, ImmutableDictionary<Guid, string>.Empty);
    public bool IsEmpty => SpeakerNames.Count == 0 && BlockTexts.Count == 0 && (BlockSpeakers?.Count ?? 0) == 0 && Title is null && (BlockTimings?.Count ?? 0) == 0;
    internal void RequireKnownTargets(Transcript source)
    {
        if (SpeakerNames.Keys.Any(id => !source.Speakers.Any(s => s.Id == id)) ||
            BlockTexts.Keys.Any(id => !source.Blocks.Any(b => b.Id == id)) ||
            BlockSpeakers is not null && (BlockSpeakers.Keys.Any(id => !source.Blocks.Any(b => b.Id == id)) ||
                BlockSpeakers.Values.Any(id => !source.Speakers.Any(s => s.Id == id))) ||
            BlockTimings is not null && BlockTimings.Keys.Any(id => !source.Blocks.Any(b => b.Id == id)))
            throw new InvalidDataException("The edit targets a missing stable ID.");
    }
    internal Guid SpeakerOf(TranscriptBlock block) => BlockSpeakers is not null && BlockSpeakers.TryGetValue(block.Id, out var id) ? id : block.SpeakerId;
}

public static class TranscriptEdits
{
    public static Transcript Apply(Transcript source, EditBatch edits)
    {
        edits.RequireKnownTargets(source);
        var result = source with
        {
            Title = edits.Title ?? source.Title,
            Speakers = source.Speakers.Select(s => edits.SpeakerNames.TryGetValue(s.Id, out var name) ? s with { Name = name } : s).ToImmutableArray(),
            Blocks = source.Blocks.Select(b =>
            {
                var block = b with { SpeakerId = edits.SpeakerOf(b) };
                // Changed text no longer matches aligned words; both block timing and word evidence are dropped.
                if (edits.BlockTexts.TryGetValue(b.Id, out var text) && text != b.Text) block = block with { Text = text, Timing = null, Words = default, ManuallyEdited = true };
                // A wider paragraph can retain independent alignment evidence, but moving its bounds past
                // an aligned word cannot leave that word seeking outside the paragraph. Never shift word times.
                if (edits.BlockTimings is not null && edits.BlockTimings.TryGetValue(b.Id, out var timing))
                    block = block with { Timing = timing, Words = timing is not null && block.Words.All(w => w.Timing is null ||
                        w.Timing.StartMicroseconds >= timing.StartMicroseconds && w.Timing.EndMicroseconds <= timing.EndMicroseconds) ? block.Words : default };
                return block;
            }).ToImmutableArray()
        };
        DocumentRules.Validate(result);
        return result;
    }

    // The draft is applied first, then each structural operation in order; every step is validated.
    public static Transcript Apply(Transcript source, EditBatch edits, IReadOnlyList<DocumentOperation> operations)
    {
        var result = Apply(source, edits);
        foreach (var operation in operations) result = operation.ApplyTo(result);
        return result;
    }
}
