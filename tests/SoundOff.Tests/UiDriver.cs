using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SoundOff.Tests;

// Drives real window controls the way a click would, whether an action lives on a button or in a menu.
internal static class UiDriver
{
    public static void Press(Control control) =>
        control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));
    public static void Click(Window window, string name) => Press(window.FindControl<Control>(name)!);
    public static Control Named(Window window, string name) => window.FindControl<Control>(name)!;
    public static MenuItem Item(Window window, string name) => window.FindControl<MenuItem>(name)!;

    // Document actions: visible buttons plus the items in each paragraph's "⋯" menu, matched by label or accessible name.
    public static Control[] Actions(Window window, string label)
    {
        var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
        var menuItems = buttons.Select(b => b.Flyout).OfType<MenuFlyout>().SelectMany(f => f.Items.OfType<MenuItem>());
        return buttons.Cast<Control>().Concat(menuItems)
            .Where(c => c.Classes.Contains("structural") && (Label(c) == label || (AutomationProperties.GetName(c) ?? "").StartsWith(label, StringComparison.Ordinal)))
            .ToArray();
    }
    private static string? Label(Control control) => control switch { MenuItem item => item.Header as string, Button button => button.Content as string, _ => null };
}
