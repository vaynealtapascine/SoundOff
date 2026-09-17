using SoundOff.Core;

namespace SoundOff.Desktop;

public sealed partial class MainWindow
{
    private Task ChangeSpeakerAsync(Guid source, bool split) => GuardAsync(async () =>
    {
        var edits = DraftEdits();
        var preview = TranscriptEdits.Apply(snapshot!, edits);
        var dialog = new SpeakerOperationDialog(preview, source, split, dirty)
        {
            RequestedThemeVariant = RequestedThemeVariant
        };
        dialog.Classes.Set("reducedMotion", Classes.Contains("reducedMotion"));
        var operation = await dialog.ShowDialog<DocumentOperation?>(this);
        if (operation is null) return;
        lifetime.Token.ThrowIfCancellationRequested();
        snapshot = store!.Apply(snapshot!.Revision, edits, operation);
        Render(); SavedStatus();
    });
}
