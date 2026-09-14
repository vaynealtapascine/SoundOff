using System.Text.Json;
using SoundOff.Core;
using SoundOff.Protocol;

namespace SoundOff.Worker;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--self-test"]) return await SelfTestAsync();
        if (args.Length != 0)
        { Console.Error.WriteLine("Usage: SoundOff.Worker [--self-test]; otherwise stdin/stdout serve only the private synthetic-fixture protocol."); return 2; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await ServeAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), deadline.Token);
            return 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ArgumentException)
        { Console.Error.WriteLine("Fixture protocol failed; no transcript was produced."); return 1; }
    }

    private static async Task ServeAsync(Stream inputStream, Stream output, CancellationToken token)
    {
        var input = new JsonLineReader(inputStream);
        var request = await input.ReadAsync(token) ?? throw new InvalidDataException();
        WorkerProtocol.ValidateIdentity(request, request, "start-fixture", 0);
        if (request.Document is not null || request.Error is not null) throw new InvalidDataException();
        if (await input.ReadAsync(token) is not null) throw new InvalidDataException();
        await WorkerProtocol.WriteAsync(output, request with { Type = "hello-fixture", Sequence = 1 }, token);
        await WorkerProtocol.WriteAsync(output, request with { Type = "completed-fixture", Sequence = 2,
            Document = SyntheticFixture.Create(request.ProjectId, request.BaseRevision) }, token);
    }

    private static async Task<int> SelfTestAsync()
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var input = new MemoryStream(); using var output = new MemoryStream();
            var request = WorkerProtocol.Start(Guid.NewGuid(), Guid.NewGuid(), 0);
            await WorkerProtocol.WriteAsync(input, request, deadline.Token); input.Position = 0;
            await ServeAsync(input, output, deadline.Token); output.Position = 0;
            var reader = new JsonLineReader(output);
            var hello = await reader.ReadAsync(deadline.Token) ?? throw new InvalidDataException("Missing hello.");
            WorkerProtocol.ValidateIdentity(hello, request, "hello-fixture", 1);
            var completed = await reader.ReadAsync(deadline.Token) ?? throw new InvalidDataException("Missing completion.");
            WorkerProtocol.ValidateIdentity(completed, request, "completed-fixture", 2);
            if (completed.Document is null || hello.Document is not null || hello.Error is not null || completed.Error is not null ||
                DocumentJson.Serialize(completed.Document) != DocumentJson.Serialize(SyntheticFixture.Create(request.ProjectId, 0)) ||
                completed.Document.Blocks.Any(block => block.Timing is not null) || await reader.ReadAsync(deadline.Token) is not null)
                throw new InvalidDataException("Fixture self-test mismatch.");
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", provider = WorkerProtocol.Provider,
                inference = false, checks = new[] { "bounded-json-lines", "hello-completion-eof", "deterministic-synthetic-untimed-fixture" } }));
            return 0;
        }
        catch (Exception error)
        { Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "failed", error = error.Message })); return 1; }
    }
}
