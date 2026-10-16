package com.soundoff.android;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/** Streaming channel downmix and linear resampling; state survives decoder buffer boundaries. */
final class PcmResampler {
    interface Sink { void write(short[] pcm, int count) throws IOException; }
    private final int rate, channels, encoding;
    private final Sink sink;
    private final short[] output = new short[1600];
    private int used;
    private long frame, nextPosition;
    private float previous;
    PcmResampler(int rate, int channels, int encoding, Sink sink) throws IOException {
        if (rate < 8000 || rate > 192000 || channels < 1 || channels > 8 || (encoding != 2 && encoding != 4))
            throw new IOException("Unsupported decoded audio format (use 16-bit WAV, MP3, or M4A)");
        this.rate = rate; this.channels = channels; this.encoding = encoding; this.sink = sink;
    }
    void accept(ByteBuffer pcm) throws IOException {
        pcm.order(ByteOrder.LITTLE_ENDIAN);
        int bytesPerFrame = channels * (encoding == 4 ? 4 : 2);
        if (pcm.remaining() % bytesPerFrame != 0) throw new IOException("Incomplete PCM frame");
        while (pcm.hasRemaining()) {
            float sample = 0;
            for (int c = 0; c < channels; c++) sample += encoding == 4 ? pcm.getFloat() : pcm.getShort() / 32768f;
            sample /= channels;
            if (!Float.isFinite(sample)) sample = 0;
            if (frame == 0) { emit(sample); nextPosition = rate; }
            else while (nextPosition <= frame * WavFile.RATE) {
                float fraction = (nextPosition - (frame - 1) * WavFile.RATE) / (float) WavFile.RATE;
                emit(previous + (sample - previous) * fraction); nextPosition += rate;
            }
            previous = sample; frame++;
        }
    }
    private void emit(float value) throws IOException {
        output[used++] = (short) Math.max(-32768, Math.min(32767, Math.round(value * 32768)));
        if (used == output.length) flush();
    }
    void flush() throws IOException { if (used > 0) { sink.write(output, used); used = 0; } }
}
