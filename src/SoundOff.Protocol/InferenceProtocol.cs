using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoundOff.Core;

namespace SoundOff.Protocol;

// Protocol 2: one command per private Python child, NDJSON both ways, bounded lines, strict schemas.
// The worker never sees the project database; it reads one audio file and writes one result artifact.
public sealed record InferenceMessage([property: JsonRequired] int Version, [property: JsonRequired] string Type, string? JobId,
    [property: JsonRequired] long Sequence, [property: JsonRequired] string Provider, double? ElapsedSeconds = null,
    string? Stage = null, double? Fraction = null, string? Message = null, string? Error = null, bool? Cancelled = null,
    JsonElement? Result = null, string? ProviderVersion = null, string? Python = null, JsonElement? Packages = null,
    bool? Cuda = null, string? CudaDevice = null, string[]? Models = null, string[]? Languages = null);

public sealed record HelloCommand(int Version, string Type, string JobId, bool ProbeCuda);
public sealed record PrepareCommand(int Version, string Type, string JobId, string ModelsDir, string LogPath, string Model,
    string[] Languages, bool Diarization, string? HfToken);
public sealed record TranscribeCommand(int Version, string Type, string JobId, string ModelsDir, string LogPath, string AudioPath,
    string OutputPath, string Model, string Device, string? ComputeType, string? Language, int BatchSize, bool Diarize, string? HfToken,
    int? MinSpeakers, int? MaxSpeakers, int? Threads);

public sealed record RuntimeHello(string ProviderVersion, string Python, JsonElement Packages, bool? Cuda, string? CudaDevice,
    string[] Models, string[] Languages);
public sealed record PackManifest([property: JsonRequired] string Model, [property: JsonRequired] string[] Languages, string? Diarization,
    [property: JsonRequired] string ModelsDir, [property: JsonRequired] long Bytes, [property: JsonRequired] JsonElement Packages);
public sealed record InferenceProgress(string Stage, double? Fraction, string? Message, double ElapsedSeconds);

// The result artifact schema. Times are the engine's seconds; conversion to microseconds happens in InferenceImport.
public sealed record ArtifactWord([property: JsonRequired] string Word, double? Start = null, double? End = null, double? Score = null, string? Speaker = null);
public sealed record ArtifactSegment([property: JsonRequired] double Start, [property: JsonRequired] double End, [property: JsonRequired] string Text,
    string? Speaker, [property: JsonRequired] ImmutableArray<ArtifactWord> Words);
public sealed record ArtifactDiarization([property: JsonRequired] string Model);
public sealed record ArtifactEngine([property: JsonRequired] string Model, [property: JsonRequired] string Device, [property: JsonRequired] string ComputeType,
    string? LanguageHint, string? Language, string? AlignModel, ArtifactDiarization? Diarization, [property: JsonRequired] int BatchSize, [property: JsonRequired] int Threads);
public sealed record ArtifactAudio([property: JsonRequired] string Path, [property: JsonRequired] string Sha256, [property: JsonRequired] double DurationSeconds);
public sealed record InferenceArtifact([property: JsonRequired] int Version, [property: JsonRequired] string Provider, string? ProviderVersion,
    [property: JsonRequired] ArtifactEngine Engine, [property: JsonRequired] ArtifactAudio Audio, [property: JsonRequired] ImmutableArray<ArtifactSegment> Segments,
    [property: JsonRequired] Dictionary<string, double> Timings)
{
    public string ProviderLabel => Provider + " " + (ProviderVersion ?? "unknown-version");
}
public sealed record InferenceCompletion(string ArtifactPath, InferenceArtifact Artifact, string Sha256, long Bytes);

// Where the private runtime and model packs live. Nothing here is created merely by asking where things are.
public sealed record InferenceRuntime(string PythonPath, string WorkerScriptPath, string ModelsDir, string RuntimeManifestPath)
{
    // Data roots, in order: SOUNDOFF_HOME, %LOCALAPPDATA%\SoundOff, %USERPROFILE%\SoundOff. The first root that already holds
    // a runtime wins; otherwise the first candidate is used. Packaged (MSIX-style) hosts virtualize LOCALAPPDATA per app, so a
    // runtime installed from one host can be invisible to another; the profile-root fallback keeps one shared location usable.
    public static InferenceRuntime Default()
    {
        var candidates = new List<string>();
        var home = Environment.GetEnvironmentVariable("SOUNDOFF_HOME");
        if (!string.IsNullOrWhiteSpace(home)) candidates.Add(home);
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "SoundOff"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), "SoundOff"));
        var root = candidates.FirstOrDefault(c => File.Exists(VenvPython(Path.Combine(c, "runtime")))) ?? candidates[0];
        return ForRoot(root);
    }
    public static InferenceRuntime ForRoot(string root)
    {
        var runtime = Path.Combine(root, "runtime");
        return new(VenvPython(runtime), Path.Combine(AppContext.BaseDirectory, "worker", "soundoff_worker.py"),
            Path.Combine(root, "models"), Path.Combine(runtime, "runtime.json"));
    }
    // A virtual environment puts its interpreter in Scripts on Windows and bin everywhere else. Looking only
    // where Windows puts it meant a runtime could never be found on macOS or Linux, which is a different claim
    // from "this build has no audio adapter".
    internal static string VenvPython(string runtime) => OperatingSystem.IsWindows()
        ? Path.Combine(runtime, "venv", "Scripts", "python.exe")
        : Path.Combine(runtime, "venv", "bin", "python");
    public bool IsInstalled => File.Exists(PythonPath) && File.Exists(WorkerScriptPath);
    // Written by PrepareAsync only after the worker verified every resource; its absence means "not ready", never "download now".
    public string PackManifestPath(string model) => Path.Combine(ModelsDir, "packs", model + ".json");
    public bool IsPackReady(string model) => File.Exists(PackManifestPath(model));
    public string MissingReason => !File.Exists(PythonPath)
        ? $"The private Python runtime is not installed at {PythonPath}. Run scripts/setup_runtime.py first."
        : !File.Exists(WorkerScriptPath) ? $"The inference worker script is missing at {WorkerScriptPath}. Rebuild the desktop app." : "";
}

public sealed class InferenceWorkerClient
{
    public const int Version = 2;
    public const string Provider = "whisperx";
    public const long MaxArtifactBytes = 256L * 1024 * 1024;

    private readonly Func<ProcessStartInfo> startInfo;
    private readonly TimeSpan liveness;
    private readonly TimeSpan cancelGrace;
    public InferenceRuntime Runtime { get; }

    public InferenceWorkerClient(InferenceRuntime? runtime = null, TimeSpan? liveness = null, TimeSpan? cancelGrace = null)
    {
        Runtime = runtime ?? InferenceRuntime.Default();
        var captured = Runtime;
        startInfo = () =>
        {
            if (!captured.IsInstalled) throw new InvalidOperationException(captured.MissingReason);
            var info = new ProcessStartInfo(captured.PythonPath); info.ArgumentList.Add(captured.WorkerScriptPath);
            info.Environment["PYTHONIOENCODING"] = "utf-8"; info.Environment["PYTHONUTF8"] = "1";
            return info;
        };
        this.liveness = liveness ?? TimeSpan.FromSeconds(120); this.cancelGrace = cancelGrace ?? TimeSpan.FromSeconds(10);
    }
    // Test seam: any executable that speaks the protocol.
    public InferenceWorkerClient(Func<ProcessStartInfo> startInfo, InferenceRuntime runtime, TimeSpan? liveness = null, TimeSpan? cancelGrace = null)
    { this.startInfo = startInfo; Runtime = runtime; this.liveness = liveness ?? TimeSpan.FromSeconds(120); this.cancelGrace = cancelGrace ?? TimeSpan.FromSeconds(10); }

    public async Task<RuntimeHello> HelloAsync(bool probeCuda, CancellationToken cancellationToken)
    {
        var job = Guid.NewGuid().ToString("N");
        RuntimeHello? hello = null;
        await RunAsync(new HelloCommand(Version, "hello", job, probeCuda), job, null, message =>
        {
            if (message.Type != "hello") return;
            hello = new RuntimeHello(message.ProviderVersion ?? "", message.Python ?? "", message.Packages ?? default, message.Cuda, message.CudaDevice,
                message.Models ?? [], message.Languages ?? []);
        }, cancellationToken);
        return hello ?? throw new InvalidDataException("The worker completed without announcing itself.");
    }

    public async Task<PackManifest> PrepareAsync(string model, string[] languages, bool diarization, string? hfToken, string logPath,
        IProgress<InferenceProgress>? progress, CancellationToken cancellationToken)
    {
        var job = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Runtime.ModelsDir);
        var completed = await RunAsync(new PrepareCommand(Version, "prepare", job, Runtime.ModelsDir, logPath, model, languages, diarization, hfToken), job, progress, null, cancellationToken);
        if (completed.Result is not { ValueKind: JsonValueKind.Object } result) throw new InvalidDataException("The worker completed preparation without a pack manifest.");
        var manifest = DocumentJson.ReadStrict<PackManifest>(result.GetRawText(), WorkerProtocol.MaxLineBytes);
        if (manifest.Model != model || !manifest.Languages.SequenceEqual(languages)) throw new InvalidDataException("The pack manifest does not describe the requested pack.");
        Directory.CreateDirectory(Path.GetDirectoryName(Runtime.PackManifestPath(model))!);
        TextExport.WriteAtomic(Runtime.PackManifestPath(model), result.GetRawText() + "\n", overwrite: true);
        return manifest;
    }

    public async Task<InferenceCompletion> TranscribeAsync(string audioPath, string outputPath, string logPath, string model, string device, string? language,
        bool diarize, string? hfToken, IProgress<InferenceProgress>? progress, CancellationToken cancellationToken, int batchSize = 8, int? threads = null)
    {
        if (!File.Exists(audioPath)) throw new FileNotFoundException("The audio file to transcribe does not exist.", audioPath);
        if (File.Exists(outputPath)) throw new IOException("The result artifact path already exists; every run gets its own artifact.");
        var job = Guid.NewGuid().ToString("N");
        var command = new TranscribeCommand(Version, "transcribe", job, Runtime.ModelsDir, logPath, Path.GetFullPath(audioPath), Path.GetFullPath(outputPath),
            model, device, null, language, batchSize, diarize, hfToken, null, null, threads);
        try
        {
            var completed = await RunAsync(command, job, progress, null, cancellationToken);
            if (completed.Result is not { ValueKind: JsonValueKind.Object } summary) throw new InvalidDataException("The worker completed without a result summary.");
            var declaredPath = summary.GetProperty("path").GetString();
            var declaredSha = summary.GetProperty("sha256").GetString() ?? "";
            var declaredBytes = summary.GetProperty("bytes").GetInt64();
            if (!string.Equals(declaredPath, Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The worker wrote its result somewhere else.");
            return ValidateArtifact(outputPath, declaredSha, declaredBytes, command);
        }
        catch
        {
            // A rejected or interrupted run leaves no half-artifact behind.
            try { if (File.Exists(outputPath)) File.Delete(outputPath); if (File.Exists(outputPath + ".tmp")) File.Delete(outputPath + ".tmp"); } catch (IOException) { }
            throw;
        }
    }

    public static InferenceCompletion ValidateArtifact(string path, string expectedSha256, long expectedBytes, TranscribeCommand command)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidDataException("The worker reported completion but no result artifact exists.");
        if (info.Length != expectedBytes || info.Length > MaxArtifactBytes) throw new InvalidDataException("The result artifact size does not match the worker's summary or exceeds the limit.");
        var bytes = File.ReadAllBytes(path);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(sha, expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The result artifact digest does not match the worker's summary.");
        var json = new System.Text.UTF8Encoding(false, true).GetString(bytes);
        var artifact = DocumentJson.ReadStrict<InferenceArtifact>(json, (int)Math.Min(int.MaxValue, MaxArtifactBytes));
        if (artifact.Version != Version || artifact.Provider != Provider) throw new InvalidDataException("The result artifact is not a protocol-2 WhisperX result.");
        if (artifact.Engine.Model != command.Model || artifact.Engine.Device != command.Device) throw new InvalidDataException("The result artifact describes a different engine configuration than requested.");
        if (!string.Equals(Path.GetFullPath(artifact.Audio.Path), command.AudioPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The result artifact refers to a different audio file.");
        if (artifact.Audio.DurationSeconds <= 0 || !Finite(artifact.Audio.DurationSeconds) || artifact.Audio.DurationSeconds >= long.MaxValue / 1_000_000.0
            || artifact.Audio.Sha256.Length != 64 || !artifact.Audio.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("The result artifact has an invalid audio description.");
        using (var audio = File.OpenRead(command.AudioPath))
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(audio)), artifact.Audio.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The result artifact audio digest does not match the input recording.");
        if (artifact.Timings.Values.Any(t => !Finite(t) || t < 0)) throw new InvalidDataException("Invalid stage timing.");
        foreach (var segment in artifact.Segments)
        {
            if (!Finite(segment.Start) || !Finite(segment.End) || segment.Start < 0 || segment.End < segment.Start || segment.End > artifact.Audio.DurationSeconds + 0.1) throw new InvalidDataException("A segment has an invalid interval.");
            if (segment.Words.IsDefault) throw new InvalidDataException("A segment is missing its word list.");
            foreach (var word in segment.Words)
            {
                if ((word.Start is null) != (word.End is null)) throw new InvalidDataException("A word has half an interval.");
                if (word.Start is { } s && word.End is { } e && (!Finite(s) || !Finite(e) || s < segment.Start || e < s || e > segment.End)) throw new InvalidDataException("A word has an invalid interval.");
                if (word.Score is { } score && !Finite(score)) throw new InvalidDataException("A word has an invalid score.");
            }
        }
        return new InferenceCompletion(Path.GetFullPath(path), artifact, sha, info.Length);
    }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private async Task<InferenceMessage> RunAsync<TCommand>(TCommand command, string job, IProgress<InferenceProgress>? progress, Action<InferenceMessage>? observe, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = startInfo();
        info.UseShellExecute = false; info.CreateNoWindow = true;
        info.RedirectStandardInput = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("Could not start the inference worker.");
        using var killed = new CancellationTokenSource();
        var cancelHandled = false;
        // Drain immediately, including while writing the request, and observe shutdown on every path.
        var diagnostics = DrainAsync(process.StandardError.BaseStream, killed.Token);
        try
        {
            // A child that exits before reading breaks the pipe: a worker that refused the request, not an app I/O fault.
            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, killed.Token);
            requestDeadline.CancelAfter(liveness);
            try { await WorkerProtocol.WriteLineAsync(process.StandardInput.BaseStream, command, requestDeadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("The inference worker did not accept the command in time."); }
            catch (IOException e) { throw new InvalidDataException("The inference worker ended before accepting the command.", e); }
            var reader = new JsonLineReader<InferenceMessage>(process.StandardOutput.BaseStream);
            long expectedSequence = 1;
            var graceEnds = DateTime.MaxValue;

            while (true)
            {
                if (cancellationToken.IsCancellationRequested && !cancelHandled)
                {
                    // Ask politely once; if the stage does not yield within the grace period the child is killed.
                    cancelHandled = true; graceEnds = DateTime.UtcNow + cancelGrace;
                    using var cancelDeadline = CancellationTokenSource.CreateLinkedTokenSource(killed.Token);
                    cancelDeadline.CancelAfter(cancelGrace);
                    try { await WorkerProtocol.WriteLineAsync(process.StandardInput.BaseStream, new { version = Version, type = "cancel", jobId = job }, cancelDeadline.Token); }
                    catch (IOException) { }
                    catch (OperationCanceledException) { throw new OperationCanceledException("The inference worker did not accept cancellation and was stopped.", cancellationToken); }
                }
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(killed.Token);
                var wait = cancelHandled ? graceEnds - DateTime.UtcNow : liveness;
                if (wait <= TimeSpan.Zero) throw new OperationCanceledException("The inference run was cancelled; the worker was stopped.", cancellationToken);
                idle.CancelAfter(wait);
                using var wake = cancellationToken.Register(() => { if (!cancelHandled) idle.Cancel(); });
                InferenceMessage? message;
                try { message = await reader.ReadAsync(idle.Token); }
                catch (OperationCanceledException) when (!killed.IsCancellationRequested)
                {
                    if (cancellationToken.IsCancellationRequested && !cancelHandled) continue; // woke up to send the cancel request
                    if (cancelHandled) throw new OperationCanceledException("The inference run was cancelled; the worker was stopped.", cancellationToken);
                    throw new TimeoutException($"The inference worker sent nothing for {liveness.TotalSeconds:0} seconds and was stopped.");
                }
                if (message is null) throw new InvalidDataException("The inference worker ended without a completed or failed message.");
                if (message.Version != Version || message.Provider != Provider) throw new InvalidDataException("Unexpected worker protocol version or provider.");
                if (message.JobId is not null && message.JobId != job) throw new InvalidDataException("The worker answered for a different job.");
                if (message.Sequence != expectedSequence) throw new InvalidDataException("Worker messages arrived out of sequence.");
                expectedSequence++;
                switch (message.Type)
                {
                    case "progress":
                        if (message.Stage is null) throw new InvalidDataException("Progress without a stage.");
                        if (message.Fraction is { } f && (f < 0 || f > 1 || double.IsNaN(f))) throw new InvalidDataException("Progress fraction out of range.");
                        if (message.Stage != "heartbeat") progress?.Report(new InferenceProgress(message.Stage, message.Fraction, message.Message, message.ElapsedSeconds ?? 0));
                        break;
                    case "hello": observe?.Invoke(message); break;
                    case "failed":
                        if (message.Cancelled == true || cancellationToken.IsCancellationRequested) throw new OperationCanceledException("The inference run was cancelled.", cancellationToken);
                        throw new InvalidDataException("The inference worker failed: " + (message.Error ?? "no reason given"));
                    case "completed":
                        if (message.JobId != job) throw new InvalidDataException("Completion for a different job.");
                        process.StandardInput.Close();
                        using (var finalDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, killed.Token))
                        {
                            finalDeadline.CancelAfter(liveness);
                            try
                            {
                                // Read before waiting for exit: a post-completion stdout flood must not fill the pipe.
                                if (await reader.ReadAsync(finalDeadline.Token) is not null) throw new InvalidDataException("Unexpected message after completion.");
                                await process.WaitForExitAsync(finalDeadline.Token);
                                await diagnostics.WaitAsync(finalDeadline.Token);
                                cancellationToken.ThrowIfCancellationRequested();
                            }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                            { throw new TimeoutException("The inference worker did not exit after completion."); }
                        }
                        if (process.ExitCode != 0) throw new InvalidDataException($"The inference worker exited unsuccessfully ({process.ExitCode}) after reporting completion.");
                        return message;
                    default: throw new InvalidDataException("Unknown worker message type: " + message.Type);
                }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                Kill(process);
            }
            killed.Cancel();
            await process.WaitForExitAsync();
            await diagnostics;
        }
    }

    private static async Task DrainAsync(Stream stream, CancellationToken token)
    {
        // Fixed memory, discarded continuously: stopping the drain at a byte limit can deadlock the child.
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, token); if (count == 0) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

// Engine output becomes an app-owned document: stable IDs, speaker-grouped turns, integer microseconds, word evidence.
public static class InferenceImport
{
    public const long ParagraphGapMicroseconds = 1_500_000;
    public const int ParagraphTargetLength = 2_000;

    public static Transcript ToTranscript(InferenceArtifact artifact, Guid projectId, long revision, string title)
    {
        var speakerIds = new Dictionary<string, Guid>(StringComparer.Ordinal); var speakers = new List<Speaker>();
        Guid SpeakerFor(string? label)
        {
            var key = label ?? "";
            if (!speakerIds.TryGetValue(key, out var id))
            {
                id = Guid.NewGuid(); speakerIds[key] = id;
                speakers.Add(new Speaker(id, label is null ? (speakers.Count == 0 ? "Speaker" : "Speaker " + (speakers.Count + 1)) : "Speaker " + (speakers.Count + 1)));
            }
            return id;
        }
        var blocks = new List<TranscriptBlock>();
        var text = new System.Text.StringBuilder(); var words = ImmutableArray.CreateBuilder<Word>();
        Guid currentSpeaker = Guid.Empty; long? start = null, end = null; var timed = true;
        void Flush()
        {
            if (text.Length == 0) return;
            TimeRange? timing = timed && start is { } s && end is { } e && e > s ? new TimeRange(s, e) : null;
            blocks.Add(new TranscriptBlock(Guid.NewGuid(), currentSpeaker, text.ToString(), timing, false, timing is null ? default : words.ToImmutable()));
            text.Clear(); words.Clear(); start = null; end = null; timed = true;
        }
        foreach (var segment in artifact.Segments)
        {
            var segmentText = segment.Text.Trim();
            if (segmentText.Length == 0) continue;
            var speaker = SpeakerFor(segment.Speaker);
            var segmentStart = Micro(segment.Start); var segmentEnd = Micro(segment.End);
            var newParagraph = text.Length == 0 || speaker != currentSpeaker || (end is { } previousEnd && (segmentStart < previousEnd || segmentStart - previousEnd >= ParagraphGapMicroseconds))
                || text.Length + 1 + segmentText.Length > ParagraphTargetLength;
            if (newParagraph) { Flush(); currentSpeaker = speaker; }
            if (text.Length > 0) text.Append(' ');
            text.Append(segmentText);
            if (segmentEnd <= segmentStart) timed = false; // a zero-length engine segment gets no invented interval
            start ??= segmentStart; end = Math.Max(end ?? segmentEnd, segmentEnd);
            foreach (var word in segment.Words)
            {
                var wordText = word.Word.Trim(); if (wordText.Length == 0) continue;
                TimeRange? wordTiming = word.Start is { } ws && word.End is { } we && Micro(we) > Micro(ws) ? new TimeRange(Micro(ws), Micro(we)) : null;
                words.Add(new Word(wordText, wordTiming, word.Score));
            }
        }
        Flush();
        if (speakers.Count == 0) speakers.Add(new Speaker(Guid.NewGuid(), "Speaker"));
        var document = new Transcript(projectId, title, revision, Provenance.Model(artifact.ProviderLabel), speakers.ToImmutableArray(), blocks.ToImmutableArray());
        DocumentRules.Validate(document);
        return document;
    }

    public static long Micro(double seconds) => (long)Math.Round(seconds * 1_000_000.0);
}
