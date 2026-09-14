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
        var frozen = snapshot with
        {
            Speakers = snapshot.Speakers.Select(s => draft.SpeakerNames.TryGetValue(s.Id, out var name) ? s with { Name = name } : s).ToImmutableArray(),
            Blocks = snapshot.Blocks.Select(b => (draft.BlockTexts.TryGetValue(b.Id, out var text) ? b with { Text = text } : b) with { SpeakerId = draft.SpeakerOf(b) }).ToImmutableArray()
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
            text.Append(names[block.SpeakerId]).Append(":\n").Append(block.Text).Append("\n\n");
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
