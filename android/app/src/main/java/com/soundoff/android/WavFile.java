package com.soundoff.android;

import java.io.*;

/** Single writer; readers only consume the published sample count after a complete write. */
final class WavFile implements Closeable {
    static final int RATE = 16000;
    private final RandomAccessFile file;
    private long samples;
    WavFile(File path) throws IOException { this(path, false); }
    WavFile(File path, boolean append) throws IOException {
        if (append) {
            if (!path.isFile() || path.length() < 44) throw new IOException("Recording audio is missing");
            repair(path);
        }
        file = new RandomAccessFile(path, "rw");
        if (append) samples = (file.length() - 44) / 2;
        else { file.setLength(0); file.write(new byte[44]); }
        checkpoint();
    }
    long samples() { return samples; }
    void write(short[] pcm, int count) throws IOException {
        if (samples + count > (0xffffffffL - 36) / 2) throw new IOException("WAV size limit reached");
        byte[] bytes = new byte[count * 2];
        for (int i = 0; i < count; i++) { bytes[i * 2] = (byte) pcm[i]; bytes[i * 2 + 1] = (byte) (pcm[i] >> 8); }
        file.seek(44 + samples * 2);
        file.write(bytes);
        samples += count;
    }
    private static void le(RandomAccessFile f, long value, int bytes) throws IOException {
        for (int i = 0; i < bytes; i++) f.write((int) (value >> (8 * i)) & 255);
    }
    private static void header(RandomAccessFile f, long count) throws IOException {
        f.seek(0); f.writeBytes("RIFF"); le(f, 36 + count * 2, 4); f.writeBytes("WAVEfmt ");
        le(f, 16, 4); le(f, 1, 2); le(f, 1, 2); le(f, RATE, 4); le(f, RATE * 2, 4);
        le(f, 2, 2); le(f, 16, 2); f.writeBytes("data"); le(f, count * 2, 4);
    }
    void checkpoint() throws IOException { header(file, samples); file.getFD().sync(); }
    @Override public void close() throws IOException { try { checkpoint(); } finally { file.close(); } }
    static void repair(File path) throws IOException {
        try (RandomAccessFile f = new RandomAccessFile(path, "rw")) {
            if (f.length() < 44) throw new IOException("Incomplete WAV header");
            long count = (f.length() - 44) / 2;
            f.setLength(44 + count * 2); header(f, count);
        }
    }
    static float[] read(File path, long start, int count) throws IOException {
        byte[] bytes = new byte[count * 2];
        try (RandomAccessFile f = new RandomAccessFile(path, "r")) { f.seek(44 + start * 2); f.readFully(bytes); }
        float[] pcm = new float[count];
        for (int i = 0; i < count; i++) pcm[i] = (short) ((bytes[i * 2] & 255) | (bytes[i * 2 + 1] << 8)) / 32768f;
        return pcm;
    }
}
