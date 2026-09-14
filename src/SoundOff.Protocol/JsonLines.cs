using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoundOff.Core;

namespace SoundOff.Protocol;

public sealed record WorkerMessage([property: JsonRequired] int Version, [property: JsonRequired] string Type,
    [property: JsonRequired] Guid JobId, [property: JsonRequired] long Sequence, [property: JsonRequired] Guid ProjectId,
    [property: JsonRequired] long BaseRevision, [property: JsonRequired] string Provider,
    Transcript? Document = null, string? Error = null);

public static class WorkerProtocol
{
    public const int Version = 1;
    public const int MaxLineBytes = 256 * 1024;
    public const int MaxDiagnosticBytes = 16 * 1024;
    public const string Provider = "soundoff-demo-v1";
    public static WorkerMessage Start(Guid job, Guid project, long revision) => new(Version, "start-fixture", job, 0, project, revision, Provider);

    public static void ValidateIdentity(WorkerMessage message, WorkerMessage request, string type, long sequence)
    {
        if (message.Version != Version || message.JobId != request.JobId || message.JobId == Guid.Empty ||
            message.ProjectId != request.ProjectId || message.ProjectId == Guid.Empty || message.BaseRevision != request.BaseRevision ||
            message.BaseRevision < 0 || message.Sequence != sequence || message.Type != type || message.Provider != Provider)
            throw new InvalidDataException("Worker protocol identity, version, type, or sequence mismatch.");
    }

    public static async Task WriteAsync(Stream stream, WorkerMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, DocumentJson.Options);
        if (bytes.Length > MaxLineBytes) throw new InvalidDataException("Worker message exceeds the byte limit.");
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.WriteAsync(new byte[] { 10 }, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

// Incremental byte framing, bounded BEFORE deserialization. Never ReadLine on untrusted stdout.
public sealed class JsonLineReader(Stream stream)
{
    private readonly byte[] buffer = new byte[4096];
    private int offset;
    private int count;
    public async Task<WorkerMessage?> ReadAsync(CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (offset == count)
            {
                count = await stream.ReadAsync(buffer, cancellationToken); offset = 0;
                if (count == 0)
                {
                    if (line.Length != 0) throw new InvalidDataException("Worker ended with an unterminated JSON line.");
                    return null;
                }
            }
            var value = buffer[offset++];
            if (value == 10)
            {
                if (line.Length == 0) throw new InvalidDataException("Empty worker message.");
                try
                {
                    // Strict UTF-8 and strict property schema; malformed payloads cannot become proposals.
                    var json = new UTF8Encoding(false, true).GetString(line.ToArray());
                    return DocumentJson.ReadStrict<WorkerMessage>(json, WorkerProtocol.MaxLineBytes);
                }
                catch (Exception e) when (e is JsonException or DecoderFallbackException)
                { throw new InvalidDataException("Invalid worker JSON/UTF-8.", e); }
            }
            if (line.Length >= WorkerProtocol.MaxLineBytes) throw new InvalidDataException("Worker message exceeds the byte limit.");
            line.WriteByte(value);
        }
    }
}
