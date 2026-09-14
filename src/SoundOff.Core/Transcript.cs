using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoundOff.Core;

// App-owned schema. Block-level identities, no engine response types or character offsets.
public sealed record Provenance([property: JsonRequired] string Kind, [property: JsonRequired] string Provider,
    [property: JsonRequired] string Notice)
{
    public static readonly Provenance Synthetic = new("synthetic-fixture", "soundoff-demo-v1",
        "SYNTHETIC DEMO — authored fixture, not a recording or model output. Any timing is synthetic, not measured.");
    // Retain readability of schema-1 projects made by the interrupted fixture implementation.
    internal static readonly Provenance LegacySynthetic = new("synthetic-fixture", "soundoff-demo-v1",
        "SYNTHETIC DEMO — authored fixture, not a recording or model output. All timing is unknown.");
    public static readonly Provenance Empty = new("empty", "none", "No transcript has been loaded.");
}

// Exact integer microseconds; half-open interval. Null means unknown, never zero.
public sealed record TimeRange([property: JsonRequired] long StartMicroseconds, [property: JsonRequired] long EndMicroseconds);
public sealed record Speaker([property: JsonRequired] Guid Id, [property: JsonRequired] string Name);
public sealed record TranscriptBlock([property: JsonRequired] Guid Id, [property: JsonRequired] Guid SpeakerId,
    [property: JsonRequired] string Text, [property: JsonRequired] TimeRange? Timing, bool ManuallyEdited = false);
public sealed record Transcript([property: JsonRequired] Guid ProjectId, [property: JsonRequired] string Title,
    [property: JsonRequired] long Revision, [property: JsonRequired] Provenance Provenance,
    [property: JsonRequired] ImmutableArray<Speaker> Speakers, [property: JsonRequired] ImmutableArray<TranscriptBlock> Blocks)
{
    public static Transcript CreateEmpty() => new(Guid.NewGuid(), "Untitled fixture project", 0,
        Provenance.Empty, [], []);
}

public static class DocumentRules
{
    public const int MaxBlocks = 128;
    public const int MaxBlockLength = 16_384; // UTF-16 code units; no positional anchors are exposed.
    public const int MaxSnapshotBytes = 8 * 1024 * 1024;
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);

    public static void Validate(Transcript document)
    {
        if (document.ProjectId == Guid.Empty || document.Revision < 0) Fail("Invalid project identity/revision.");
        Text(document.Title, 200, false);
        if (document.Provenance != Provenance.Synthetic && document.Provenance != Provenance.LegacySynthetic && document.Provenance != Provenance.Empty)
            Fail("Unsupported provenance; v0.1 accepts only its explicit synthetic fixture.");
        if (document.Speakers.IsDefault || document.Blocks.IsDefault || document.Speakers.Length > 32 || document.Blocks.Length > MaxBlocks)
            Fail("Document exceeds the v0.1 editor limits.");
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
            if (block.Timing is { } time && (time.StartMicroseconds < 0 || time.EndMicroseconds <= time.StartMicroseconds))
                Fail("Timing must be a positive half-open microsecond interval.");
        }
        if (document.Provenance == Provenance.Empty && (document.Blocks.Length != 0 || document.Speakers.Length != 0))
            Fail("An empty document cannot contain transcript data.");
        if (document.Provenance == Provenance.LegacySynthetic && document.Blocks.Any(b => b.Timing is not null))
            Fail("The legacy fixture declares unknown timing; it cannot contain timed blocks.");
    }

    internal static void Text(string? text, int limit, bool allowEmpty)
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
public sealed record EditBatch(IReadOnlyDictionary<Guid, string> SpeakerNames, IReadOnlyDictionary<Guid, string> BlockTexts,
    IReadOnlyDictionary<Guid, Guid>? BlockSpeakers = null)
{
    public static EditBatch None { get; } = new(ImmutableDictionary<Guid, string>.Empty, ImmutableDictionary<Guid, string>.Empty);
    public bool IsEmpty => SpeakerNames.Count == 0 && BlockTexts.Count == 0 && (BlockSpeakers?.Count ?? 0) == 0;
    internal void RequireKnownTargets(Transcript source)
    {
        if (SpeakerNames.Keys.Any(id => !source.Speakers.Any(s => s.Id == id)) ||
            BlockTexts.Keys.Any(id => !source.Blocks.Any(b => b.Id == id)) ||
            BlockSpeakers is not null && (BlockSpeakers.Keys.Any(id => !source.Blocks.Any(b => b.Id == id)) ||
                BlockSpeakers.Values.Any(id => !source.Speakers.Any(s => s.Id == id))))
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
            Speakers = source.Speakers.Select(s => edits.SpeakerNames.TryGetValue(s.Id, out var name) ? s with { Name = name } : s).ToImmutableArray(),
            Blocks = source.Blocks.Select(b =>
            {
                var block = b with { SpeakerId = edits.SpeakerOf(b) };
                return edits.BlockTexts.TryGetValue(b.Id, out var text) && text != b.Text
                    ? block with { Text = text, Timing = null, ManuallyEdited = true } : block;
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
