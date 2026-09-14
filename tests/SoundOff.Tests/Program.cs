using SoundOff.Core;
using SoundOff.Protocol;

namespace SoundOff.Tests;

// Test-only executable adversary. Never copied into the desktop distribution.
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) return 2;
        var mode = args[0];
        if (mode == "hold-lock")
        {
            using var store = ProjectStore.Open(args[1]); Console.WriteLine("locked"); Console.Out.Flush();
            await Task.Delay(TimeSpan.FromMinutes(1)); return 0;
        }
        if (mode == "crash-before-commit")
        {
            using var store = ProjectStore.Open(args[1]); var current = store.Read();
            store.BeforeCommit = () => Environment.Exit(77);
            store.Apply(current.Revision, new(new Dictionary<Guid, string> { [current.Speakers[0].Id] = "CRASH MUST ROLLBACK" }, new Dictionary<Guid, string>()));
            return 2;
        }
        if (mode == "exit-zero") return 0;
        if (mode == "hang")
        {
            if (args.Length == 2) File.WriteAllText(args[1], Environment.ProcessId.ToString());
            await Task.Delay(TimeSpan.FromMinutes(1)); return 0;
        }
        var request = await new JsonLineReader(Console.OpenStandardInput()).ReadAsync(CancellationToken.None);
        if (request is null) return 2;
        var output = Console.OpenStandardOutput();
        if (mode == "invalid-json") { Console.WriteLine("{not json}"); return 0; }
        if (mode == "oversized") { Console.WriteLine(new string('x', WorkerProtocol.MaxLineBytes + 1)); return 0; }
        if (mode == "truncated") { Console.Write("{}"); return 0; }
        if (mode == "stderr-overflow") { Console.Error.Write(new string('e', WorkerProtocol.MaxDiagnosticBytes + 1)); Console.Error.Flush(); await Task.Delay(TimeSpan.FromSeconds(20)); return 0; }
        var hello = request with { Type = "hello-fixture", Sequence = 1 };
        hello = mode switch
        {
            "wrong-run" => hello with { JobId = Guid.NewGuid() },
            "wrong-project" => hello with { ProjectId = Guid.NewGuid() },
            "wrong-revision" => hello with { BaseRevision = hello.BaseRevision + 1 },
            "wrong-version" => hello with { Version = 9 },
            "wrong-sequence" => hello with { Sequence = 9 },
            "wrong-provider" => hello with { Provider = "WhisperX" },
            _ => hello
        };
        await WorkerProtocol.WriteAsync(output, hello, CancellationToken.None);
        if (mode == "hello-only") return 0;
        if (mode == "duplicate") { await WorkerProtocol.WriteAsync(output, hello, CancellationToken.None); return 0; }
        var document = SyntheticFixture.Create(request.ProjectId, request.BaseRevision);
        if (mode == "forged-result") document = document with { Title = "Claimed model output" };
        var completed = request with { Type = "completed-fixture", Sequence = 2, Document = document };
        await WorkerProtocol.WriteAsync(output, completed, CancellationToken.None);
        if (mode == "extra") await WorkerProtocol.WriteAsync(output, completed, CancellationToken.None);
        return mode == "bad-exit" ? 4 : 0;
    }
}
