using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SoundOff.Core;

// Local, silent, bounded four-second decode jobs. Never starts an audio device or a per-frame process.
public sealed class FfmpegVideoPreviewDecoder : IVideoPreviewDecoder
{
    private readonly string ffmpeg, ffprobe;
    private readonly TimeSpan timeout;
    private int activeProcesses, startedProcesses;
    public int ActiveProcesses => Volatile.Read(ref activeProcesses);
    public int StartedProcesses => Volatile.Read(ref startedProcesses);
    public FfmpegVideoPreviewDecoder() : this(MediaTools.Ffmpeg, MediaTools.Ffprobe, TimeSpan.FromSeconds(15)) { }
    internal FfmpegVideoPreviewDecoder(string ffmpeg, string ffprobe, TimeSpan timeout)
    { this.ffmpeg = ffmpeg; this.ffprobe = ffprobe; this.timeout = timeout; }

    public async Task<VideoPreviewMedia> ProbeAsync(string path, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("The video is missing. Restore the project's owned media file.", path);
        var result = await RunAsync(ffprobe, ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_streams", "-of", "json", "--", path],
            async (stream, ct) => { using var reader = new StreamReader(stream); return await ReadTextAsync(reader, 262144, false, ct); }, token);
        using var json = JsonDocument.Parse(result.Output, new JsonDocumentOptions { MaxDepth = 16 });
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video" &&
            (!s.TryGetProperty("disposition", out var d) || !d.TryGetProperty("attached_pic", out var a) || a.GetInt32() == 0));
        if (video.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("This file has no moving-video stream (cover art is not a video).");
        var width = video.GetProperty("width").GetInt32(); var height = video.GetProperty("height").GetInt32();
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || (long)width * height > 8_847_360)
            throw new InvalidDataException("Preview supports video up to 4K pixel area. Make a smaller local copy for preview.");
        var start = Micro(video, "start_time"); var duration = Micro(video, "duration");
        if (duration <= 0 || duration > VideoPreviewLimits.MaxTimeMicroseconds || Math.Abs(start) > VideoPreviewLimits.MaxTimeMicroseconds)
            throw new InvalidDataException("Video timing is outside the supported seven-day range. Remux a local copy with valid timestamps.");
        // Same automatic audio selection, channel conversion and sample rate as DecodeToPcmAsync. -copyts lets us
        // measure where its first rendered PCM sample belongs in the container, including AAC priming/edit lists.
        var audio = await RunAsync(ffmpeg, ["-nostdin", "-hide_banner", "-nostats", "-v", "info", "-copyts", "-threads", "1",
            "-protocol_whitelist", "file,pipe", "-i", path, "-vn", "-sn", "-dn", "-ac", "1", "-ar", "22050",
            "-af", "aresample=22050,ashowinfo", "-frames:a", "1", "-c:a", "pcm_s16le", "-f", "null", "-"],
            async (stream, ct) => { using var reader = new StreamReader(stream); return await ReadTextAsync(reader, 4096, false, ct); }, token);
        var match = Regex.Match(audio.Error, @"\bn:0\s+pts:(-?\d+)\s+pts_time:", RegexOptions.CultureInvariant);
        if (!match.Success) throw new InvalidDataException("Cannot establish the audio/video time origin. Remux a local copy with an audio track and valid timestamps.");
        var origin = checked((long)Math.Round(decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 1_000_000 / MediaTools.ProxySampleRate));
        if (Math.Abs(origin) > VideoPreviewLimits.MaxTimeMicroseconds) throw new InvalidDataException("Audio timestamps exceed the supported seven-day range.");
        return new VideoPreviewMedia(path, video.GetProperty("index").GetInt32(), origin, start, checked(start + duration));
    }

    public async Task<VideoPreviewWindow> DecodeAsync(VideoPreviewMedia media, long windowStartMicroseconds, CancellationToken token)
    {
        var start = windowStartMicroseconds;
        if (start % VideoPreviewLimits.WindowMicroseconds != 0 || start < -VideoPreviewLimits.MaxTimeMicroseconds || start > 2 * VideoPreviewLimits.MaxTimeMicroseconds)
            throw new ArgumentOutOfRangeException(nameof(windowStartMicroseconds));
        if (start >= media.VideoEndMicroseconds || start + VideoPreviewLimits.WindowMicroseconds <= media.VideoStartMicroseconds)
            return new(start, []);
        var end = Math.Min(start + VideoPreviewLimits.WindowMicroseconds, media.VideoEndMicroseconds);
        // Preserve keyframe preroll: accurate input seeking would drop the preceding VFR frame. fps round=up holds
        // each source frame until its next timestamp, on an absolute 100-ms grid; it never uses avg_frame_rate.
        var filter = $"fps=fps=10:start_time={Seconds(start)}:round=up:eof_action=pass,trim=start={Seconds(start)}:end={Seconds(end)}," +
            "scale=w='min(640,iw*sar)':h='min(360,ih)':force_original_aspect_ratio=decrease,setsar=1," +
            "pad=640:360:(ow-iw)/2:(oh-ih)/2:color=black,format=bgra";
        var decoded = await RunAsync(ffmpeg, ["-nostdin", "-hide_banner", "-nostats", "-v", "error", "-copyts", "-noaccurate_seek",
            "-seek_timestamp", "1", "-ss", Seconds(Math.Max(start, media.VideoStartMicroseconds)), "-threads", "1", "-filter_threads", "1",
            "-protocol_whitelist", "file,pipe", "-i", media.Path, "-map", $"0:{media.StreamIndex}", "-an", "-sn", "-dn", "-vf", filter,
            "-frames:v", "40", "-fps_mode", "passthrough", "-threads:v", "1", "-c:v", "rawvideo", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"],
            async (stream, ct) =>
            {
                var frames = new List<VideoPreviewFrame>();
                for (var i = 0; i < VideoPreviewLimits.FramesPerWindow; i++)
                {
                    var bytes = new byte[VideoPreviewLimits.FrameBytes]; var read = 0;
                    while (read < bytes.Length)
                    {
                        var n = await stream.ReadAsync(bytes.AsMemory(read), ct).ConfigureAwait(false);
                        if (n == 0) break;
                        read += n;
                    }
                    if (read == 0) break;
                    if (read != bytes.Length) throw new InvalidDataException("The decoder returned an incomplete video frame. Check the local file for damage.");
                    frames.Add(new(start + i * VideoPreviewLimits.FrameMicroseconds, bytes));
                }
                var extra = new byte[1];
                if (await stream.ReadAsync(extra, ct).ConfigureAwait(false) != 0) throw new InvalidDataException("The video decoder exceeded its frame limit.");
                return frames;
            }, token).ConfigureAwait(false);
        if (decoded.Output.Count == 0) throw new InvalidDataException("No frames could be decoded at this position. Try a different seek or remux a local copy.");
        return new(start, decoded.Output);
    }

    private async Task<(T Output, string Error)> RunAsync<T>(string tool, string[] args, Func<Stream, CancellationToken, Task<T>> read, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
        var ct = deadline.Token;
        var info = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        ct.ThrowIfCancellationRequested();
        try { if (!process.Start()) throw new IOException("The video decoder did not start."); }
        catch (System.ComponentModel.Win32Exception e) { throw new IOException("Install FFmpeg (ffmpeg and ffprobe), or set SOUNDOFF_FFMPEG_DIR to its bin folder and restart SoundOff.", e); }
        Interlocked.Increment(ref activeProcesses); Interlocked.Increment(ref startedProcesses);
        using var cancel = ct.Register(() => Kill(process));
        var output = read(process.StandardOutput.BaseStream, ct);
        var error = ReadTextAsync(process.StandardError, 16384, true, ct);
        try
        {
            // If either pipe fails, terminate immediately rather than waiting for a child blocked on that pipe.
            var wait = process.WaitForExitAsync(ct);
            var first = await Task.WhenAny(output, error, wait).ConfigureAwait(false);
            if (first.IsFaulted || first.IsCanceled) await first.ConfigureAwait(false);
            var value = await output.ConfigureAwait(false);
            await wait.ConfigureAwait(false); var diagnostic = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidDataException("FFmpeg could not read this video. Check the file/codec or remux a local copy. " + diagnostic.Trim()[..Math.Min(400, diagnostic.Trim().Length)]);
            return (value, diagnostic);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new IOException($"Video decoding exceeded {timeout.TotalSeconds:0.#} seconds. Try a smaller local copy or a file with shorter keyframe intervals."); }
        finally
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await output.ConfigureAwait(false); } catch (Exception) { }
            try { await error.ConfigureAwait(false); } catch (Exception) { }
            Interlocked.Decrement(ref activeProcesses);
        }
    }
    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
    private static async Task<string> ReadTextAsync(StreamReader reader, int limit, bool tail, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[2048]; int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            text.Append(buffer, 0, n);
            if (text.Length <= limit) continue;
            if (!tail) throw new InvalidDataException("Video metadata exceeds the safety limit.");
            text.Remove(0, text.Length - limit);
        }
        return text.ToString();
    }
    private static long Micro(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || !decimal.TryParse(field.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            seconds < -VideoPreviewLimits.MaxTimeMicroseconds / 1_000_000 || seconds > VideoPreviewLimits.MaxTimeMicroseconds / 1_000_000)
            throw new InvalidDataException($"The video has no usable {name}. Remux a local MP4 copy with valid stream timestamps.");
        return (long)Math.Round(seconds * 1_000_000);
    }
    private static string Seconds(long value) => (value / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture);
}
