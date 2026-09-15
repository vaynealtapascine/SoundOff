using System.Text;

namespace SoundOff.Core;

// SRT from a frozen snapshot. One cue per timed paragraph; untimed paragraphs are refused unless explicitly excluded,
// never given invented timing. Timestamps round outward (start floor, end ceiling) so no cue shrinks or becomes empty.
public enum OverlapPolicy
{
    Refuse,   // any two cues sharing an instant is an error
    Combine   // compatibility layout: overlapping cues merge into one cue spanning their union, one paragraph per line group
}

public sealed record SubtitleOptions(bool IncludeSpeakerLabels = true, int MaxLineLength = 42, bool ExcludeUntimed = false,
    OverlapPolicy Overlap = OverlapPolicy.Refuse);

public sealed record SubtitleResult(string Srt, int CueCount, int CombinedOverlaps, IReadOnlyList<int> ExcludedUntimedParagraphs, int SkippedEmpty);

public static class SubtitleExport
{
    public static SubtitleResult RenderSrt(Transcript snapshot, SubtitleOptions? options = null)
    {
        options ??= new SubtitleOptions();
        DocumentRules.Validate(snapshot);
        if (options.MaxLineLength < 8 || options.MaxLineLength > 200) throw new ArgumentOutOfRangeException(nameof(options), "Line length must be between 8 and 200 code units.");
        var names = snapshot.Speakers.ToDictionary(s => s.Id, s => s.Name);
        var untimed = new List<int>(); var cues = new List<(long Start, long End, List<string> Lines)>(); var skippedEmpty = 0;
        for (var i = 0; i < snapshot.Blocks.Length; i++)
        {
            var block = snapshot.Blocks[i];
            if (block.Timing is null) { untimed.Add(i + 1); continue; }
            if (block.Text.Trim().Length == 0) { skippedEmpty++; continue; }
            var text = options.IncludeSpeakerLabels ? names[block.SpeakerId] + ": " + block.Text : block.Text;
            cues.Add((block.Timing.StartMicroseconds, block.Timing.EndMicroseconds, Wrap(text, options.MaxLineLength)));
        }
        if (untimed.Count > 0 && !options.ExcludeUntimed)
            throw new InvalidDataException("Paragraph(s) " + string.Join(", ", untimed) + " have no timing. Give them timing or export with untimed paragraphs explicitly excluded.");
        cues.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        var combined = 0;
        for (var i = 1; i < cues.Count; i++)
        {
            // The SRT's outward-rounded millisecond intervals, not the original microseconds, must obey the policy.
            if (cues[i].Start / 1000 >= cues[i - 1].End / 1000 + (cues[i - 1].End % 1000 == 0 ? 0 : 1)) continue;
            if (options.Overlap == OverlapPolicy.Refuse)
                throw new InvalidDataException("Overlapping paragraphs cannot be written as separate SRT cues. Export with the combined-overlap layout instead.");
            var previous = cues[i - 1]; var current = cues[i];
            var merged = (previous.Start, Math.Max(previous.End, current.End), previous.Lines.Concat(current.Lines).ToList());
            cues[i - 1] = merged; cues.RemoveAt(i); i--; combined++;
        }
        var srt = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            var (start, end, lines) = cues[i];
            srt.Append(i + 1).Append('\n').Append(Timestamp(start, roundUp: false)).Append(" --> ").Append(Timestamp(end, roundUp: true)).Append('\n');
            // A blank line terminates an SRT cue; preserve text and nonblank line breaks, not empty author lines.
            foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l))) srt.Append(line).Append('\n');
            srt.Append('\n');
        }
        return new SubtitleResult(srt.ToString(), cues.Count, combined, untimed, skippedEmpty);
    }

    // HH:MM:SS,mmm. Ceiling for cue ends so rounding never produces end <= start.
    public static string Timestamp(long microseconds, bool roundUp)
    {
        var ms = roundUp ? (microseconds + 999) / 1000 : microseconds / 1000;
        var hours = ms / 3_600_000; ms %= 3_600_000; var minutes = ms / 60_000; ms %= 60_000; var seconds = ms / 1000; ms %= 1000;
        return $"{hours:00}:{minutes:00}:{seconds:00},{ms:000}";
    }

    // Soft word wrap on whitespace, honouring the author's own line breaks; an over-long word stays whole (never split mid-grapheme).
    public static List<string> Wrap(string text, int maxLineLength)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var current = new StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.Length > 0 && current.Length + 1 + word.Length > maxLineLength) { lines.Add(current.ToString()); current.Clear(); }
                if (current.Length > 0) current.Append(' ');
                current.Append(word);
            }
            if (current.Length > 0 || paragraph.Length == 0 && lines.Count > 0) lines.Add(current.ToString());
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}
