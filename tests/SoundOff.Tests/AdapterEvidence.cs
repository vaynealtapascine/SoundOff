using System.Text.Json;

namespace SoundOff.Tests;

// A passing conditional test is not proof that it reached a model/device. The verifier deletes these
// files before each run and requires a fresh record from each designated adapter test.
internal static class AdapterEvidence
{
    public static void Write(string adapter, bool exercised, string reason, object? details = null) =>
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"adapter-evidence-{adapter}.json"),
            JsonSerializer.Serialize(new { adapter, status = exercised ? "exercised" : "unavailable", reason, details }));
}
