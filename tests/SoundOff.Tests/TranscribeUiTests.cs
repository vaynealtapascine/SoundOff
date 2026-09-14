using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SoundOff.Core;
using SoundOff.Desktop;
using SoundOff.Protocol;
using Xunit;

namespace SoundOff.Tests;

public sealed class TranscribeUiTests
{
    private sealed class Picker(string project, string media) : IProjectPicker
    {
        public Task<string?> CreateProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> OpenProjectAsync() => Task.FromResult<string?>(project);
        public Task<string?> ExportTextAsync(bool isDraft) => Task.FromResult<string?>(null);
        public Task<string?> PickMediaAsync() => Task.FromResult<string?>(media);
    }
    private static string Clip => Path.Combine(AppContext.BaseDirectory, "fixtures", "tts-english.wav");
    private static Button Button(MainWindow window, string name) => window.FindControl<Button>(name)!;
    private static void Click(MainWindow window, string name) => Button(window, name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    private static string Status(MainWindow window) => window.FindControl<TextBlock>("StatusText")!.Text ?? "";
    private static string Job(MainWindow window) => window.FindControl<TextBlock>("JobText")!.Text ?? "";
    private static TextBox[] Blocks(MainWindow window) => window.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("transcript")).ToArray();
    private static async Task Idle(MainWindow window, Func<bool>? until = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((!Button(window, "DemoButton").IsEnabled || (until is not null && !until())) && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(Button(window, "DemoButton").IsEnabled, "UI operation did not become idle.");
    }
    private static async Task JobDone(MainWindow window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!Button(window, "ImportMediaButton").IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(Button(window, "ImportMediaButton").IsEnabled, "Job did not finish: " + Job(window));
    }

    [AvaloniaFact] public async Task Import_prepare_transcribe_auto_applies_into_an_empty_project_and_later_results_need_explicit_apply()
    {
        using var folder = new TestDirectory(); var mode = "inf-prepare-ok";
        var runtime = new InferenceRuntime(typeof(TranscribeUiTests).Assembly.Location, typeof(TranscribeUiTests).Assembly.Location, Path.Combine(folder.Root, "models"), Path.Combine(folder.Root, "runtime.json"));
        var client = new InferenceWorkerClient(() => ProtocolTests.Helper(mode), runtime, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2));
        var window = new MainWindow(new Picker(folder.Project, Clip), folder.Settings, null, client); window.Show();
        try
        {
            Assert.False(Button(window, "TranscribeButton").IsEnabled); Assert.True(Button(window, "PreparePackButton").IsEnabled);
            Assert.Contains("not prepared yet", window.FindControl<TextBlock>("RuntimeText")!.Text);
            Click(window, "ImportMediaButton"); await Idle(window); // creates the project first, then copies the clip
            Assert.Contains("Imported tts-english.wav", Status(window)); Assert.Contains("Recording: tts-english.wav", window.FindControl<TextBlock>("MediaText")!.Text);
            Assert.True(File.Exists(folder.Project)); Assert.False(Button(window, "TranscribeButton").IsEnabled);
            Click(window, "PreparePackButton"); await JobDone(window);
            Assert.StartsWith("Model pack ready: small with en, tl alignment", Job(window)); Assert.True(runtime.IsPackReady("small"));
            Assert.True(Button(window, "TranscribeButton").IsEnabled); Assert.Contains("model pack prepared", window.FindControl<TextBlock>("RuntimeText")!.Text);
            mode = "inf-ok"; Click(window, "TranscribeButton"); await JobDone(window);
            Assert.Contains("The result is now the transcript (revision 1)", Job(window));
            Assert.Equal(2, Blocks(window).Length); Assert.StartsWith("Hello. This is a synthetic", Blocks(window)[0].Text);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? "").Contains("MODEL OUTPUT"));
            Assert.Contains(window.FindControl<StackPanel>("RunHost")!.Children.OfType<Grid>().Select(g => g.Children.OfType<TextBlock>().Single().Text), t => t!.Contains("completed"));
            Assert.False(Button(window, "ApplyResultButton").IsEnabled);
            // A second run over a non-empty document is kept pending until applied explicitly.
            Click(window, "TranscribeButton"); await JobDone(window);
            Assert.Contains("The current transcript was kept", Job(window)); Assert.True(Button(window, "ApplyResultButton").IsEnabled);
            Assert.Contains("Saved · revision 1", Status(window));
            Click(window, "ApplyResultButton");
            var dialog = Assert.Single(window.OwnedWindows); Assert.Equal("Replace the transcript?", dialog.Title);
            dialog.GetVisualDescendants().OfType<Button>().Single(b => !b.IsCancel).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await Idle(window); Assert.Contains("Applied model result", Status(window)); Assert.Contains("revision 2", Status(window));
            Assert.False(Button(window, "ApplyResultButton").IsEnabled);
            Assert.Equal(2, window.FindControl<StackPanel>("RunHost")!.Children.Count);
            Assert.Contains("import-inference:", window.FindControl<StackPanel>("HistoryHost")!.Children.OfType<Grid>().First().Children.OfType<TextBlock>().Single().Text);
        }
        finally { foreach (var owned in window.OwnedWindows.ToArray()) owned.Close(false); window.Close(); }
        using var store = ProjectStore.Open(folder.Project);
        Assert.Equal(2, store.Read().Revision); Assert.True(store.Read().Provenance.IsModel); Assert.Equal(2, store.Runs().Count); Assert.All(store.Runs(), r => Assert.Equal("completed", r.Status));
        Assert.True(File.Exists(Path.Combine(store.MediaDirectory, store.Runs()[0].ArtifactRelativePath!)));
    }

    [AvaloniaFact] public async Task Cancelled_and_failed_runs_are_recorded_and_leave_the_transcript_untouched()
    {
        using var folder = new TestDirectory(); var mode = "inf-cancel-honoured";
        var runtime = new InferenceRuntime(typeof(TranscribeUiTests).Assembly.Location, typeof(TranscribeUiTests).Assembly.Location, Path.Combine(folder.Root, "models"), Path.Combine(folder.Root, "runtime.json"));
        Directory.CreateDirectory(Path.Combine(runtime.ModelsDir, "packs")); File.WriteAllText(runtime.PackManifestPath("small"), "{}");
        var client = new InferenceWorkerClient(() => ProtocolTests.Helper(mode), runtime, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2));
        var window = new MainWindow(new Picker(folder.Project, Clip), folder.Settings, null, client); window.Show();
        try
        {
            Click(window, "ImportMediaButton"); await Idle(window);
            Click(window, "TranscribeButton");
            Assert.False(Button(window, "TranscribeButton").IsEnabled); Assert.True(Button(window, "CancelRunButton").IsEnabled);
            await Task.Delay(300); Click(window, "CancelRunButton"); await JobDone(window);
            Assert.StartsWith("Stopped.", Job(window));
            mode = "inf-failed"; Click(window, "TranscribeButton"); await JobDone(window);
            Assert.Contains("model files are missing", Job(window));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "No transcript loaded");
        }
        finally { window.Close(); }
        using var store = ProjectStore.Open(folder.Project);
        Assert.Equal(["failed", "cancelled"], store.Runs().Select(r => r.Status).ToArray()); Assert.Equal(0, store.Read().Revision);
        Assert.Empty(Directory.GetFiles(Path.Combine(store.MediaDirectory, "runs"), "*.json"));
    }
}
