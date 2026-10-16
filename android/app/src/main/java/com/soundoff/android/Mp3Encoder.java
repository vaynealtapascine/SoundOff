package com.soundoff.android;

import java.io.*;

final class Mp3Encoder {
    static { System.loadLibrary("soundoff_mp3"); }
    private static native long open();
    private static native byte[] encode(long encoder, short[] pcm, int count) throws IOException;
    private static native byte[] flush(long encoder) throws IOException;
    private static native void close(long encoder);
    static void export(File wav, OutputStream destination) throws IOException {
        long encoder = open();
        if (encoder == 0) throw new IOException("Could not start MP3 encoder");
        try (RandomAccessFile input = new RandomAccessFile(wav, "r")) {
            if (input.length() < 44) throw new IOException("Recording is incomplete");
            input.seek(44);
            short[] pcm = new short[8192]; byte[] buffer = new byte[pcm.length * 2];
            long remaining = input.length() - 44;
            while (remaining > 0) {
                int n = (int) Math.min(buffer.length, remaining);
                input.readFully(buffer, 0, n); remaining -= n;
                if (n % 2 != 0) throw new IOException("Incomplete audio sample");
                for (int i = 0; i < n / 2; i++) pcm[i] = (short) ((buffer[i * 2] & 255) | (buffer[i * 2 + 1] << 8));
                destination.write(encode(encoder, pcm, n / 2));
            }
            destination.write(flush(encoder));
        } finally { close(encoder); }
    }
}
