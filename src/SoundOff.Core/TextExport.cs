using System.Collections.Immutable;
using System.Text;

namespace SoundOff.Core;

public static class TextExport
{
    public static string Render(Transcript snapshot, bool isDraft = false)
    {
        DocumentRules.Validate(snapshot);
        return RenderUnchecked(snapshot, isDraft);
    }

    // Emergency text rescue permits blank speaker names, but never writes an invalid project.
    // It retains raw input (no normalization or invented replacement names).
    public static string RenderDraft(Transcript snapshot, EditBatch draft)
    {
        DocumentRules.Validate(snapshot);
        draft.RequireKnownTargets(snapshot);
        foreach (var name in draft.SpeakerNames.Values) DocumentRules.Text(name, 100, true);
        foreach (var text in draft.BlockTexts.Values) DocumentRules.Text(text, DocumentRules.MaxBlockLength, true);
        if (draft.Title is not null) DocumentRules.Text(draft.Title, 200, true);
        var frozen = snapshot with
        {
            Title = draft.Title ?? snapshot.Title,
            Speakers = snapshot.Speakers.Select(s => draft.SpeakerNames.TryGetValue(s.Id, out var name) ? s with { Name = name } : s).ToImmutableArray(),
            // An edited paragraph would lose its timing on save, so the draft rescue shows none for it either.
            Blocks = snapshot.Blocks.Select(b =>
            {
                var block = b with { SpeakerId = draft.SpeakerOf(b) };
                if (draft.BlockTexts.TryGetValue(b.Id, out var text) && text != b.Text) block = block with { Text = text, Timing = null };
                if (draft.BlockTimings is not null && draft.BlockTimings.TryGetValue(b.Id, out var timing)) block = block with { Timing = timing };
                return block;
            }).ToImmutableArray()
        };
        return RenderUnchecked(frozen, true);
    }

    private static string RenderUnchecked(Transcript snapshot, bool isDraft)
    {
        var text = new StringBuilder();
        text.Append(snapshot.Title).Append('\n').Append(snapshot.Provenance.Notice).Append('\n');
        text.Append(isDraft ? "UNSAVED DRAFT based on revision " : "Saved revision ").Append(snapshot.Revision).Append("\n\n");
        var names = snapshot.Speakers.ToDictionary(s => s.Id, s => s.Name);
        foreach (var block in snapshot.Blocks)
        {
            text.Append(names[block.SpeakerId]);
            // Timecodes appear only where an interval is actually stored; untimed paragraphs get no invented placeholder.
            if (block.Timing is { } timing)
                text.Append(" [").Append(TimeText.Format(timing.StartMicroseconds)).Append(" – ").Append(TimeText.Format(timing.EndMicroseconds)).Append(']');
            text.Append(":\n").Append(block.Text).Append("\n\n");
        }
        return text.ToString();
    }

    public static void WriteAtomic(string destination, string text, bool overwrite = false)
    {
        var path = Path.GetFullPath(destination);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(text);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            // Same-directory move; an existing export remains intact if staging fails.
            File.Move(temp, path, overwrite);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
