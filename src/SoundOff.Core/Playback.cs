namespace SoundOff.Core;

// Where the playhead is inside the document. WordIndex -1 means the paragraph is active but no word covers this instant
// (unaligned words, or a gap between them): the paragraph highlights, no word does. Timing is never invented to fill a gap.
public readonly record struct ActiveSpan(Guid BlockId, int WordIndex);

public enum SeekAvailability { Available, Unaligned, Untimed }

// Maps the authoritative playback clock onto the app-owned document. Pure and synchronous so it can be tested
// without an audio device, and so the UI never keeps a second, drifting idea of the position.
public static class PlaybackCursor
{
    // Half-open intervals: a paragraph is active for [start, end). Overlapping speakers therefore yield several spans,
    // in document order; a single-speaker projection is never forced here.
    public static IReadOnlyList<ActiveSpan> Locate(Transcript document, long positionMicroseconds)
    {
        var spans = new List<ActiveSpan>();
        if (positionMicroseconds < 0) return spans;
        foreach (var block in document.Blocks)
        {
            if (block.Timing is not { } timing) continue;
            if (positionMicroseconds < timing.StartMicroseconds || positionMicroseconds >= timing.EndMicroseconds) continue;
            spans.Add(new ActiveSpan(block.Id, WordAt(block, positionMicroseconds)));
        }
        return spans;
    }

    public static int WordAt(TranscriptBlock block, long positionMicroseconds)
    {
        var words = block.WordsOrEmpty;
        for (var i = 0; i < words.Length; i++)
            if (words[i].Timing is { } t && positionMicroseconds >= t.StartMicroseconds && positionMicroseconds < t.EndMicroseconds) return i;
        return -1;
    }

    // Seeking to a paragraph uses its own interval; an untimed paragraph offers no seek rather than a guessed one.
    public static (SeekAvailability Availability, long Target) SeekTarget(TranscriptBlock block) =>
        block.Timing is { } timing ? (SeekAvailability.Available, timing.StartMicroseconds) : (SeekAvailability.Untimed, 0);

    // Seeking to a word falls back to the paragraph when that word was never aligned, and reports which it used.
    public static (SeekAvailability Availability, long Target) SeekTarget(TranscriptBlock block, int wordIndex)
    {
        var words = block.WordsOrEmpty;
        if (wordIndex >= 0 && wordIndex < words.Length && words[wordIndex].Timing is { } word) return (SeekAvailability.Available, word.StartMicroseconds);
        return block.Timing is { } timing ? (SeekAvailability.Unaligned, timing.StartMicroseconds) : (SeekAvailability.Untimed, 0);
    }
}

public enum PlaybackStatus { Empty, Loading, Ready, Playing, Paused, Ended, Failed }

// One media file, one authoritative clock. Implementations own decoding and the output device; callers only read
// Position, never their own timer. Every member is used from the UI thread.
public interface IPlaybackEngine : IDisposable
{
    PlaybackStatus Status { get; }
    string? FailureReason { get; }
    long DurationMicroseconds { get; }
    long PositionMicroseconds { get; }
    double Volume { get; set; }
    Task LoadAsync(string path, CancellationToken cancellationToken);
    void Play();
    void Pause();
    void Seek(long positionMicroseconds);
    void Unload();
    event EventHandler? Changed;
}
