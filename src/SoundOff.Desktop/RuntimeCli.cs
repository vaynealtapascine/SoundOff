using System.Text.Json;
using SoundOff.Protocol;

namespace SoundOff.Desktop;

// Headless entry points for the private inference runtime: status, explicit model-pack preparation, and a
// transcription run that writes a result artifact. Exit 0 success, 1 failure, 2 usage. Nothing downloads unless
// --prepare-pack is used; --transcribe runs fully offline.
public static class RuntimeCli
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var runtime = InferenceRuntime.Default();
            switch (args)
            {
                case ["--runtime-status"]:
                {
                    object status;
                    if (!runtime.IsInstalled) status = new { installed = false, reason = runtime.MissingReason, python = runtime.PythonPath, models = runtime.ModelsDir };
                    else
                    {
                        var hello = await new InferenceWorkerClient(runtime).HelloAsync(probeCuda: true, CancellationToken.None);
                        status = new { installed = true, python = runtime.PythonPath, worker = runtime.WorkerScriptPath, models = runtime.ModelsDir, hello,
                            packs = hello.Models.ToDictionary(m => m, runtime.IsPackReady) };
                    }
                    Console.WriteLine(JsonSerializer.Serialize(status, Pretty)); return 0;
                }
                case ["--prepare-pack", var model, .. var languages]:
                {
                    if (languages.Length == 0) languages = ["en"];
                    var diarize = languages.Contains("--diarization"); languages = languages.Where(l => l != "--diarization").ToArray();
                    var token = diarize ? Environment.GetEnvironmentVariable("HF_TOKEN") : null;
                    Directory.CreateDirectory(runtime.ModelsDir);
                    var log = Path.Combine(runtime.ModelsDir, $"prepare-{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}.log");
                    var manifest = await new InferenceWorkerClient(runtime, liveness: TimeSpan.FromMinutes(30)).PrepareAsync(model, languages, diarize, token, log,
                        new Progress<InferenceProgress>(p => Console.Error.WriteLine($"[{p.ElapsedSeconds,7:0.0}s] {p.Stage} {(p.Fraction is { } f ? $"{f:P0}" : "")} {p.Message}")), CancellationToken.None);
                    Console.WriteLine(JsonSerializer.Serialize(new { status = "ready", manifest, manifestPath = runtime.PackManifestPath(model), log }, Pretty)); return 0;
                }
                case ["--transcribe", var audio, var output, .. var rest]:
                {
                    var model = rest.Length > 0 ? rest[0] : "small"; var device = rest.Length > 1 ? rest[1] : "cpu"; var language = rest.Length > 2 && rest[2] != "auto" ? rest[2] : null;
                    if (!runtime.IsPackReady(model)) { Console.Error.WriteLine($"Model pack '{model}' is not prepared; run --prepare-pack {model} first."); return 1; }
                    var log = Path.ChangeExtension(Path.GetFullPath(output), ".log");
                    var completion = await new InferenceWorkerClient(runtime, liveness: TimeSpan.FromMinutes(10)).TranscribeAsync(audio, output, log, model, device, language, false, null,
                        new Progress<InferenceProgress>(p => Console.Error.WriteLine($"[{p.ElapsedSeconds,7:0.0}s] {p.Stage} {(p.Fraction is { } f ? $"{f:P0}" : "")} {p.Message}")), CancellationToken.None);
                    var document = InferenceImport.ToTranscript(completion.Artifact, Guid.NewGuid(), 0, Path.GetFileNameWithoutExtension(audio));
                    Console.WriteLine(JsonSerializer.Serialize(new { status = "completed", artifact = completion.ArtifactPath, bytes = completion.Bytes, sha256 = completion.Sha256,
                        language = completion.Artifact.Engine.Language, durationSeconds = completion.Artifact.Audio.DurationSeconds, timings = completion.Artifact.Timings,
                        segments = completion.Artifact.Segments.Length, paragraphs = document.Blocks.Length, speakers = document.Speakers.Length,
                        text = string.Join("\n", document.Blocks.Select(b => b.Text)) }, Pretty)); return 0;
                }
                default:
                    Console.Error.WriteLine("Usage: --runtime-status | --prepare-pack <model> [en tl] [--diarization] | --transcribe <audio> <output.json> [model] [cpu|cuda] [auto|en|tl]"); return 2;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "failed", error = e.Message })); return 1;
        }
    }
}
