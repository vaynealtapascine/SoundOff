package com.soundoff.android;

import java.io.*;

/** One peak per 100 ms, bounded even for multi-hour recordings. */
final class WaveformData {
    final float[] peaks;
    final int durationMs;
    WaveformData(float[] peaks, int durationMs) { this.peaks = peaks; this.durationMs = durationMs; }
    static WaveformData read(File file) throws IOException { return new Reader().update(file); }
    static final class Reader {
        private long consumed;
        private float[] peaks = new float[600];
        WaveformData update(File file) throws IOException {
            long samples = Math.max(0, file.length() - 44) / 2;
            if (samples > 4L * 3600 * WavFile.RATE) throw new IOException("Recording exceeds four hours");
            int bins = (int) ((samples + 1599) / 1600);
            if (bins > peaks.length) peaks = java.util.Arrays.copyOf(peaks, Math.max(bins, peaks.length * 2));
            try (InputStream source = new FileInputStream(file)) {
                source.skip(44 + consumed * 2);
                DataInputStream in = new DataInputStream(new BufferedInputStream(source));
                for (; consumed < samples; consumed++) {
                    short value = (short) (in.readUnsignedByte() | in.readUnsignedByte() << 8);
                    int bin = (int) (consumed / 1600);
                    peaks[bin] = Math.max(peaks[bin], Math.abs((float) value) / 32768f);
                }
            }
            return new WaveformData(java.util.Arrays.copyOf(peaks, bins), (int) (samples * 1000 / WavFile.RATE));
        }
    }
}
