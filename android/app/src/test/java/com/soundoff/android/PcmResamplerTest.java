package com.soundoff.android;

import org.junit.Test;
import java.nio.*;
import java.util.*;
import static org.junit.Assert.*;

public class PcmResamplerTest {
    @Test public void stereoDownmixAndResamplePreserveDurationAcrossBuffers() throws Exception {
        List<Short> output = new ArrayList<>();
        PcmResampler converter = new PcmResampler(48000, 2, 2, (pcm, n) -> { for (int i = 0; i < n; i++) output.add(pcm[i]); });
        for (int i = 0; i < 480; i++) {
            ByteBuffer bytes = ByteBuffer.allocate(400).order(ByteOrder.LITTLE_ENDIAN);
            for (int j = 0; j < 100; j++) bytes.putShort((short) 2000).putShort((short) 4000);
            bytes.flip(); converter.accept(bytes);
        }
        converter.flush(); assertEquals(16000, output.size());
        for (short sample : output) assertEquals(3000, sample);
    }
    @Test public void floatAudioClipsAndSanitizesNonFiniteValues() throws Exception {
        List<Short> output = new ArrayList<>();
        PcmResampler converter = new PcmResampler(16000, 1, 4, (pcm, n) -> { for (int i = 0; i < n; i++) output.add(pcm[i]); });
        ByteBuffer bytes = ByteBuffer.allocate(20).order(ByteOrder.LITTLE_ENDIAN);
        bytes.putFloat(0).putFloat(2).putFloat(-2).putFloat(Float.NaN).putFloat(0.5f).flip();
        converter.accept(bytes); converter.flush();
        assertEquals(Arrays.asList((short) 0, (short) 32767, (short) -32768, (short) 0, (short) 16384), output);
    }
    @Test(expected = java.io.IOException.class) public void rejectsTruncatedStereoFrames() throws Exception {
        new PcmResampler(44100, 2, 2, (pcm, n) -> {}).accept(ByteBuffer.allocate(3));
    }
}
