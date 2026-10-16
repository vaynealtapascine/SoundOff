package com.soundoff.android;

import android.content.Context;
import android.media.*;
import android.net.Uri;
import android.os.SystemClock;
import java.io.*;
import java.nio.ByteBuffer;
import java.util.function.BooleanSupplier;

final class AudioImporter {
    interface Progress { void duration(long microseconds); }
    static void decode(Context context, Uri uri, File staging, BooleanSupplier cancelled,
                       PcmResampler.Sink sink, Progress progress) throws IOException {
        MediaExtractor extractor = new MediaExtractor();
        MediaCodec decoder = null;
        try {
            // Copy first: document providers can return non-seekable streams, unlike MediaExtractor.
            try (InputStream in = context.getContentResolver().openInputStream(uri); FileOutputStream out = new FileOutputStream(staging)) {
                if (in == null) throw new IOException("Cannot open the selected recording");
                byte[] buffer = new byte[65536]; int n; long total = 0;
                while ((n = in.read(buffer)) != -1) {
                    if (cancelled.getAsBoolean()) throw new IOException("Import cancelled");
                    total += n;
                    if (total > 2_000_000_000L) throw new IOException("Audio files must be smaller than 2 GB");
                    out.write(buffer, 0, n);
                }
            }
            extractor.setDataSource(staging.getAbsolutePath());
            MediaFormat input = null;
            for (int i = 0; i < extractor.getTrackCount(); i++) {
                MediaFormat format = extractor.getTrackFormat(i);
                String mime = format.getString(MediaFormat.KEY_MIME);
                if (mime != null && mime.startsWith("audio/")) { input = format; extractor.selectTrack(i); break; }
            }
            if (input == null) throw new IOException("No supported audio track found");
            if (input.containsKey(MediaFormat.KEY_DURATION)) {
                long duration = input.getLong(MediaFormat.KEY_DURATION);
                if (duration > 4L * 60 * 60 * 1_000_000) throw new IOException("Recordings must be four hours or shorter");
                progress.duration(duration);
            }
            String mime = input.getString(MediaFormat.KEY_MIME);
            if ("audio/raw".equals(mime)) {
                PcmResampler resampler = resampler(input, sink);
                ByteBuffer buffer = ByteBuffer.allocateDirect(1024 * 1024);
                while (!cancelled.getAsBoolean()) {
                    buffer.clear(); int n = extractor.readSampleData(buffer, 0);
                    if (n < 0) break;
                    buffer.position(0); buffer.limit(n); resampler.accept(buffer); extractor.advance();
                }
                resampler.flush();
            } else {
                input.setInteger(MediaFormat.KEY_PCM_ENCODING, AudioFormat.ENCODING_PCM_16BIT);
                decoder = MediaCodec.createDecoderByType(mime); decoder.configure(input, null, null, 0); decoder.start();
                PcmResampler resampler = null;
                boolean inputDone = false, outputDone = false;
                MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
                long lastProgress = SystemClock.elapsedRealtime();
                while (!outputDone && !cancelled.getAsBoolean()) {
                    if (!inputDone) {
                        int index = decoder.dequeueInputBuffer(10000);
                        if (index >= 0) {
                            ByteBuffer buffer = decoder.getInputBuffer(index); buffer.clear();
                            int n = extractor.readSampleData(buffer, 0);
                            if (n < 0) { decoder.queueInputBuffer(index, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM); inputDone = true; }
                            else { decoder.queueInputBuffer(index, 0, n, extractor.getSampleTime(), 0); extractor.advance(); }
                            lastProgress = SystemClock.elapsedRealtime();
                        }
                    }
                    int index = decoder.dequeueOutputBuffer(info, 10000);
                    if (index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                        if (resampler != null) throw new IOException("Audio format changed midway through the recording");
                        resampler = resampler(decoder.getOutputFormat(), sink);
                    } else if (index >= 0) {
                        try {
                            if (info.size > 0 && (info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) == 0) {
                                if (resampler == null) resampler = resampler(decoder.getOutputFormat(), sink);
                                ByteBuffer buffer = decoder.getOutputBuffer(index);
                                buffer.position(info.offset); buffer.limit(info.offset + info.size); resampler.accept(buffer);
                            }
                            outputDone = (info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0;
                            lastProgress = SystemClock.elapsedRealtime();
                        } finally { decoder.releaseOutputBuffer(index, false); }
                    }
                    if (SystemClock.elapsedRealtime() - lastProgress > 30000) throw new IOException("Audio decoder stopped responding");
                }
                if (resampler != null) resampler.flush();
            }
            if (cancelled.getAsBoolean()) throw new IOException("Import cancelled");
        } catch (RuntimeException e) { throw new IOException("Cannot decode this audio file: " + e.getMessage(), e); }
        finally {
            if (decoder != null) { try { decoder.stop(); } catch (Exception ignored) {} decoder.release(); }
            extractor.release(); staging.delete();
        }
    }
    private static PcmResampler resampler(MediaFormat format, PcmResampler.Sink sink) throws IOException {
        return new PcmResampler(format.getInteger(MediaFormat.KEY_SAMPLE_RATE), format.getInteger(MediaFormat.KEY_CHANNEL_COUNT),
            format.containsKey(MediaFormat.KEY_PCM_ENCODING) ? format.getInteger(MediaFormat.KEY_PCM_ENCODING) : AudioFormat.ENCODING_PCM_16BIT, sink);
    }
}
