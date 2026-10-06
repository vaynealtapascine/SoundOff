using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Layout;
using Avalonia.Media;
using SoundOff.Core;
using SoundOff.Protocol;

namespace SoundOff.Desktop;

// Media import, model-pack preparation and transcription jobs. A job runs beside the editor (editing stays available);
// its result becomes the document only when the document is still empty, or when the user applies it explicitly.
public sealed partial class MainWindow
{
    private const string PackModel = "small";
    private InferenceWorkerClient inference = null!;
    private TextBlock runtimeText = null!, mediaText = null!, jobText = null!;
    private Button importMedia = null!, setupRuntime = null!, preparePack = null!, transcribe = null!, cancelRun = null!, applyResult = null!;
    private ComboBox languageChoice = null!, deviceChoice = null!;
    private StackPanel runHost = null!;
    private ProgressBar jobProgress = null!;
    private Expander runsExpander = null!;
    private Control transcribeCard = null!, mediaRow = null!, emptyTranscript = null!, dropOverlay = null!;
    private TextBlock mediaDetail = null!, emptyHint = null!, emptyJobText = null!;
    private Button emptyTranscribe = null!, emptyCancel = null!, emptyImport = null!;
    private ProgressBar emptyProgress = null!;
    private PathIcon mediaIcon = null!;
    private CancellationTokenSource? job;
    private Task? jobTask;
    private (string RunId, Transcript Proposal)? pendingResult;
    private int jobGeneration;
    private bool JobRunning => job is not null;
    // Set once the setup window has been opened, so coming back to SoundOff notices the runtime without a restart.
    private bool awaitingSetup;

    private void InitializeTranscribe(InferenceWorkerClient? client)
    {
        inference = client ?? new InferenceWorkerClient();
        runtimeText = this.FindControl<TextBlock>("RuntimeText")!; mediaText = this.FindControl<TextBlock>("MediaText")!; jobText = this.FindControl<TextBlock>("JobText")!;
        importMedia = this.FindControl<Button>("ImportMediaButton")!; preparePack = this.FindControl<Button>("PreparePackButton")!;
        setupRuntime = this.FindControl<Button>("SetupRuntimeButton")!;
        transcribe = this.FindControl<Button>("TranscribeButton")!; cancelRun = this.FindControl<Button>("CancelRunButton")!; applyResult = this.FindControl<Button>("ApplyResultButton")!;
        languageChoice = this.FindControl<ComboBox>("LanguageChoice")!; deviceChoice = this.FindControl<ComboBox>("DeviceChoice")!; runHost = this.FindControl<StackPanel>("RunHost")!;
        jobProgress = this.FindControl<ProgressBar>("JobProgressBar")!; runsExpander = this.FindControl<Expander>("RunsExpander")!;
        transcribeCard = this.FindControl<Control>("TranscribeCard")!;
        mediaRow = this.FindControl<Control>("MediaRow")!; mediaIcon = this.FindControl<PathIcon>("MediaIcon")!; mediaDetail = this.FindControl<TextBlock>("MediaDetail")!;
        emptyTranscript = this.FindControl<Control>("EmptyTranscript")!; emptyHint = this.FindControl<TextBlock>("EmptyHint")!;
        emptyJobText = this.FindControl<TextBlock>("EmptyJobText")!; emptyProgress = this.FindControl<ProgressBar>("EmptyProgress")!;
        emptyTranscribe = this.FindControl<Button>("EmptyTranscribeButton")!; emptyCancel = this.FindControl<Button>("EmptyCancelButton")!;
        emptyImport = this.FindControl<Button>("EmptyImportButton")!; dropOverlay = this.FindControl<Control>("DropOverlay")!;
        emptyTranscribe.Click += (_, _) => StartJob(TranscribeJobAsync);
        emptyCancel.Click += (_, _) => { job?.Cancel(); SetJobText("Stopping…"); UpdateControls(); };
        emptyImport.Click += async (_, _) => await GuardAsync(() => ImportMediaAsync());
        importMedia.Click += async (_, _) => await GuardAsync(() => ImportMediaAsync());
        setupRuntime.Click += (_, _) => LaunchSetup();
        Activated += (_, _) =>
        {
            if (!awaitingSetup || !inference.Runtime.IsInstalled) return;
            awaitingSetup = false; RenderTranscribe(); UpdateControls();
        };
        preparePack.Click += (_, _) => StartJob(PreparePackAsync);
        transcribe.Click += (_, _) => StartJob(TranscribeJobAsync);
        cancelRun.Click += (_, _) => { job?.Cancel(); SetJobText("Stopping…"); UpdateControls(); };
        applyResult.Click += async (_, _) => await GuardAsync(ApplyPendingResultAsync);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => dropOverlay.IsVisible = false);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    // The whole window is the drop target, so the whole window says so while a usable file is over it.
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var usable = DroppedMedia(e) is not null;
        e.DragEffects = usable ? DragDropEffects.Copy : DragDropEffects.None;
        dropOverlay.IsVisible = usable;
        e.Handled = true;
    }
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        dropOverlay.IsVisible = false;
        var dropped = DroppedMedia(e);
        e.Handled = true;
        if (dropped is null) return;
        await GuardAsync(() => ImportMediaAsync(dropped));
    }
    // Exactly one file, and only one this build can identify as media: a dropped project or bundle would
    // otherwise be imported as if it were a recording.
    private string? DroppedMedia(DragEventArgs e)
    {
        if (busy || JobRunning || Recording) return null;
        var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Take(2).ToList();
        if (files is not { Count: 1 } || files[0] is not { } local) return null;
        if (local.EndsWith(".soundoff.sqlite", StringComparison.OrdinalIgnoreCase) ||
            local.EndsWith(SoundOff.Core.ProjectBundle.Extension, StringComparison.OrdinalIgnoreCase)) return null;
        return MediaFormats.IsRecognized(local) ? local : null;
    }

    private void SetJobText(string text)
    {
        jobText.Text = text; jobText.IsVisible = text.Length > 0;
        emptyJobText.Text = text; emptyJobText.IsVisible = text.Length > 0;
    }

    private void RenderTranscribe()
    {
        var runtime = inference.Runtime;
        runtimeText.Text = !runtime.IsInstalled
            ? TranscriptionSetup.Script is null
                ? "The transcription runtime is not installed. Run scripts/setup_runtime.py, then restart. " + runtime.MissingReason
                : "Transcription needs a one-time setup that downloads the speech engine. It runs in its own window; come back when it says it is done."
            : runtime.IsPackReady(PackModel) ? ""
            : "The model pack is not prepared yet. It is a one-time download of about 2 GB.";
        runtimeText.IsVisible = runtimeText.Text.Length > 0;
        setupRuntime.IsVisible = !runtime.IsInstalled && TranscriptionSetup.Script is not null;
        preparePack.IsVisible = runtime.IsInstalled && !runtime.IsPackReady(PackModel);
        // Before a project exists the card only matters if setup is still needed.
        transcribeCard.IsVisible = store is not null || runtimeText.IsVisible;
        // Transcribe is the next step only while there is no transcript yet; after that it is a do-over.
        var empty = snapshot is null || snapshot.Provenance == Provenance.Empty;
        // The empty state in the middle of the window owns the one accent; this is the same action, kept in reach.
        transcribe.Classes.Set("accent", false);
        transcribe.Content = empty ? "Transcribe" : "Transcribe again";
        var asset = store?.MediaAssets().LastOrDefault();
        mediaText.Text = asset?.OriginalName ?? "";
        mediaDetail.Text = asset is null ? "" : string.Join(" · ", new[]
        {
            asset.DurationMicroseconds is { } d ? Clock(d) : "unknown length", $"{asset.Bytes / 1_048_576.0:0.0} MB",
            MediaFormats.IsVideo(asset.OriginalName) ? "video" : "audio"
        });
        mediaRow.IsVisible = asset is not null;
        var video = asset is not null && MediaFormats.IsVideo(asset.OriginalName);
        mediaIcon.Classes.Set("film", video); mediaIcon.Classes.Set("wave", !video);
        ToolTip.SetTip(mediaRow, asset is null ? null : $"Copied into the project as {asset.RelativePath}");
        RenderEmptyState(asset is not null);
        runHost.Children.Clear();
        var runs = store?.Runs().Take(10).ToList() ?? [];
        runsExpander.IsVisible = runs.Count > 0;
        foreach (var run in runs)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 }; row.Classes.Add("run");
            var started = DateTime.TryParse(run.StartedUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var utc) ? utc.ToLocalTime().ToString("d MMM HH:mm") : run.StartedUtc;
            var text = $"{started} · {run.Status}" + (run.Error is null ? "" : $": {run.Error}");
            var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            ToolTip.SetTip(label, $"Run {run.Id}" + (run.Provider is null ? "" : $" · {run.Provider}"));
            row.Children.Add(label);
            var id = run.Id; var artifact = run.ArtifactRelativePath;
            var apply = Action("Apply", $"Apply run {run.Id[..Math.Min(8, run.Id.Length)]}", () => GuardAsync(() => ApplyStoredRunAsync(id)), enabled: run.Status == "completed" && artifact is not null);
            apply.Classes.Add("quiet");
            Grid.SetColumn(apply, 1); row.Children.Add(apply); runHost.Children.Add(Row(row));
        }
    }

    // The empty project's next step: import or record if there is no audio, transcribe if there is.
    private void RenderEmptyState(bool hasMedia)
    {
        var show = store is not null && (snapshot is null || snapshot.Provenance == Provenance.Empty);
        emptyTranscript.IsVisible = show;
        if (!show) return;
        var runtime = inference.Runtime;
        emptyHint.Text = !hasMedia ? "Import a recording, or record one from the Audio card in the side panel."
            : !runtime.IsInstalled ? "Transcription needs a one-time setup first. Start it from the Transcribe card in the side panel."
            : !runtime.IsPackReady(PackModel) ? "The speech model has not been prepared yet. Prepare it once from the side panel; it is about 2 GB."
            : "Transcription runs on this computer. Nothing is uploaded.";
        emptyImport.IsVisible = !hasMedia;
        emptyTranscribe.IsVisible = hasMedia;
    }

    private void LaunchSetup()
    {
        if (TranscriptionSetup.Script is not { } script) return;
        awaitingSetup = true;
        if (!TranscriptionSetup.Launch(script)) runtimeText.Text = "No terminal window could be opened. Run this in a terminal, then come back: " + script;
    }

    private void UpdateTranscribeControls()
    {
        var runtimeReady = inference.Runtime.IsInstalled; var packReady = runtimeReady && inference.Runtime.IsPackReady(PackModel);
        importMedia.IsEnabled = !busy && !JobRunning && !Recording;
        setupRuntime.IsEnabled = !busy && !JobRunning && !Recording;
        preparePack.IsEnabled = !busy && !JobRunning && !Recording && runtimeReady;
        transcribe.IsEnabled = !busy && !JobRunning && !Recording && packReady && store?.MediaAssets().Count > 0;
        cancelRun.IsEnabled = JobRunning && job?.IsCancellationRequested == false;
        cancelRun.IsVisible = jobProgress.IsVisible = JobRunning;
        emptyTranscribe.IsEnabled = transcribe.IsEnabled;
        ToolTip.SetTip(emptyTranscribe, ToolTip.GetTip(transcribe));
        emptyTranscribe.Content = JobRunning ? "Transcribing…" : "Transcribe";
        emptyCancel.IsVisible = emptyProgress.IsVisible = JobRunning;
        emptyCancel.IsEnabled = cancelRun.IsEnabled;
        emptyImport.IsEnabled = importMedia.IsEnabled;
        applyResult.IsEnabled = !busy && !JobRunning && pendingResult is not null && store is not null;
        applyResult.IsVisible = pendingResult is not null;
        languageChoice.IsEnabled = deviceChoice.IsEnabled = !JobRunning;
        ToolTip.SetTip(transcribe, !runtimeReady ? "Install the transcription runtime first." : !packReady ? "Prepare the model pack first."
            : store?.MediaAssets().Count > 0 ? "Transcribe the latest recording on this computer" : "Import or record audio first.");
    }

    // dropped: a file dragged onto the window, which skips the picker but takes exactly the same path after that.
    private async Task ImportMediaAsync(string? dropped = null)
    {
        if (Recording || JobRunning) return;
        var media = dropped ?? await picker.PickMediaAsync();
        if (media is null) return;
        if (store is null)
        {
            // A recording needs a project: create an empty one first, titled after the file.
            var local = await picker.CreateProjectAsync();
            if (local is null) return;
            RequireProjectExtension(local);
            if (File.Exists(local)) throw new IOException("Choose a new project filename. Existing projects are never overwritten.");
            var next = ProjectStore.Create(local, Transcript.CreateEmpty() with { Title = Path.GetFileNameWithoutExtension(media) });
            try { Replace(next, next.Read()); } catch { next.Dispose(); throw; }
            RememberCurrent();
        }
        status.Text = "Importing…";
        var asset = await MediaImport.ImportAsync(store!, media, lifetime.Token);
        RenderTranscribe(); SavedStatus();
        await SyncPlaybackSourceAsync();
        status.Text = $"Imported {asset.OriginalName}. " + status.Text;
        Toast("Imported " + asset.OriginalName);
    }

    // Jobs run outside GuardAsync so the editor stays usable; only media/model actions are blocked meanwhile.
    private void StartJob(Func<CancellationToken, Task> work)
    {
        if (JobRunning || busy || Recording) return;
        job = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = job.Token; UpdateControls();
        jobTask = RunJobAsync(work, token);
    }
    private async Task RunJobAsync(Func<CancellationToken, Task> work, CancellationToken token)
    {
        jobProgress.IsIndeterminate = emptyProgress.IsIndeterminate = true;
        try { await work(token); }
        catch (OperationCanceledException) { SetJobText("Stopped. The transcript is unchanged."); }
        catch (Exception e) { SetJobText("Failed: " + e.Message); }
        finally { job?.Dispose(); job = null; if (!lifetime.IsCancellationRequested) { RenderTranscribe(); UpdateControls(); } }
    }
    // Progress<T> posts asynchronously, so a late report could land after the job's final success or failure message
    // and overwrite it. This reporter delivers on the UI thread immediately and ignores anything from a finished job.
    private sealed class JobReporter(Action<InferenceProgress> handler, Func<bool> isCurrent) : IProgress<InferenceProgress>
    {
        public void Report(InferenceProgress value)
        {
            if (Dispatcher.UIThread.CheckAccess()) { if (isCurrent()) handler(value); }
            else Dispatcher.UIThread.Post(() => { if (isCurrent()) handler(value); });
        }
    }

    private IProgress<InferenceProgress> JobProgress(string verb)
    {
        var started = DateTime.UtcNow;
        var generation = ++jobGeneration;
        return new JobReporter(p =>
        {
            var elapsed = DateTime.UtcNow - started; var eta = "";
            if (p.Fraction is { } f && f > 0.05 && f < 1)
            {
                var remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - f) / f);
                eta = remaining.TotalMinutes >= 1 ? $" · about {Math.Ceiling(remaining.TotalMinutes):0} min left" : " · under a minute left";
            }
            jobProgress.IsIndeterminate = emptyProgress.IsIndeterminate = p.Fraction is null;
            if (p.Fraction is { } value) jobProgress.Value = emptyProgress.Value = Math.Clamp(value, 0, 1);
            SetJobText($"{verb}: {Describe(p.Stage)}{eta}");
            ToolTip.SetTip(jobText, p.Message);
        }, () => jobGeneration == generation && JobRunning);
    }
    private static string Describe(string stage) => stage switch
    {
        "import" => "starting the worker", "load-model" => "loading the model", "decode-audio" => "decoding audio", "transcribe" => "recognizing speech",
        "align" => "aligning words", "diarize" => "estimating speakers", "finalize" => "writing the result", "download-asr" => "downloading the recognition model",
        "download-vad" => "verifying the voice-activity model", "download-align" => "downloading an alignment model", "download-sentence-data" => "downloading sentence data",
        "verify" => "verifying the pack", _ => stage
    };

    private async Task PreparePackAsync(CancellationToken token)
    {
        Directory.CreateDirectory(inference.Runtime.ModelsDir);
        var log = Path.Combine(inference.Runtime.ModelsDir, $"prepare-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.log");
        SetJobText("Preparing the model pack…");
        var manifest = await inference.PrepareAsync(PackModel, ["en", "tl"], false, null, log, JobProgress("Preparing"), token);
        SetJobText($"Model pack ready ({manifest.Model} · {string.Join(", ", manifest.Languages)} · {manifest.Bytes / 1_048_576.0:0} MiB).");
    }

    private async Task TranscribeJobAsync(CancellationToken token)
    {
        var baseRevision = snapshot!.Revision;
        var asset = store!.MediaAssets().Last();
        var runId = Guid.NewGuid().ToString("N");
        var language = languageChoice.SelectedIndex switch { 1 => "en", 2 => "tl", _ => null };
        var device = deviceChoice.SelectedIndex == 1 ? "cuda" : "cpu";
        var options = JsonSerializer.Serialize(new { model = PackModel, device, language, diarize = false }, DocumentJson.Options);
        var runs = Path.Combine(store.MediaDirectory, "runs"); Directory.CreateDirectory(runs);
        var artifact = Path.Combine(runs, runId + ".json"); var log = Path.Combine(runs, runId + ".log");
        store.AddRun(new ProcessingRun(runId, asset.Id, DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), null, "running", options, null, null, null, null));
        RenderTranscribe(); SetJobText("Starting…");
        try
        {
            var completion = await inference.TranscribeAsync(Path.Combine(store.MediaDirectory, asset.RelativePath), artifact, log, PackModel, device, language, false, null, JobProgress("Transcribing"), token);
            var relative = Path.Combine("runs", runId + ".json");
            var proposal = InferenceImport.ToTranscript(completion.Artifact, snapshot!.ProjectId, baseRevision, snapshot.Title);
            store.FinishRun(runId, "completed", relative, completion.Sha256, null, completion.Artifact.ProviderLabel);
            var seconds = completion.Artifact.Timings.Values.Sum();
            ToolTip.SetTip(jobText, $"{proposal.Blocks.Length} paragraph(s), {proposal.Speakers.Length} speaker(s), language {completion.Artifact.Engine.Language ?? "?"}, " +
                $"{seconds:0} s of processing for {completion.Artifact.Audio.DurationSeconds:0} s of audio.");
            if (snapshot.Provenance == Provenance.Empty && snapshot.Revision == baseRevision && !dirty && !busy)
            {
                snapshot = store.ImportInference(snapshot.Revision, proposal, runId); Render(); SavedStatus();
                SetJobText($"Done. Transcript created as revision {snapshot.Revision}.");
                Toast("Transcript ready");
            }
            else
            {
                pendingResult = (runId, proposal);
                SetJobText("Done. Apply the result to replace the current transcript.");
            }
        }
        catch (OperationCanceledException) { TryFinish(runId, "cancelled", "cancelled by the user"); throw; }
        catch (Exception e) { TryFinish(runId, "failed", e.Message); throw; }
    }
    private void TryFinish(string runId, string status, string error)
    {
        try { store?.FinishRun(runId, status, null, null, error, null); } catch (InvalidOperationException) { } // already finished, or the store was disposed
    }

    private async Task ApplyPendingResultAsync()
    {
        if (pendingResult is not { } pending) return;
        if (await ApplyProposalAsync(pending.RunId, pending.Proposal)) pendingResult = null;
    }
    private async Task ApplyStoredRunAsync(string runId)
    {
        var run = store!.Runs().Single(r => r.Id == runId);
        if (run.ArtifactRelativePath is null || run.ArtifactSha256 is null) throw new InvalidOperationException("This run has no result artifact.");
        var path = Path.Combine(store.MediaDirectory, run.ArtifactRelativePath);
        var command = new TranscribeCommand(InferenceWorkerClient.Version, "transcribe", runId, inference.Runtime.ModelsDir, "", Path.Combine(store.MediaDirectory, store.MediaAssets().Single(a => a.Id == run.AssetId).RelativePath),
            path, PackModel, JsonDocument.Parse(run.OptionsJson).RootElement.GetProperty("device").GetString() ?? "cpu", null, null, 8, false, null, null, null, null);
        var completion = InferenceWorkerClient.ValidateArtifact(path, run.ArtifactSha256, new FileInfo(path).Length, command);
        await ApplyProposalAsync(runId, InferenceImport.ToTranscript(completion.Artifact, snapshot!.ProjectId, snapshot.Revision, snapshot.Title));
    }
    private async Task<bool> ApplyProposalAsync(string runId, Transcript proposal)
    {
        if (!await SettleDraftAsync("Discard unsaved changes?", "Applying the result replaces the whole document and discards your unsaved changes.", "Discard and apply")) return false;
        if (snapshot!.Provenance != Provenance.Empty && !await ConfirmAsync("Replace the transcript?",
            "The result replaces the current text, speakers and timing as a new revision. The current version stays in History.", "Replace")) return false;
        snapshot = store!.ImportInference(snapshot.Revision, proposal with { Revision = snapshot.Revision }, runId); Render(); SavedStatus();
        status.Text = $"Applied the transcription result as revision {snapshot.Revision}.";
        Toast("Transcription applied");
        return true;
    }

    // Closing with a running job: stop the worker first so no half-written artifact or orphan process remains.
    private async Task<bool> StopJobForCloseAsync()
    {
        if (!JobRunning) return true;
        if (!await ConfirmAsync("Stop transcription?", "Closing stops the running job. The recording and finished runs stay in the project.", "Stop and close")) return false;
        job?.Cancel();
        if (jobTask is not null) { try { await jobTask; } catch (Exception) { } }
        return true;
    }
}
