using System.Diagnostics;
using System.Text.Json;
using NAudio.Utils;
using NAudio.Wave;
using SoundOff.Core;

namespace SoundOff.Desktop;

internal sealed class CombinedCaptureEngine : ICombinedCaptureEngine
{
    private readonly object gate = new();
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly Func<CaptureMode, string?, ITimestampedCaptureSource> open;
    private readonly Func<long> now;
    private readonly TimeSpan joinTimeout;
    private readonly CancellationTokenSource disposing = new();
    private Session? session;
    private RecordingState state = RecordingState.Idle;
    private bool disposed;
    private string? failure;
    private long completedDuration;
    internal CombinedCaptureEngine(Func<CaptureMode, string?, ITimestampedCaptureSource> open, Func<long>? now = null, TimeSpan? joinTimeout = null)
    {
        this.open = open;
        this.now = now ?? (() => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency)));
        this.joinTimeout = joinTimeout ?? TimeSpan.FromSeconds(5);
    }
    private sealed class Source(string key, CaptureMode mode, string? id)
    {
        public readonly string Key = key;
        public readonly CaptureMode Mode = mode;
        public readonly string? Id = id;
        public string Name = key;
        public long Frames;
        public double Peak;
        public readonly TaskCompletionSource Prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Task = Task.CompletedTask;
    }
    private sealed class Session(string path, string folder, FileStream output, string? mic, string? render)
    {
        public readonly string Path = path, Folder = folder;
        public readonly FileStream Output = output;
        public readonly CaptureTimeline Timeline = new();
        public readonly CancellationTokenSource Cancel = new();
        public readonly ManualResetEventSlim Begin = new();
        public readonly Source[] Sources = [new("microphone", CaptureMode.Microphone, mic), new("system", CaptureMode.SystemAudio, render)];
        public readonly List<CaptureGap> Gaps = [];
        public CancellationTokenRegistration Registration;
        public Task All => Task.WhenAll(Sources.Select(s => s.Task));
        public void Journal(string status, long tick, string? reason = null)
        {
            // Control-thread only. Append rather than overwrite the last recoverable session metadata.
            File.AppendAllText(System.IO.Path.Combine(Folder, "session.ndjson"), JsonSerializer.Serialize(new
            {
                schema = 1, status, qpc100ns = tick, utc = DateTime.UtcNow, reason, spans = Timeline.Snapshot(),
                sources = Sources.Select(s => new { s.Key, s.Id, s.Name, frames = Interlocked.Read(ref s.Frames) }),
                output = System.IO.Path.GetFileName(Path), scope = "selected-render-endpoint-whole-computer", echoCancellation = false
            }) + Environment.NewLine);
        }
    }
    public RecordingState State { get { lock (gate) return state; } }
    public string? FailureReason { get { lock (gate) return failure; } }
    public long RecordedMicroseconds { get { lock (gate) return state == RecordingState.Completed ? completedDuration : session?.Timeline.Duration(now()) / 10 ?? 0; } }
    public double PeakLevel { get { lock (gate) return state == RecordingState.Recording && session is { } s ? s.Sources.Max(x => Volatile.Read(ref x.Peak)) : 0; } }
    public event EventHandler? Changed;
    // Never execute client code on a native producer thread or under an operation/state lock.
    private void Announce() => ThreadPool.QueueUserWorkItem(_ => Changed?.Invoke(this, EventArgs.Empty));
    public IReadOnlyList<CaptureDevice> Devices(CaptureMode mode) => throw new NotSupportedException("Enumerate through WindowsCaptureEngine.");
    public void Start(CaptureMode mode, string? deviceId, string destinationPath) => throw new ArgumentException("Use StartCombined with separate source IDs.");
    private void Enter()
    {
        if (!operation.Wait(joinTimeout + joinTimeout)) throw new TimeoutException("Another capture operation is still finishing; retained files were not touched.");
    }
    public void StartCombined(string? microphoneId, string? renderEndpointId, string destinationPath, CancellationToken cancellation = default)
    {
        Enter();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); cancellation.ThrowIfCancellationRequested();
            lock (gate) if (session is not null) throw new InvalidOperationException("Stop and keep the current combined recording first.");
            RecordingRules.RequireWritableSpace(destinationPath);
            var path = Path.GetFullPath(destinationPath);
            var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            Session s;
            try
            {
                // Valid empty output from the outset; originals and maps are continuously checkpointed separately.
                using (var header = new WaveFileWriter(new IgnoreDisposeStream(output), new WaveFormat(48000, 16, 1))) header.Flush();
                var folder = path + ".sources-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(folder);
                s = new Session(path, folder, output, microphoneId, renderEndpointId);
                s.Journal("preparing", now());
                lock (gate) { session = s; state = RecordingState.Ready; failure = null; completedDuration = 0; }
            }
            catch { output.Dispose(); throw; }
            try
            {
                s.Registration = cancellation.Register(() => Interrupt(s, "Combined capture was cancelled; both sources stopped."));
                foreach (var source in s.Sources)
                    source.Task = Task.Factory.StartNew(() => Produce(s, source), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Task.WhenAll(s.Sources.Select(x => x.Prepared.Task)).WaitAsync(joinTimeout, cancellation).GetAwaiter().GetResult();
                lock (gate)
                {
                    s.Cancel.Token.ThrowIfCancellationRequested();
                    s.Timeline.Resume(now()); state = RecordingState.Recording;
                }
                s.Journal("recording", now()); s.Begin.Set();
                Task.WhenAll(s.Sources.Select(x => x.Started.Task)).WaitAsync(joinTimeout, cancellation).GetAwaiter().GetResult();
                if (State == RecordingState.Interrupted) throw new IOException(FailureReason);
            }
            catch (Exception e)
            {
                Interrupt(s, "Combined capture could not start both sources: " + e.Message);
                try { s.All.WaitAsync(joinTimeout).GetAwaiter().GetResult(); } catch (Exception) { }
                TryJournal(s, "interrupted");
                if (e is OperationCanceledException && cancellation.IsCancellationRequested) throw;
                throw new IOException(FailureReason + " Retained sources: " + s.Folder, e);
            }
        }
        finally { operation.Release(); Announce(); }
    }
    private void Produce(Session s, Source source)
    {
        CaptureSourceArchive? archive = null;
        try
        {
            s.Cancel.Token.ThrowIfCancellationRequested();
            using var native = open(source.Mode, source.Id);
            source.Name = native.Name;
            archive = new CaptureSourceArchive(s.Folder, source.Key, native.Format, s.Timeline);
            source.Prepared.TrySetResult();
            s.Begin.Wait(s.Cancel.Token);
            native.Run(packet =>
            {
                archive.Push(packet);
                Interlocked.Exchange(ref source.Frames, archive.Frames);
                Volatile.Write(ref source.Peak, archive.Peak);
            }, () => source.Started.TrySetResult(), s.Cancel.Token);
            if (!s.Cancel.IsCancellationRequested) throw new IOException("Source stopped without a stop request.");
        }
        catch (OperationCanceledException) when (s.Cancel.IsCancellationRequested) { }
        catch (Exception e) { Interrupt(s, source.Key + " interrupted: " + e.Message); }
        finally
        {
            try { archive?.Dispose(); }
            catch (Exception e) { Interrupt(s, source.Key + " source finalization failed: " + e.Message); }
            if (archive is not null) Interlocked.Exchange(ref source.Frames, archive.Frames);
            // Signal failure without unobserved faulted TCS tasks on the partially-prepared path.
            source.Prepared.TrySetCanceled(); source.Started.TrySetCanceled();
        }
    }
    private void Interrupt(Session s, string reason)
    {
        lock (gate)
        {
            if (session != s || state == RecordingState.Completed) return;
            failure ??= reason;
            s.Timeline.Pause(now());
            if (state != RecordingState.Stopping) state = RecordingState.Interrupted;
        }
        s.Cancel.Cancel(); Announce();
    }
    private void TryJournal(Session s, string status)
    {
        try { s.Journal(status, now(), FailureReason); }
        catch (Exception e) { lock (gate) failure ??= "Recovery metadata could not be written: " + e.Message; }
    }
    public void Pause()
    {
        Enter();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Session s;
            lock (gate) { if (state != RecordingState.Recording) return; s = session!; s.Timeline.Pause(now()); state = RecordingState.Paused; }
            try { s.Journal("paused", now()); } catch (Exception e) { Interrupt(s, "Pause metadata failed: " + e.Message); }
        }
        finally { operation.Release(); Announce(); }
    }
    public void Resume()
    {
        Enter();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Session s;
            lock (gate)
            {
                if (state != RecordingState.Paused) return;
                s = session!;
                if (s.Gaps.Count >= 10000) throw new IOException("Pause marker limit reached; stop and keep this take.");
                s.Gaps.Add(new(s.Timeline.Duration(now()) / 10, DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")));
                s.Timeline.Resume(now()); state = RecordingState.Recording;
            }
            try { s.Journal("resumed", now()); } catch (Exception e) { Interrupt(s, "Resume metadata failed: " + e.Message); }
        }
        finally { operation.Release(); Announce(); }
    }
    public RecordingResult Stop() => StopCombined();
    public RecordingResult StopCombined(CancellationToken cancellation = default)
    {
        Enter();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Session s;
            lock (gate)
            {
                s = session ?? throw new InvalidOperationException("Nothing is being recorded.");
                s.Timeline.Pause(now()); state = RecordingState.Stopping;
            }
            s.Cancel.Cancel(); Announce();
            try
            {
                // Always request BOTH stops, including when the caller has already cancelled finalization.
                s.All.WaitAsync(joinTimeout).GetAwaiter().GetResult();
                s.Registration.Dispose();
                if (s.Sources.Any(x => x.Frames == 0)) Interrupt(s, "At least one source captured no samples. This is an interrupted partial take, not successful combined capture.");
                TryJournal(s, "finalizing");
                using var finalize = CancellationTokenSource.CreateLinkedTokenSource(cancellation, disposing.Token);
                finalize.Token.ThrowIfCancellationRequested();
                // Missing source after partial open is represented as silence ONLY in a visibly interrupted result.
                foreach (var source in s.Sources)
                    if (!File.Exists(Path.Combine(s.Folder, source.Key + ".wav")))
                        using (new CaptureSourceArchive(s.Folder, source.Key, new WaveFormat(48000, 16, 1), s.Timeline)) { }
                s.Output.Position = 0; s.Output.SetLength(0);
                var duration = CaptureMixdown.Write(s.Folder, new IgnoreDisposeStream(s.Output), s.Sources.All(x => x.Frames == 0) ? 0 : s.Timeline.Duration(now()), finalize.Token);
                s.Output.Flush(true);
                TryJournal(s, FailureReason is null ? "completed" : "interrupted-partial");
                var result = new RecordingResult(s.Path, duration, CaptureMode.Combined, string.Join(" + ", s.Sources.Select(x => x.Name)),
                    s.Gaps.ToArray(), FailureReason is not null, FailureReason) { RecoveryDirectory = s.Folder };
                s.Output.Dispose(); s.Cancel.Dispose(); s.Begin.Dispose();
                lock (gate) { completedDuration = duration; session = null; state = RecordingState.Completed; }
                return result;
            }
            catch (Exception e)
            {
                Interrupt(s, "Combined finalization did not complete: " + e.Message);
                lock (gate) state = RecordingState.Interrupted;
                TryJournal(s, "finalization-incomplete");
                // Output handle and source files remain owned for retry. Do not dispose a live native thread.
                throw;
            }
        }
        finally { operation.Release(); Announce(); }
    }
    public void Dispose()
    {
        disposing.Cancel(); // interrupt a concurrent long mix before waiting for its operation lock
        Enter();
        try
        {
            if (disposed) return; disposed = true;
            Session? s; lock (gate) s = session;
            if (s is null) return;
            Interrupt(s, "Capture disposed before adoption; retained source files and maps require recovery.");
            s.Registration.Dispose();
            try { s.All.WaitAsync(joinTimeout).GetAwaiter().GetResult(); } catch (Exception) { }
            TryJournal(s, "disposed-recoverable"); s.Output.Dispose();
            // A broken driver can outlive the join deadline. The producer retains its OWN native/archive
            // resources until it returns; never tear them down under it or block UI disposal forever.
            _ = s.All.ContinueWith(_ => { s.Cancel.Dispose(); s.Begin.Dispose(); }, TaskScheduler.Default);
        }
        finally { operation.Release(); }
    }
}
