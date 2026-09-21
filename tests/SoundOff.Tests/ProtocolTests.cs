using System.Diagnostics;
using System.Text;
using SoundOff.Core;
using SoundOff.Protocol;
using Xunit;

namespace SoundOff.Tests;

public sealed class ProtocolTests
{
    internal static ProcessStartInfo Helper(string mode, string? path = null)
    {
        var info = FixtureWorkerClient.ForAssembly(typeof(ProtocolTests).Assembly.Location);
        info.ArgumentList.Add(mode); if (path is not null) info.ArgumentList.Add(path);
        return info;
    }
    // A self-contained app must not need a .NET install just to make the demo project.
    [Fact] public void The_worker_is_started_through_its_own_launcher_when_one_is_published()
    {
        var assembly = typeof(ProtocolTests).Assembly.Location;
        var host = Path.ChangeExtension(Path.GetFullPath(assembly), OperatingSystem.IsWindows() ? ".exe" : null);
        var info = FixtureWorkerClient.ForAssembly(assembly);
        if (File.Exists(host))
        {
            Assert.Equal(host, info.FileName);
            Assert.Empty(info.ArgumentList);          // no assembly argument: the launcher knows its own
        }
        else
        {
            Assert.Equal("dotnet", info.FileName);
            Assert.Equal(Path.GetFullPath(assembly), Assert.Single(info.ArgumentList));
        }
        Assert.Throws<FileNotFoundException>(() => FixtureWorkerClient.ForAssembly(assembly + ".missing"));
    }

    [Fact] public async Task Real_child_worker_delivers_only_deterministic_fixture()
    {
        var id = Guid.NewGuid(); var result = await new FixtureWorkerClient().LoadAsync(id, 4);
        Assert.Equal(DocumentJson.Serialize(SyntheticFixture.Create(id, 4)), DocumentJson.Serialize(result));
    }

    [Theory]
    [InlineData("exit-zero")][InlineData("invalid-json")][InlineData("oversized")][InlineData("truncated")]
    [InlineData("wrong-run")][InlineData("wrong-project")][InlineData("wrong-revision")][InlineData("wrong-version")]
    [InlineData("wrong-sequence")][InlineData("wrong-provider")][InlineData("hello-only")][InlineData("duplicate")]
    [InlineData("forged-result")][InlineData("extra")][InlineData("bad-exit")][InlineData("stderr-overflow")]
    public async Task Supervisor_rejects_invalid_and_incomplete_child_output(string mode)
    {
        var client = new FixtureWorkerClient(() => Helper(mode), TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.LoadAsync(Guid.NewGuid(), 0));
    }

    [Fact] public async Task Hung_worker_times_out_and_is_reaped()
    {
        using var folder = new TestDirectory(); var pidPath = Path.Combine(folder.Root, "pid.txt");
        var client = new FixtureWorkerClient(() => Helper("hang", pidPath), TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<TimeoutException>(() => client.LoadAsync(Guid.NewGuid(), 0));
        Assert.True(File.Exists(pidPath)); AssertProcessGone(int.Parse(File.ReadAllText(pidPath)));
    }

    [Fact] public async Task User_cancellation_terminates_the_child()
    {
        using var folder = new TestDirectory(); var pidPath = Path.Combine(folder.Root, "pid.txt");
        using var cancel = new CancellationTokenSource();
        var task = new FixtureWorkerClient(() => Helper("hang", pidPath)).LoadAsync(Guid.NewGuid(), 0, cancel.Token);
        var watch = Stopwatch.StartNew();
        while (!File.Exists(pidPath) && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.True(File.Exists(pidPath)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); AssertProcessGone(int.Parse(File.ReadAllText(pidPath)));
    }
    private static void AssertProcessGone(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { }
    }

    [Fact] public async Task Framing_is_bounded_and_rejects_invalid_utf8_unterminated_and_unknown_fields()
    {
        foreach (var bytes in new[] { new byte[] { 0xff, 10 }, Encoding.UTF8.GetBytes("{}"), Encoding.UTF8.GetBytes("{\"unexpected\":true}\n"), new byte[WorkerProtocol.MaxLineBytes + 1] })
        {
            using var stream = new MemoryStream(bytes);
            await Assert.ThrowsAsync<InvalidDataException>(() => new JsonLineReader(stream).ReadAsync(CancellationToken.None));
        }
    }

    [Fact] public async Task Cross_process_writer_lock_releases_after_abrupt_owner_exit()
    {
        using var folder = new TestDirectory(); using (var store = ProjectStore.Create(folder.Project)) { }
        var info = Helper("hold-lock", folder.Project); info.UseShellExecute = false; info.RedirectStandardOutput = true; info.CreateNoWindow = true;
        using var child = Process.Start(info)!;
        try
        {
            Assert.Equal("locked", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Throws<ProjectLockedException>(() => ProjectStore.Open(folder.Project));
        }
        finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); }
        using var reopened = ProjectStore.Open(folder.Project); Assert.Equal(0, reopened.Read().Revision);
    }

    [Fact] public async Task Crash_inside_transaction_recovers_without_partial_edit_or_undo()
    {
        using var folder = new TestDirectory(); string before;
        using (var store = ProjectStore.Create(folder.Project))
        { var empty = store.Read(); before = DocumentJson.Serialize(store.LoadFixture(0, SyntheticFixture.Create(empty.ProjectId, 0))); }
        var info = Helper("crash-before-commit", folder.Project); info.UseShellExecute = false; info.CreateNoWindow = true;
        using var child = Process.Start(info)!; await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(77, child.ExitCode);
        using var recovered = ProjectStore.Open(folder.Project);
        Assert.Equal(before, DocumentJson.Serialize(recovered.Read()));
        Assert.Empty(recovered.Undo(recovered.Read().Revision).Blocks); Assert.False(recovered.CanUndo);
    }
}
