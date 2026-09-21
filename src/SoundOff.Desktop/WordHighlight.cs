using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace SoundOff.Desktop;

// The reading highlight, drawn BEHIND a paragraph's own TextBox from that box's text layout. The box stays the
// only place the text lives, so playback never touches the caret, the selection, the draft or undo history —
// the same guarantee the word ribbon gave, with the highlight where the eye already is.
public sealed class WordHighlight : Control
{
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<WordHighlight, IBrush?>(nameof(Fill));

    private readonly TextBox source;
    private int start, length;

    static WordHighlight() => AffectsRender<WordHighlight>(FillProperty);

    public WordHighlight(TextBox source)
    {
        this.source = source;
        IsHitTestVisible = false;   // clicks belong to the text box behind nothing
        // Re-wrapping or re-typing moves every rectangle, and the layout is the box's, not ours.
        source.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty || e.Property == BoundsProperty || e.Property == TextBox.FontSizeProperty)
                InvalidateVisual();
        };
    }

    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    // What is lit, in characters of the box's text. Zero length means nothing is.
    internal (int Start, int Length) Span => (start, length);

    // A zero length clears it. Offsets are character offsets into the box's current text.
    public void Show(int start, int length)
    {
        if (this.start == start && this.length == length) return;
        this.start = start; this.length = length;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (length <= 0 || Fill is null) return;
        var text = source.Text ?? "";
        if (start < 0 || start + length > text.Length) return;
        if (source.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault() is not { } presenter) return;
        if (presenter.TranslatePoint(default, this) is not { } origin) return;
        foreach (var rect in presenter.TextLayout.HitTestTextRange(start, length))
        {
            var box = new Rect(rect.X + origin.X - 1.5, rect.Y + origin.Y - 1, rect.Width + 3, rect.Height + 2);
            context.DrawRectangle(Fill, null, new RoundedRect(box, 3));
        }
    }
}
