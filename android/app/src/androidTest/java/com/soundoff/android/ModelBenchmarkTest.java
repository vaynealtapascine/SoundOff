package com.soundoff.android;

import android.content.Context;
import android.os.SystemClock;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import static androidx.test.platform.app.InstrumentationRegistry.getInstrumentation;
import static org.junit.Assert.*;
import org.junit.Test;
import org.junit.runner.RunWith;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.Locale;

/** Explicit opt-in: compares Tiny and Small Q5 using the same public English clip. */
@RunWith(AndroidJUnit4.class)
public class ModelBenchmarkTest {
    @Test public void compareTinyAndSmall() throws Exception {
        Context context = getInstrumentation().getTargetContext();
        File fixture = new File(context.getFilesDir(), "test-jfk.wav");
        float[] pcm = WavFile.read(fixture, 0, (int) ((fixture.length() - 44) / 2));
        StringBuilder report = new StringBuilder("Public English JFK fixture; this does not measure Filipino/Taglish accuracy.\n");
        for (int model : new int[]{0, 2}) {
            assertTrue("Provision Small with --benchmark-small", ModelStore.file(context, model).isFile());
            long start = SystemClock.elapsedRealtime();
            long engine = WhisperEngine.open(ModelStore.file(context, model).getAbsolutePath());
            long load = SystemClock.elapsedRealtime() - start;
            assertTrue("Model should load", engine != 0);
            try {
                start = SystemClock.elapsedRealtime();
                byte[] result = WhisperEngine.transcribe(engine, pcm, "en", "");
                long inference = SystemClock.elapsedRealtime() - start;
                assertNotNull(result);
                String text = new String(result, StandardCharsets.UTF_8);
                assertTrue(text.toLowerCase(Locale.ROOT).contains("country"));
                report.append(String.format(Locale.ROOT, "%s: load=%d ms, inference=%d ms, audio=%.3f s, realtime_factor=%.3f\n%s\n", ModelStore.NAMES[model], load, inference, pcm.length / 16000.0, inference * 16.0 / pcm.length, text));
            } finally { WhisperEngine.close(engine); }
        }
        try (FileOutputStream out = new FileOutputStream(new File(context.getCacheDir(), "model-benchmark.txt"))) { out.write(report.toString().getBytes(StandardCharsets.UTF_8)); }
    }
}
