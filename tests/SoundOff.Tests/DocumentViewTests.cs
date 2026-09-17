using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoundOff.Core;
using SoundOff.Desktop;
using Xunit;

namespace SoundOff.Tests;

public sealed class DocumentViewTests
{
    private sealed class Picker(string project, string output) : IProjectPicker
    {
        public Task<string?> CreateProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> ExportTextAsync(bool isDraft) => Task.FromResult<string?>(output);
    }

    [Fact] public void Document_export_keeps_order_unicode_and_notice_but_omits_speakers_and_timestamps()
    {
        var source = SyntheticFixture.Create(Guid.NewGuid(), 0);
        source = source with { Blocks = source.Blocks.SetItem(0, source.Blocks[0] with { Text = "Kumusta 👋\nSecond line", Timing = new TimeRange(1_000_000, 2_000_000) }) };
        var text = TextExport.Render(source, document: true);
        Assert.Contains(source.Provenance.Notice, text);
        Assert.Contains("Kumusta 👋\nSecond line", text);
        Assert.DoesNotContain("0:00:01", text);
        Assert.DoesNotContain(source.Speakers[0].Name + ":", text);
        Assert.Contains("0:00:01", TextExport.Render(source));
        Assert.NotNull(source.Blocks[0].Timing);
    }

    [AvaloniaFact] public async Task Switching_views_keeps_draft_and_selection_and_document_export_uses_it()
    {
        using var folder = new TestDirectory();
        using (var store = ProjectStore.Create(folder.Project, SyntheticFixture.Create(Guid.NewGuid(), 0))) { }
        var output = Path.Combine(folder.Root, "document.txt");
        var window = new MainWindow(new Picker(folder.Project, output), folder.Settings);
        window.Show();
        try
        {
            UiDriver.Click(window, "OpenProjectItem");
            await Task.Delay(50); Dispatcher.UIThread.RunJobs();
            var choice = window.FindControl<ComboBox>("ViewChoice")!;
            Assert.Equal(0, choice.SelectedIndex);
            Assert.Equal("Timings", Assert.IsType<ComboBoxItem>(choice.Items[1]).Content);
            var input = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("transcript"));
            input.Text = "Edited words 👋"; input.SelectionStart = 2; input.SelectionEnd = 8;
            choice.SelectedIndex = 1; choice.SelectedIndex = 0;
            Assert.Equal("Edited words 👋", input.Text); Assert.Equal(2, input.SelectionStart); Assert.Equal(8, input.SelectionEnd);
            choice.SelectedIndex = 1;
            UiDriver.Click(window, "CollapseSectionsButton"); Assert.False(input.IsVisible);
            var section = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(t => t.Classes.Contains("section-toggle"));
            Assert.Equal("Edited words 👋", Assert.IsType<TextBlock>(section.Content).Text);
            var timing = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("timing"));
            Assert.False(timing.IsEffectivelyVisible);
            section.BringIntoView(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var clickPoint = section.TranslatePoint(new Point(section.Bounds.Width / 2, section.Bounds.Height / 2), window)!.Value;
            window.MouseDown(clickPoint, MouseButton.Left);
            window.MouseUp(clickPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(input.IsVisible);
            Assert.True(timing.IsEffectivelyVisible);
            var speaker = window.GetVisualDescendants().OfType<ComboBox>().First(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Speaker for paragraph 1");
            Assert.True(speaker.IsEffectivelyVisible);
            window.MouseMove(new Point(0, 0));
            foreach (var field in new Avalonia.Controls.Primitives.TemplatedControl[] { input, timing, speaker })
            {
                Assert.Equal(Avalonia.Media.Colors.Transparent, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(field.Background).Color);
                Assert.Equal(Avalonia.Media.Colors.Transparent, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(field.BorderBrush).Color);
            }
            timing.Focus(); Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(Avalonia.Media.Colors.Transparent, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(timing.BorderBrush).Color);
            var presenter = section.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            Assert.Equal(Avalonia.Media.Colors.Transparent, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(presenter.Background).Color);
            UiDriver.Click(window, "CollapseSectionsButton"); Assert.False(input.IsVisible);
            choice.SelectedIndex = 0; Assert.True(input.IsVisible);
            choice.SelectedIndex = 1; Assert.False(input.IsVisible);
            UiDriver.Click(window, "ExpandSectionsButton"); Assert.True(input.IsVisible);
            UiDriver.Click(window, "CollapseSectionsButton");
            window.FindControl<TextBox>("FindInput")!.Text = "Edited words";
            UiDriver.Click(window, "FindNextButton"); Assert.True(input.IsVisible);
            Assert.Equal("Edited words 👋", input.Text);
            choice.SelectedIndex = 0;
            Assert.True(window.FindControl<Button>("SaveButton")!.IsEnabled);
            UiDriver.Click(window, "ExportDocumentItem"); await Task.Delay(50);
            Assert.Contains("Edited words 👋", File.ReadAllText(output)); Assert.Contains("UNSAVED DRAFT", File.ReadAllText(output));
            UiDriver.Click(window, "SaveButton"); await Task.Delay(50);
            Assert.False(window.FindControl<Button>("SaveButton")!.IsEnabled);
        }
        finally { UiDriver.Discard(window); window.Close(); }
        using var reopened = ProjectStore.Open(folder.Project);
        Assert.Equal("Edited words 👋", reopened.Read().Blocks[0].Text);
    }
}
