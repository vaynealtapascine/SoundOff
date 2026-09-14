using System.Diagnostics;
using System.Runtime.ExceptionServices;
using SoundOff.Core;

namespace SoundOff.Protocol;

// Private child process, no shell, ports, network, paths to media or access to the canonical DB.
// This boundary is resource/lifecycle containment, NOT an OS security sandbox.
public sealed class FixtureWorkerClient
{
    private readonly Func<ProcessStartInfo> startInfo;
    private readonly TimeSpan timeout;
    public FixtureWorkerClient(string? workerAssembly = null, TimeSpan? timeout = null)
        : this(() => ForAssembly(workerAssembly ?? Path.Combine(AppContext.BaseDirectory, "worker", "SoundOff.Worker.dll")), timeout) { }
    public FixtureWorkerClient(Func<ProcessStartInfo> startInfo, TimeSpan? timeout = null)
    { this.startInfo = startInfo; this.timeout = timeout ?? TimeSpan.FromSeconds(10); }

    public static ProcessStartInfo ForAssembly(string assembly)
    {
        if (!File.Exists(assembly)) throw new FileNotFoundException("The fixture worker is missing. Rebuild the desktop app with its worker folder.", assembly);
        var info = new ProcessStartInfo("dotnet"); info.ArgumentList.Add(Path.GetFullPath(assembly)); return info;
    }

    public async Task<Transcript> LoadAsync(Guid projectId, long baseRevision, CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty || baseRevision < 0) throw new ArgumentException("Invalid fixture request identity.");
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var info = startInfo();
        info.UseShellExecute = false; info.CreateNoWindow = true;
        info.RedirectStandardInput = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("Could not start the fixture worker.");
        using var killOnCancel = deadline.Token.Register(() => Kill(process));
        Exception? failure = null;
        async Task ObserveAsync(Task task)
        {
            try { await task; }
            catch (Exception error)
            {
                // Preserve the FIRST channel failure, not secondary pipe errors caused by killing it.
                if (!deadline.IsCancellationRequested)
                    Interlocked.CompareExchange(ref failure, error, null);
                deadline.Cancel();
                throw;
            }
        }
        try
        {
            var request = WorkerProtocol.Start(Guid.NewGuid(), projectId, baseRevision);
            var read = ExchangeAsync(process, request, deadline.Token);
            var diagnostics = DrainDiagnosticsAsync(process.StandardError.BaseStream, deadline.Token);
            var exit = process.WaitForExitAsync(deadline.Token);
            // All observers finish before the cancellation source is disposed.
            await Task.WhenAll(ObserveAsync(read), ObserveAsync(diagnostics), ObserveAsync(exit));
            if (process.ExitCode != 0) throw new InvalidDataException($"Fixture worker exited unsuccessfully ({process.ExitCode}).");
            return await read;
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            if (deadline.IsCancellationRequested)
                throw new TimeoutException("The fixture worker did not complete within its bounded execution window.");
            throw;
        }
        finally
        {
            Kill(process);
            await process.WaitForExitAsync();
        }
    }

    private static async Task<Transcript> ExchangeAsync(Process process, WorkerMessage request, CancellationToken token)
    {
        // A child that exits before reading breaks the pipe. That is a worker that refused the request, not an app I/O
        // fault, so it is reported as the protocol failure it is rather than as a raw broken-pipe error.
        try
        {
            await WorkerProtocol.WriteAsync(process.StandardInput.BaseStream, request, token);
            process.StandardInput.Close();
        }
        catch (IOException e) { throw new InvalidDataException("The fixture worker ended before accepting the request.", e); }
        var reader = new JsonLineReader(process.StandardOutput.BaseStream);
        var hello = await reader.ReadAsync(token) ?? throw new InvalidDataException("Worker exited without hello.");
        WorkerProtocol.ValidateIdentity(hello, request, "hello-fixture", 1);
        if (hello.Document is not null || hello.Error is not null) throw new InvalidDataException("Unexpected hello payload.");
        var completed = await reader.ReadAsync(token) ?? throw new InvalidDataException("Worker exited without completion.");
        WorkerProtocol.ValidateIdentity(completed, request, "completed-fixture", 2);
        if (completed.Error is not null || completed.Document is null) throw new InvalidDataException("Fixture completion is incomplete.");
        var expected = SyntheticFixture.Create(request.ProjectId, request.BaseRevision);
        if (DocumentJson.Serialize(completed.Document) != DocumentJson.Serialize(expected))
            throw new InvalidDataException("Worker result does not match synthetic fixture provenance/content.");
        if (await reader.ReadAsync(token) is not null) throw new InvalidDataException("Unexpected message after completion.");
        return completed.Document;
    }

    private static async Task DrainDiagnosticsAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[1024]; var total = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token); if (count == 0) return;
            total += count;
            if (total > WorkerProtocol.MaxDiagnosticBytes) throw new InvalidDataException("Worker diagnostics exceeded the byte limit.");
            // Deliberately discard: do not forward arbitrary child output or project text to logs.
        }
    }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
