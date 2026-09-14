using System.Text.Json;
using Avalonia.Controls;
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
    private Button importMedia = null!, preparePack = null!, transcribe = null!, cancelRun = null!, applyResult = null!;
    private ComboBox languageChoice = null!, deviceChoice = null!;
    private StackPanel runHost = null!;
    private CancellationTokenSource? job;
    private Task? jobTask;
    private (string RunId, Transcript Proposal)? pendingResult;
    private int jobGeneration;
    private bool JobRunning => job is not null;

    private void InitializeTranscribe(InferenceWorkerClient? client)
    {
        inference = client ?? new InferenceWorkerClient();
        runtimeText = this.FindControl<TextBlock>("RuntimeText")!; mediaText = this.FindControl<TextBlock>("MediaText")!; jobText = this.FindControl<TextBlock>("JobText")!;
        importMedia = this.FindControl<Button>("ImportMediaButton")!; preparePack = this.FindControl<Button>("PreparePackButton")!;
        transcribe = this.FindControl<Button>("TranscribeButton")!; cancelRun = this.FindControl<Button>("CancelRunButton")!; applyResult = this.FindControl<Button>("ApplyResultButton")!;
        languageChoice = this.FindControl<ComboBox>("LanguageChoice")!; deviceChoice = this.FindControl<ComboBox>("DeviceChoice")!; runHost = this.FindControl<StackPanel>("RunHost")!;
        importMedia.Click += async (_, _) => await GuardAsync(ImportMediaAsync);
        preparePack.Click += (_, _) => StartJob(PreparePackAsync);
        transcribe.Click += (_, _) => StartJob(TranscribeJobAsync);
        cancelRun.Click += (_, _) => { job?.Cancel(); jobText.Text = "Stopping the worker…"; UpdateControls(); };
        applyResult.Click += async (_, _) => await GuardAsync(ApplyPendingResultAsync);
        jobText.Text = "No transcription has run.";
    }

    private void RenderTranscribe()
    {
        var runtime = inference.Runtime;
        runtimeText.Text = !runtime.IsInstalled
            ? "Private WhisperX runtime not installed. Run scripts/setup_runtime.py, then restart. " + runtime.MissingReason
            : runtime.IsPackReady(PackModel)
                ? $"WhisperX runtime ready · '{PackModel}' model pack prepared in {runtime.ModelsDir}. Inference runs offline on this computer."
                : $"WhisperX runtime ready · the '{PackModel}' model pack is not prepared yet. Prepare model pack downloads it once (about 2 GB with the English and Filipino aligners).";
        var asset = store?.MediaAssets().LastOrDefault();
        mediaText.Text = store is null ? "Open or create a project, then import a recording."
            : asset is null ? "No recording imported into this project yet."
            : $"Recording: {asset.OriginalName} · {(asset.DurationMicroseconds is { } d ? TimeText.Format(d) : "unknown length")} · {asset.Bytes / 1_048_576.0:0.0} MiB · copied to {asset.RelativePath}.";
        runHost.Children.Clear();
        if (store is not null)
            foreach (var run in store.Runs().Take(10))
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 }; row.Classes.Add("run");
                var text = $"Run {run.Id[..8]} · {run.Status} · {run.StartedUtc}" + (run.Provider is null ? "" : $" · {run.Provider}") + (run.Error is null ? "" : $" · {run.Error}");
                row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                var id = run.Id; var artifact = run.ArtifactRelativePath;
                var apply = Action("Apply", $"Apply run {run.Id[..8]}", () => GuardAsync(() => ApplyStoredRunAsync(id)), enabled: run.Status == "completed" && artifact is not null);
                Grid.SetColumn(apply, 1); row.Children.Add(apply); runHost.Children.Add(row);
            }
    }

    private void UpdateTranscribeControls()
    {
        var runtimeReady = inference.Runtime.IsInstalled; var packReady = runtimeReady && inference.Runtime.IsPackReady(PackModel);
        importMedia.IsEnabled = !busy && !JobRunning;
        preparePack.IsEnabled = !busy && !JobRunning && runtimeReady;
        transcribe.IsEnabled = !busy && !JobRunning && packReady && store?.MediaAssets().Count > 0;
        cancelRun.IsEnabled = JobRunning && job?.IsCancellationRequested == false;
        applyResult.IsEnabled = !busy && !JobRunning && pendingResult is not null && store is not null;
        languageChoice.IsEnabled = deviceChoice.IsEnabled = !JobRunning;
        ToolTip.SetTip(transcribe, !runtimeReady ? "Install the private runtime first." : !packReady ? "Prepare the model pack first." : store?.MediaAssets().Count > 0 ? "Runs WhisperX locally on the latest imported recording." : "Import a recording first.");
    }

    private async Task ImportMediaAsync()
    {
        var media = await picker.PickMediaAsync();
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
        status.Text = "Copying and probing the recording… The original file is not modified.";
        var asset = await MediaImport.ImportAsync(store!, media, lifetime.Token);
        RenderTranscribe(); SavedStatus();
        await SyncPlaybackSourceAsync();
        status.Text = $"Imported {asset.OriginalName} ({(asset.DurationMicroseconds is { } d ? TimeText.Format(d) : "?")}). " + status.Text;
    }

    // Jobs run outside GuardAsync so the editor stays usable; only media/model actions are blocked meanwhile.
    private void StartJob(Func<CancellationToken, Task> work)
    {
        if (JobRunning || busy) return;
        job = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = job.Token; UpdateControls();
        jobTask = RunJobAsync(work, token);
    }
    private async Task RunJobAsync(Func<CancellationToken, Task> work, CancellationToken token)
    {
        try { await work(token); }
        catch (OperationCanceledException) { jobText.Text = "Stopped. Nothing was changed in the transcript."; }
        catch (Exception e) { jobText.Text = "Failed: " + e.Message; }
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
            var elapsed = DateTime.UtcNow - started; string eta = "Estimating…";
            if (p.Fraction is { } f && f > 0.05 && f < 1)
            {
                var remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - f) / f);
                eta = remaining.TotalMinutes >= 1 ? $"about {Math.Ceiling(remaining.TotalMinutes):0} min left" : "under a minute left";
            }
            jobText.Text = $"{verb}: {Describe(p.Stage)}{(p.Fraction is { } fr ? $" ({fr:P0})" : "")} · {elapsed:mm\\:ss} elapsed · {eta}" + (p.Message is null ? "" : $" · {p.Message}");
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
        jobText.Text = "Preparing the model pack…";
        var manifest = await inference.PrepareAsync(PackModel, ["en", "tl"], false, null, log, JobProgress("Preparing"), token);
        jobText.Text = $"Model pack ready: {manifest.Model} with {string.Join(", ", manifest.Languages)} alignment, {manifest.Bytes / 1_048_576.0:0} MiB on disk.";
    }

    private async Task TranscribeJobAsync(CancellationToken token)
    {
        var asset = store!.MediaAssets().Last();
        var runId = Guid.NewGuid().ToString("N");
        var language = languageChoice.SelectedIndex switch { 1 => "en", 2 => "tl", _ => null };
        var device = deviceChoice.SelectedIndex == 1 ? "cuda" : "cpu";
        var options = JsonSerializer.Serialize(new { model = PackModel, device, language, diarize = false }, DocumentJson.Options);
        var runs = Path.Combine(store.MediaDirectory, "runs"); Directory.CreateDirectory(runs);
        var artifact = Path.Combine(runs, runId + ".json"); var log = Path.Combine(runs, runId + ".log");
        store.AddRun(new ProcessingRun(runId, asset.Id, DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), null, "running", options, null, null, null, null));
        RenderTranscribe(); jobText.Text = "Starting the private worker…";
        try
        {
            var completion = await inference.TranscribeAsync(Path.Combine(store.MediaDirectory, asset.RelativePath), artifact, log, PackModel, device, language, false, null, JobProgress("Transcribing"), token);
            var relative = Path.Combine("runs", runId + ".json");
            store.FinishRun(runId, "completed", relative, completion.Sha256, null, completion.Artifact.ProviderLabel);
            var proposal = InferenceImport.ToTranscript(completion.Artifact, snapshot!.ProjectId, snapshot.Revision, snapshot.Title);
            var seconds = completion.Artifact.Timings.Values.Sum();
            var summary = $"Finished: {proposal.Blocks.Length} paragraph(s), {proposal.Speakers.Length} speaker(s), language {completion.Artifact.Engine.Language ?? "?"}, {seconds:0} s of processing for {completion.Artifact.Audio.DurationSeconds:0} s of audio.";
            if (snapshot.Provenance == Provenance.Empty && !dirty)
            {
                snapshot = store.ImportInference(snapshot.Revision, proposal, runId); Render(); SavedStatus();
                jobText.Text = summary + " The result is now the transcript (revision " + snapshot.Revision + ").";
            }
            else
            {
                pendingResult = (runId, proposal);
                jobText.Text = summary + " The current transcript was kept; use Apply model result to replace it as a new revision.";
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
        await ApplyProposalAsync(pending.RunId, pending.Proposal);
        pendingResult = null;
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
    private async Task ApplyProposalAsync(string runId, Transcript proposal)
    {
        if (dirty && !await ConfirmAsync("Discard unsaved draft?", "Applying the model result replaces the whole document as a new revision. Your unsaved input would be discarded.", "Discard draft and apply")) return;
        if (snapshot!.Provenance != Provenance.Empty && !await ConfirmAsync("Replace the transcript?",
            "The model result becomes a new revision replacing the current text, speakers and timing. History keeps the current revision and Restore brings it back.", "Replace with model result")) return;
        snapshot = store!.ImportInference(snapshot.Revision, proposal with { Revision = snapshot.Revision }, runId); Render(); SavedStatus();
        status.Text = $"Applied model result of run {runId[..8]} as revision {snapshot.Revision}. " + status.Text;
    }

    // Closing with a running job: stop the worker first so no half-written artifact or orphan process remains.
    private async Task<bool> StopJobForCloseAsync()
    {
        if (!JobRunning) return true;
        if (!await ConfirmAsync("Stop transcription?", "The worker is still running. Closing stops it; the recording and any finished runs stay in the project.", "Stop and close")) return false;
        job?.Cancel();
        if (jobTask is not null) { try { await jobTask; } catch (Exception) { } }
        return true;
    }
}
