using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoundOff.Core;
using SoundOff.Protocol;

namespace SoundOff.Tests;

// Test-only stand-in for the Python inference worker: speaks protocol 2 correctly or deliberately wrongly.
internal static class InferenceAdversary
{
    public static async Task<int> RunAsync(string mode)
    {
        var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput();
        var line = await new StreamReader(input, Encoding.UTF8).ReadLineAsync() ?? "";
        var command = JsonNode.Parse(line)!.AsObject();
        var job = command["jobId"]!.GetValue<string>(); var sequence = 0L;
        async Task Emit(JsonObject message, string? jobOverride = null, long? sequenceOverride = null)
        {
            message["version"] = 2; message["provider"] = "whisperx"; message["jobId"] = jobOverride ?? job; message["sequence"] = sequenceOverride ?? ++sequence;
            await output.WriteAsync(Encoding.UTF8.GetBytes(message.ToJsonString() + "\n")); await output.FlushAsync();
        }
        switch (mode)
        {
            case "inf-hello":
                await Emit(new JsonObject { ["type"] = "hello", ["providerVersion"] = "3.8.6", ["python"] = "3.11.16", ["packages"] = new JsonObject { ["torch"] = "2.8.0" },
                    ["cuda"] = false, ["cudaDevice"] = null, ["models"] = new JsonArray("small"), ["languages"] = new JsonArray("en", "tl") });
                await Emit(new JsonObject { ["type"] = "completed", ["result"] = null }); return 0;
            case "inf-prepare-ok":
                await Emit(new JsonObject { ["type"] = "progress", ["stage"] = "download-asr", ["fraction"] = 0.1, ["message"] = "fetching", ["elapsedSeconds"] = 1.5 });
                await Emit(new JsonObject { ["type"] = "completed", ["result"] = new JsonObject { ["model"] = command["model"]!.GetValue<string>(),
                    ["languages"] = new JsonArray(command["languages"]!.AsArray().Select(n => (JsonNode?)n!.GetValue<string>()).ToArray()), ["diarization"] = null,
                    ["modelsDir"] = command["modelsDir"]!.GetValue<string>(), ["bytes"] = 123456789, ["packages"] = new JsonObject { ["torch"] = "2.8.0" } } });
                return 0;
            case "inf-wrong-job":
                await Emit(new JsonObject { ["type"] = "progress", ["stage"] = "load-model" }, jobOverride: "someone-else"); return 0;
            case "inf-sequence-gap":
                await Emit(new JsonObject { ["type"] = "progress", ["stage"] = "load-model" });
                await Emit(new JsonObject { ["type"] = "progress", ["stage"] = "transcribe" }, sequenceOverride: 3); return 0;
            case "inf-failed":
                await Emit(new JsonObject { ["type"] = "progress", ["stage"] = "load-model" });
                await Emit(new JsonObject { ["type"] = "failed", ["error"] = "RuntimeError: model files are missing", ["cancelled"] = false }); return 1;
            case "inf-hang":
                await Emit(new JsonObject { ["type"] = "progress", ["stage"] = "transcribe", ["fraction"] = 0.2 });
                await Task.Delay(TimeSpan.FromMinutes(5)); return 0;
            case "inf-cancel-honoured":
            {
                await Emit(new JsonObject { ["type"] = "progress", ["stage"] = "transcribe", ["fraction"] = 0.2 });
                var cancel = await new StreamReader(input, Encoding.UTF8).ReadLineAsync();
                if (cancel is not null && cancel.Contains("\"cancel\"")) { await Emit(new JsonObject { ["type"] = "failed", ["error"] = "cancelled", ["cancelled"] = true }); return 3; }
                return 9;
            }
            case "inf-cancel-ignored":
                await Emit(new JsonObject { ["type"] = "progress", ["stage"] = "transcribe", ["fraction"] = 0.2 });
                await Task.Delay(TimeSpan.FromMinutes(5)); return 0;
            case "inf-extra-after-completed":
            case "inf-missing-artifact":
            case "inf-bad-sha":
            case "inf-bad-schema":
            case "inf-wrong-audio":
            case "inf-ok":
            {
                var outputPath = command["outputPath"]!.GetValue<string>(); var audioPath = command["audioPath"]!.GetValue<string>();
                var artifact = Artifact(mode == "inf-wrong-audio" ? audioPath + ".other" : audioPath, command["model"]!.GetValue<string>(), command["device"]!.GetValue<string>());
                if (mode == "inf-bad-schema") artifact["surprise"] = true;
                var bytes = Encoding.UTF8.GetBytes(artifact.ToJsonString());
                if (mode != "inf-missing-artifact") await File.WriteAllBytesAsync(outputPath, bytes);
                var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (mode == "inf-bad-sha") sha = new string('0', 64);
                await Emit(new JsonObject { ["type"] = "progress", ["stage"] = "load-model", ["fraction"] = 0.05, ["message"] = "Loading", ["elapsedSeconds"] = 0.5 });
                await Emit(new JsonObject { ["type"] = "progress", ["stage"] = "heartbeat" });
                await Emit(new JsonObject { ["type"] = "completed", ["result"] = new JsonObject { ["path"] = outputPath, ["bytes"] = bytes.Length, ["sha256"] = sha,
                    ["segments"] = 2, ["durationSeconds"] = 9.3, ["language"] = "en", ["timings"] = new JsonObject() } });
                if (mode == "inf-extra-after-completed") await Emit(new JsonObject { ["type"] = "progress", ["stage"] = "late" });
                return 0;
            }
            default: return 2;
        }
    }

    public static JsonObject Artifact(string audioPath, string model, string device) => new()
    {
        ["version"] = 2, ["provider"] = "whisperx", ["providerVersion"] = "3.8.6",
        ["engine"] = new JsonObject { ["model"] = model, ["device"] = device, ["computeType"] = "int8", ["languageHint"] = null, ["language"] = "en",
            ["alignModel"] = "torchaudio:WAV2VEC2_ASR_BASE_960H", ["diarization"] = null, ["batchSize"] = 8, ["threads"] = 4 },
        ["audio"] = new JsonObject { ["path"] = audioPath, ["sha256"] = new string('a', 64), ["durationSeconds"] = 9.335 },
        ["segments"] = new JsonArray(
            new JsonObject { ["start"] = 0.5, ["end"] = 3.9, ["text"] = " Hello. This is a synthetic English test clip.", ["speaker"] = "SPEAKER_00",
                ["words"] = new JsonArray(new JsonObject { ["word"] = "Hello.", ["start"] = 0.5, ["end"] = 0.9, ["score"] = 0.91 }, new JsonObject { ["word"] = "This", ["start"] = 1.1, ["end"] = 1.3 }, new JsonObject { ["word"] = "42" }) },
            new JsonObject { ["start"] = 5.5, ["end"] = 8.7, ["text"] = "The quick brown fox jumps over the lazy dog.", ["speaker"] = "SPEAKER_00",
                ["words"] = new JsonArray(new JsonObject { ["word"] = "The", ["start"] = 5.5, ["end"] = 5.7, ["score"] = 0.8 }) }),
        ["timings"] = new JsonObject { ["transcribe"] = 2.5 }
    };
}
