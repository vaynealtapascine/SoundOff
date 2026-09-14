using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoundOff.Core;

// Owned media beside the project: <name>.soundoff.media/media/<sha16>.<ext>. The original file is never modified; the copy is
// hashed while it is written, probed with ffprobe (content, not extension) and moved into place atomically.
public sealed record MediaAsset(string Id, string OriginalName, string RelativePath, string Sha256, long Bytes, long? DurationMicroseconds,
    string ProbeJson, string ImportedUtc);

public sealed record MediaStream([property: JsonRequired] string CodecType, string? CodecName, string? SampleRate, int? Channels, int? Width, int? Height);
public sealed record MediaProbe([property: JsonRequired] string FormatName, [property: JsonRequired] double DurationSeconds, [property: JsonRequired] IReadOnlyList<MediaStream> Streams)
{
    public bool HasAudio => Streams.Any(s => s.CodecType == "audio");
    public bool HasVideo => Streams.Any(s => s.CodecType == "video");
}

public static class MediaTools
{
    // Resolved from SOUNDOFF_FFMPEG_DIR, then PATH. Packaging will bundle a pinned build; development uses the installed one.
    public static string Ffprobe => Resolve("ffprobe");
    public static string Ffmpeg => Resolve("ffmpeg");
    private static string Resolve(string tool)
    {
        var dir = Environment.GetEnvironmentVariable("SOUNDOFF_FFMPEG_DIR");
        if (!string.IsNullOrWhiteSpace(dir)) return Path.Combine(dir, tool + (OperatingSystem.IsWindows() ? ".exe" : ""));
        return tool;
    }

    // A regenerable playback proxy: 16-bit PCM mono at 22.05 kHz, the same decoder the transcription worker uses, so the
    // proxy's time base matches the source exactly. Resampling changes sample rate, never duration, so stored timing stays valid.
    public const int ProxySampleRate = 22050;
    public static async Task DecodeToPcmAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var staging = destinationPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        var info = new ProcessStartInfo(Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // -f wav is explicit because the staging name ends in .tmp and ffmpeg would otherwise infer the format from it.
        foreach (var arg in new[] { "-nostdin", "-v", "error", "-i", Path.GetFullPath(sourcePath), "-vn", "-ac", "1", "-ar", ProxySampleRate.ToString(), "-c:a", "pcm_s16le", "-f", "wav", "-y", staging })
            info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        try
        {
            try { if (!process.Start()) throw new IOException("ffmpeg could not be started."); }
            catch (System.ComponentModel.Win32Exception e) { throw new IOException("ffmpeg is not available. Install FFmpeg or set SOUNDOFF_FFMPEG_DIR.", e); }
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) throw new InvalidDataException("The recording could not be decoded for playback: " + Truncate((await error).Trim(), 300));
            File.Move(staging, destinationPath, overwrite: true);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    // Test helper: produces a compressed file so the proxy path can be exercised against a real encoder.
    internal static async Task DecodeToMp3ForTestAsync(string sourcePath, string destinationPath)
    {
        var info = new ProcessStartInfo(Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-nostdin", "-v", "error", "-i", Path.GetFullPath(sourcePath), "-c:a", "libmp3lame", "-y", Path.GetFullPath(destinationPath) })
            info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        process.Start();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidDataException("ffmpeg could not produce the test mp3: " + error);
    }

    public static async Task<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(Ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "format=format_name,duration:stream=codec_type,codec_name,sample_rate,channels,width,height", "-of", "json", "--", Path.GetFullPath(path) })
            info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        try { if (!process.Start()) throw new IOException("ffprobe could not be started."); }
        catch (System.ComponentModel.Win32Exception e) { throw new IOException("ffprobe is not available. Install FFmpeg or set SOUNDOFF_FFMPEG_DIR.", e); }
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken); var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidDataException("This file could not be read as media: " + Truncate((await error).Trim(), 300));
        return Parse(await output);
    }

    public static MediaProbe Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (!root.TryGetProperty("format", out var format)) throw new InvalidDataException("ffprobe reported no container format.");
        var name = format.TryGetProperty("format_name", out var fn) ? fn.GetString() ?? "" : "";
        var duration = format.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
        var streams = new List<MediaStream>();
        if (root.TryGetProperty("streams", out var list))
            foreach (var stream in list.EnumerateArray())
                streams.Add(new MediaStream(Text(stream, "codec_type") ?? "", Text(stream, "codec_name"), Text(stream, "sample_rate"), Int(stream, "channels"), Int(stream, "width"), Int(stream, "height")));
        var probe = new MediaProbe(name, duration, streams);
        if (!probe.HasAudio) throw new InvalidDataException("The file has no audio stream to transcribe.");
        if (!(duration > 0) || double.IsNaN(duration) || double.IsInfinity(duration)) throw new InvalidDataException("The media duration is unknown; the file may be truncated.");
        return probe;
    }
    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

public static class MediaImport
{
    public const long MaxBytes = 16L * 1024 * 1024 * 1024;

    public static async Task<MediaAsset> ImportAsync(ProjectStore store, string sourcePath, CancellationToken cancellationToken)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        var source = new FileInfo(sourcePath);
        if (!source.Exists) throw new FileNotFoundException("The media file does not exist.", sourcePath);
        if (source.Length == 0 || source.Length > MaxBytes) throw new InvalidDataException("The media file is empty or larger than the 16 GiB limit.");
        var probe = await MediaTools.ProbeAsync(sourcePath, cancellationToken); // content check before any copy
        var mediaDir = Path.Combine(store.MediaDirectory, "media"); Directory.CreateDirectory(mediaDir);
        var staging = Path.Combine(mediaDir, "." + Guid.NewGuid().ToString("N") + ".importing");
        string sha;
        try
        {
            using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1024 * 1024]; int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0) { await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken); hash.AppendData(buffer, 0, read); }
                await output.FlushAsync(cancellationToken); output.Flush(true);
                sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (extension.Length > 8 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = ".media";
            var relative = Path.Combine("media", sha[..16] + extension);
            var destination = Path.Combine(store.MediaDirectory, relative);
            if (File.Exists(destination))
            {
                // Same bytes already owned by this project: reuse the copy, still record a fresh asset row.
                File.Delete(staging);
            }
            else File.Move(staging, destination);
            var copyProbe = await MediaTools.ProbeAsync(destination, cancellationToken);
            if (Math.Abs(copyProbe.DurationSeconds - probe.DurationSeconds) > 0.001) throw new InvalidDataException("The copied media does not match the original.");
            var asset = new MediaAsset(Guid.NewGuid().ToString("N"), Path.GetFileName(sourcePath), relative, sha, source.Length,
                InferenceImportMicro(probe.DurationSeconds), JsonSerializer.Serialize(probe, DocumentJson.Options), DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
            store.AddMediaAsset(asset);
            return asset;
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    // Takes ownership of a file the app itself just wrote inside the project (a finished recording). It is probed and
    // hashed in place and then renamed, so a long take is never stored twice just to satisfy the import contract.
    public static async Task<MediaAsset> AdoptAsync(ProjectStore store, string ownedPath, string originalName, CancellationToken cancellationToken)
    {
        ownedPath = Path.GetFullPath(ownedPath);
        var mediaDir = Path.Combine(store.MediaDirectory, "media");
        if (!ownedPath.StartsWith(Path.GetFullPath(store.MediaDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only a file already inside this project's media directory can be adopted.");
        var source = new FileInfo(ownedPath);
        if (!source.Exists || source.Length == 0) throw new InvalidDataException("The recording is missing or empty.");
        var probe = await MediaTools.ProbeAsync(ownedPath, cancellationToken);
        Directory.CreateDirectory(mediaDir);
        string sha;
        using (var stream = new FileStream(ownedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        var extension = Path.GetExtension(ownedPath).ToLowerInvariant();
        if (extension.Length > 8 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = ".media";
        var relative = Path.Combine("media", sha[..16] + extension);
        var destination = Path.Combine(store.MediaDirectory, relative);
        if (File.Exists(destination)) File.Delete(ownedPath);   // identical bytes already owned
        else File.Move(ownedPath, destination);
        var asset = new MediaAsset(Guid.NewGuid().ToString("N"), originalName, relative, sha, source.Length,
            InferenceImportMicro(probe.DurationSeconds), JsonSerializer.Serialize(probe, DocumentJson.Options), DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
        store.AddMediaAsset(asset);
        return asset;
    }

    private static long InferenceImportMicro(double seconds) => (long)Math.Round(seconds * 1_000_000.0);
}
