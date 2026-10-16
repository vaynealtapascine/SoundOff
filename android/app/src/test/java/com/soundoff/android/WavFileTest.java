package com.soundoff.android;

import org.junit.Test;
import java.io.*;
import java.nio.*;
import static org.junit.Assert.*;

public class WavFileTest {
    @Test public void preservesPcmAndWritesStandardHeader() throws Exception {
        File file = File.createTempFile("soundoff", ".wav");
        try {
            try (WavFile wav = new WavFile(file)) { wav.write(new short[]{0, 32767, -32768, 1234}, 4); }
            byte[] bytes = java.nio.file.Files.readAllBytes(file.toPath());
            ByteBuffer header = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN);
            assertEquals(52, bytes.length); assertEquals(44, header.getInt(4));
            assertEquals(16000, header.getInt(24)); assertEquals(8, header.getInt(40));
            assertArrayEquals(new float[]{0, 32767 / 32768f, -1, 1234 / 32768f}, WavFile.read(file, 0, 4), 0);
            assertArrayEquals(new float[]{-1, 1234 / 32768f}, WavFile.read(file, 2, 2), 0);
        } finally { file.delete(); }
    }
    @Test public void repairsInterruptedHeaderAndDropsIncompleteSample() throws Exception {
        File file = File.createTempFile("soundoff", ".wav");
        try {
            try (WavFile wav = new WavFile(file)) { wav.write(new short[]{7, 8}, 2); }
            try (RandomAccessFile f = new RandomAccessFile(file, "rw")) { f.seek(40); f.writeInt(0); f.seek(f.length()); f.write(12); }
            WavFile.repair(file);
            assertEquals(48, file.length());
            byte[] bytes = java.nio.file.Files.readAllBytes(file.toPath());
            assertEquals(4, ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).getInt(40));
            assertArrayEquals(new float[]{7 / 32768f, 8 / 32768f}, WavFile.read(file, 0, 2), 0);
        } finally { file.delete(); }
    }
    @Test public void resumesWithoutReplacingAudioOrAddingGap() throws Exception {
        File file = File.createTempFile("soundoff-resume", ".wav");
        try {
            try (WavFile wav = new WavFile(file)) { wav.write(new short[]{7, 8}, 2); }
            try (WavFile wav = new WavFile(file, true)) { assertEquals(2, wav.samples()); wav.write(new short[]{9, 10}, 2); }
            assertEquals(52, file.length());
            assertArrayEquals(new float[]{7 / 32768f, 8 / 32768f, 9 / 32768f, 10 / 32768f}, WavFile.read(file, 0, 4), 0);
            assertEquals(8, ByteBuffer.wrap(java.nio.file.Files.readAllBytes(file.toPath())).order(ByteOrder.LITTLE_ENDIAN).getInt(40));
        } finally { file.delete(); }
    }
    @Test(expected = EOFException.class) public void neverReadsUnpublishedSamples() throws Exception {
        File file = File.createTempFile("soundoff", ".wav");
        try { try (WavFile wav = new WavFile(file)) { wav.write(new short[]{1}, 1); } WavFile.read(file, 0, 2); }
        finally { file.delete(); }
    }
}
