package com.soundoff.android;
import org.junit.Test;
import static org.junit.Assert.*;
import java.io.*;

public class WaveformDataTest {
    @Test public void peaksPreserveNegativeFullScaleAndPartialBins() throws Exception {
        File file = File.createTempFile("soundoff-waveform", ".wav");
        try {
            short[] pcm = new short[3201]; pcm[5] = Short.MIN_VALUE; pcm[1600] = 16384; pcm[3200] = 8192;
            try (WavFile wav = new WavFile(file)) { wav.write(pcm, pcm.length); }
            WaveformData data = WaveformData.read(file);
            assertEquals(200, data.durationMs);
            assertArrayEquals(new float[]{1f, .5f, .25f}, data.peaks, .0001f);
        } finally { file.delete(); }
    }
    @Test public void incrementalReaderRetainsEarlierPeaks() throws Exception {
        File file = File.createTempFile("soundoff-growing", ".wav");
        try {
            WaveformData.Reader reader = new WaveformData.Reader();
            try (WavFile wav = new WavFile(file)) {
                short[] pcm = new short[1600]; pcm[0] = 16384; wav.write(pcm, pcm.length);
                assertArrayEquals(new float[]{.5f}, reader.update(file).peaks, .0001f);
                pcm[0] = Short.MIN_VALUE; wav.write(pcm, pcm.length);
                assertArrayEquals(new float[]{.5f, 1f}, reader.update(file).peaks, .0001f);
            }
        } finally { file.delete(); }
    }
    @Test public void emptyRecordingHasNoPeaks() throws Exception {
        File file = File.createTempFile("soundoff-waveform", ".wav");
        try {
            try (WavFile wav = new WavFile(file)) { }
            WaveformData data = WaveformData.read(file);
            assertEquals(0, data.peaks.length); assertEquals(0, data.durationMs);
        } finally { file.delete(); }
    }
}
