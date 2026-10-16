package com.soundoff.android;



import android.content.Context;

import android.content.Intent;

import android.content.IntentFilter;

import android.content.ClipboardManager;

import android.app.Activity;

import android.app.Instrumentation;

import android.net.Uri;

import android.view.View;

import android.view.ViewGroup;

import android.widget.Button;

import androidx.test.ext.junit.runners.AndroidJUnit4;

import static androidx.test.platform.app.InstrumentationRegistry.getInstrumentation;

import static org.junit.Assert.*;

import org.junit.Test;

import org.junit.runner.RunWith;

import java.io.*;

import java.nio.charset.StandardCharsets;



/** Run using scripts/test_android.py, which provisions the pinned multilingual tiny model. */

@RunWith(AndroidJUnit4.class)

public class RecordingTest {

    @Test public void testNativeEnglishTranscription() throws Exception {

        Context context = getInstrumentation().getTargetContext();

        File fixture = new File(context.getFilesDir(), "test-jfk.wav");

        assertTrue("Provision test-jfk.wav with scripts/test_android.py", fixture.isFile());

        long engine = WhisperEngine.open(ModelStore.file(context, 0).getAbsolutePath());

        assertTrue("Multilingual tiny model must load", engine != 0);

        try {

            float[] pcm = WavFile.read(fixture, 0, (int) ((fixture.length() - 44) / 2));

            byte[] result = WhisperEngine.transcribe(engine, pcm, "en", "");

            assertNotNull(result);

            String text = new String(result, StandardCharsets.UTF_8).toLowerCase(java.util.Locale.ROOT);

            assertTrue("Expected recognizable English speech, got: " + text, text.contains("country"));

        } finally { WhisperEngine.close(engine); }

    }



    @Test public void testRecordingSurvivesActivityRecreationAndStops() throws Exception {

        Context context = getInstrumentation().getTargetContext();

        Intent open = new Intent(context, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK);

        MainActivity activity = (MainActivity) getInstrumentation().startActivitySync(open);

        try {

            RecordingService previous = RecordingService.current;

            getInstrumentation().runOnMainSync(() -> context.startForegroundService(new Intent(context, RecordingService.class).putExtra("language", "auto").putExtra("model", 0)));

            long deadline = System.currentTimeMillis() + 20000;

            while ((RecordingService.current == null || RecordingService.current == previous || RecordingService.current.samples < 16000) && System.currentTimeMillis() < deadline) Thread.sleep(100);

            RecordingService service = RecordingService.current;

            assertNotNull(service); assertTrue("Microphone must produce samples", service.samples >= 16000);

            getInstrumentation().runOnMainSync(activity::recreate);

            Thread.sleep(4000);

            assertFalse("Rotation must not stop capture", service.stopRequested);

            getInstrumentation().runOnMainSync(() -> context.startService(new Intent(context, RecordingService.class).setAction(RecordingService.STOP)));

            deadline = System.currentTimeMillis() + 60000;

            while (!service.finished && System.currentTimeMillis() < deadline) Thread.sleep(100);

            assertTrue("Recording should finish", service.finished);

            assertTrue(service.session.audio().length() > 44);

            assertEquals("Saved", service.session.status());

            assertEquals(service.samples, service.processed);

            WavFile.repair(service.session.audio());

            assertEquals(44 + service.samples * 2, service.session.audio().length());

        } finally {

            getInstrumentation().runOnMainSync(() -> context.startService(new Intent(context, RecordingService.class).setAction(RecordingService.FINISH)));

        }

    }



    @Test public void testSessionRetainsFilipinoAndMixedUnicode() throws Exception {

        Context context = getInstrumentation().getTargetContext();

        // Session names sort by wall-clock seconds; ensure this fixture is newest.

        Thread.sleep(1100);

        SessionStore.Session session = SessionStore.create(context);

        String words = "Kumusta! Let's meet bukas, alas-dos. Salamat, MaÃ±alac. ðŸ‡µðŸ‡­";

        try (WavFile wav = new WavFile(session.audio())) { wav.write(new short[]{1, -2, 3, -4}, 4); }

        session.append(words); session.save("auto", "tiny", "Saved");

        assertEquals(words + "\n\n", session.text());

        assertEquals("Saved", session.status());

        assertTrue(SessionStore.list(context).stream().anyMatch(s -> s.dir.equals(session.dir)));

        MainActivity activity = (MainActivity) getInstrumentation().startActivitySync(new Intent(context, MainActivity.class).putExtra("session", session.dir.getName()).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK));

        getInstrumentation().waitForIdleSync();

        Thread.sleep(600); // Allow the UI to enable actions for the restored recording.

        File exported = new File(context.getCacheDir(), "test-export.wav");

        exported.delete();

        IntentFilter filter = new IntentFilter(Intent.ACTION_CREATE_DOCUMENT);

        filter.addCategory(Intent.CATEGORY_OPENABLE);

        filter.addDataType("audio/wav");

        Instrumentation.ActivityMonitor picker = getInstrumentation().addMonitor(filter,

            new Instrumentation.ActivityResult(Activity.RESULT_OK, new Intent().setData(Uri.fromFile(exported))), true);

        try {

            getInstrumentation().runOnMainSync(() -> {

                activity.copyTranscript();

                ClipboardManager clipboard = context.getSystemService(ClipboardManager.class);

                assertNotNull(clipboard.getPrimaryClip());

                assertEquals(words + "\n\n", clipboard.getPrimaryClip().getItemAt(0).getText().toString());

                activity.showExport();

            });

            getInstrumentation().runOnMainSync(() -> {

                android.widget.ListView choices = activity.audioFormat.getListView();

                choices.performItemClick(choices.getChildAt(0), 0, choices.getAdapter().getItemId(0));

            });

            getInstrumentation().waitForIdleSync();

            assertEquals("File-picker request should be intercepted", 1, picker.getHits());

            long deadline = System.currentTimeMillis() + 10000;

            while (exported.length() != session.audio().length() && System.currentTimeMillis() < deadline) Thread.sleep(100);

            assertArrayEquals(java.nio.file.Files.readAllBytes(session.audio().toPath()), java.nio.file.Files.readAllBytes(exported.toPath()));

            assertEquals(1, picker.getHits());

        } finally {

            getInstrumentation().removeMonitor(picker);

            getInstrumentation().runOnMainSync(activity::finish);

        }

    }



    @Test public void testImportButtonTranscribesExistingWav() throws Exception {

        Context context = getInstrumentation().getTargetContext();

        File fixture = new File(context.getFilesDir(), "test-jfk.wav");

        MainActivity activity = (MainActivity) getInstrumentation().startActivitySync(new Intent(context, MainActivity.class)

            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK));

        IntentFilter filter = new IntentFilter(Intent.ACTION_OPEN_DOCUMENT);

        filter.addCategory(Intent.CATEGORY_OPENABLE); filter.addDataType("audio/*");

        Instrumentation.ActivityMonitor picker = getInstrumentation().addMonitor(filter,

            new Instrumentation.ActivityResult(Activity.RESULT_OK, new Intent().setData(Uri.fromFile(fixture))), true);

        RecordingService previous = RecordingService.current;

        try {

            Thread.sleep(700);

            getInstrumentation().runOnMainSync(() -> activity.importAudio());

            long deadline = System.currentTimeMillis() + 120000;

            while ((RecordingService.current == previous || RecordingService.current == null || !RecordingService.current.finished) && System.currentTimeMillis() < deadline) Thread.sleep(100);

            RecordingService service = RecordingService.current;

            assertNotSame(previous, service); assertNotNull(service); assertTrue(service.importing); assertTrue(service.finished);

            assertEquals(service.message, "Saved", service.session.status());

            assertTrue(service.transcript.toLowerCase(java.util.Locale.ROOT).contains("country"));

            assertEquals(1, picker.getHits());

            assertEquals(fixture.length(), service.session.audio().length());

        } finally { getInstrumentation().removeMonitor(picker); getInstrumentation().runOnMainSync(activity::finish); }

    }



    @Test public void testM4aDecodeAndMp3RoundTrip() throws Exception {

        Context context = getInstrumentation().getTargetContext();

        File decoded = new File(context.getCacheDir(), "decoded.wav");

        try (WavFile wav = new WavFile(decoded)) {

            AudioImporter.decode(context, Uri.fromFile(new File(context.getFilesDir(), "test-jfk.m4a")),

                new File(context.getCacheDir(), "m4a-source.tmp"), () -> false, wav::write, duration -> {});

        }

        assertTrue("44.1 kHz stereo AAC should decode to about 11 seconds of mono PCM", Math.abs((decoded.length() - 44) / 32000.0 - 11) < 0.3);

        File mp3 = new File(context.getCacheDir(), "test-export.mp3");

        try (OutputStream output = new FileOutputStream(mp3)) { Mp3Encoder.export(decoded, output); }

        assertTrue("MP3 should be smaller than PCM WAV", mp3.length() < decoded.length() / 2);

        File roundTrip = new File(context.getCacheDir(), "mp3-roundtrip.wav");

        try (WavFile wav = new WavFile(roundTrip)) {

            AudioImporter.decode(context, Uri.fromFile(mp3), new File(context.getCacheDir(), "mp3-source.tmp"),

                () -> false, wav::write, duration -> {});

        }

        long engine = WhisperEngine.open(ModelStore.file(context, 0).getAbsolutePath());

        assertTrue(engine != 0);

        try {

            byte[] result = WhisperEngine.transcribe(engine, WavFile.read(roundTrip, 0, (int) ((roundTrip.length() - 44) / 2)), "en", "");

            assertNotNull(result);

            assertTrue("Exported MP3 must retain intelligible speech", new String(result, StandardCharsets.UTF_8).toLowerCase(java.util.Locale.ROOT).contains("country"));

        } finally { WhisperEngine.close(engine); }

    }

    @Test public void testReviewPlaybackAndTranscriptEdits() throws Exception {

        Context context = getInstrumentation().getTargetContext();

        SessionStore.Session session = SessionStore.create(context);

        java.nio.file.Files.copy(new File(context.getFilesDir(), "test-jfk.wav").toPath(), session.audio().toPath());

        session.append("Original transcript"); session.save("en", "tiny", "Saved");

        byte[] original = java.nio.file.Files.readAllBytes(session.audio().toPath());

        Intent intent = new Intent(context, ReviewActivity.class).putExtra("session", session.dir.getName())

            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK);

        try (androidx.test.core.app.ActivityScenario<ReviewActivity> scenario = androidx.test.core.app.ActivityScenario.launch(intent)) {

            long deadline = System.currentTimeMillis() + 15000;

            java.util.concurrent.atomic.AtomicBoolean ready = new java.util.concurrent.atomic.AtomicBoolean();

            do {

                scenario.onActivity(a -> ready.set(button(a.getWindow().getDecorView(), "Play").isEnabled()));

                if (!ready.get()) Thread.sleep(100);

            } while (!ready.get() && System.currentTimeMillis() < deadline);

            assertTrue("Review audio must prepare", ready.get());

            scenario.onActivity(a -> {

                a.seekTo(4000); a.setSpeed(.75f);

                findEditor(a.getWindow().getDecorView()).setText("Kumusta! Corrected Taglish — salamat.");

                button(a.getWindow().getDecorView(), "Play").performClick();

            });

            Thread.sleep(1200);

            scenario.onActivity(a -> {

                try {

                    java.lang.reflect.Field f = ReviewActivity.class.getDeclaredField("player"); f.setAccessible(true);

                    android.media.MediaPlayer player = (android.media.MediaPlayer) f.get(a);

                    assertTrue(player.isPlaying()); assertTrue(player.getCurrentPosition() >= 4000);

                    assertEquals(.75f, player.getPlaybackParams().getSpeed(), .001f);

                    button(a.getWindow().getDecorView(), "Pause").performClick(); assertFalse(player.isPlaying());

                    a.setSpeed(1.37f);

                } catch (ReflectiveOperationException e) { throw new AssertionError(e); }

            });

            scenario.recreate();

            Thread.sleep(700);

            scenario.onActivity(a -> assertEquals("Kumusta! Corrected Taglish — salamat.", findEditor(a.getWindow().getDecorView()).getText().toString()));

        }

        SessionStore.IO.submit(() -> {}).get();

        assertEquals("Kumusta! Corrected Taglish — salamat.", session.text());

        assertArrayEquals(original, java.nio.file.Files.readAllBytes(session.audio().toPath()));

    }

    @Test public void testCatchUpPreservesConcurrentEdits() throws Exception {

        Context context = getInstrumentation().getTargetContext();

        RecordingService service = new RecordingService(); service.session = SessionStore.create(context);

        service.appendRecognized("Kumusta po"); int seen = service.transcript.length();

        service.appendRecognized("Let's meet bukas.");

        service.saveEdited("Kumusta, Mañalac!", seen);

        assertEquals("Kumusta, Mañalac!\n\nLet's meet bukas.", service.session.text());

        service.appendRecognized("Salamat.");

        assertEquals("Kumusta, Mañalac!\n\nLet's meet bukas.\n\nSalamat.", service.session.text());

    }

    @Test public void testResumeAppendsSameInterviewDuringAndAfterCatchUp() throws Exception {
        Context context = getInstrumentation().getTargetContext();
        SessionStore.Session interview = SessionStore.create(context);
        java.nio.file.Files.copy(new File(context.getFilesDir(), "test-jfk.wav").toPath(), interview.audio().toPath());
        interview.saveText("Corrected interview introduction.");
        interview.saveProgress("en", "tiny", "Partial transcript", 0);
        byte[] original = java.nio.file.Files.readAllBytes(interview.audio().toPath());
        long initialSamples = (original.length - 44) / 2;
        Intent open = new Intent(context, MainActivity.class).putExtra("session", interview.dir.getName()).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK);
        MainActivity activity = (MainActivity) getInstrumentation().startActivitySync(open);
        MainActivity[] screen = {activity};
        try {
            Thread.sleep(700);
            getInstrumentation().runOnMainSync(() -> button(activity.getWindow().getDecorView(), "Resume").performClick());
            long deadline = System.currentTimeMillis() + 20000;
            while ((RecordingService.current == null || !interview.dir.equals(RecordingService.current.session == null ? null : RecordingService.current.session.dir)
                    || RecordingService.current.samples < initialSamples + 16000) && System.currentTimeMillis() < deadline) Thread.sleep(50);
            RecordingService service = RecordingService.current;
            assertNotNull(service); assertEquals(interview.dir, service.session.dir);
            assertTrue(service.samples >= initialSamples + 16000);
            getInstrumentation().runOnMainSync(() -> context.startService(new Intent(context, RecordingService.class).setAction(RecordingService.STOP)));
            deadline = System.currentTimeMillis() + 10000;
            while (service.capturing && System.currentTimeMillis() < deadline) Thread.sleep(20);
            assertFalse(service.capturing);
            long firstStop = service.samples;
            assertFalse("Fixture backlog should still be transcribing", service.finished);
            getInstrumentation().runOnMainSync(() -> context.startForegroundService(new Intent(context, RecordingService.class).setAction(RecordingService.RESUME).putExtra("session", interview.dir.getName())));
            deadline = System.currentTimeMillis() + 15000;
            while (service.samples < firstStop + 16000 && System.currentTimeMillis() < deadline) Thread.sleep(50);
            assertSame("Resume during catch-up retains the service", service, RecordingService.current);
            assertTrue(service.samples >= firstStop + 16000);
            getInstrumentation().runOnMainSync(() -> context.startService(new Intent(context, RecordingService.class).setAction(RecordingService.STOP)));
            deadline = System.currentTimeMillis() + 120000;
            while (!service.finished && System.currentTimeMillis() < deadline) Thread.sleep(100);
            assertTrue(service.finished); assertEquals(service.samples, service.processed);
            long drainedSamples = service.samples;
            assertEquals(drainedSamples, interview.processed());
            getInstrumentation().runOnMainSync(activity::finish);
            screen[0] = (MainActivity) getInstrumentation().startActivitySync(open);
            Thread.sleep(700);
            getInstrumentation().runOnMainSync(() -> button(screen[0].getWindow().getDecorView(), "Resume").performClick());
            deadline = System.currentTimeMillis() + 20000;
            while ((RecordingService.current == service || RecordingService.current.samples < drainedSamples + 16000) && System.currentTimeMillis() < deadline) Thread.sleep(50);
            RecordingService resumed = RecordingService.current;
            assertNotSame("Resume after completion restarts the worker", service, resumed);
            assertEquals(interview.dir, resumed.session.dir);
            assertTrue(resumed.processed >= drainedSamples);
            assertTrue(resumed.samples >= drainedSamples + 16000);
            getInstrumentation().runOnMainSync(() -> context.startService(new Intent(context, RecordingService.class).setAction(RecordingService.STOP)));
            deadline = System.currentTimeMillis() + 60000;
            while (!resumed.finished && System.currentTimeMillis() < deadline) Thread.sleep(100);
            assertTrue(resumed.finished); assertEquals(resumed.samples, resumed.processed);
            byte[] combined = java.nio.file.Files.readAllBytes(interview.audio().toPath());
            assertArrayEquals(java.util.Arrays.copyOfRange(original, 44, original.length), java.util.Arrays.copyOfRange(combined, 44, original.length));
            assertEquals(44 + resumed.samples * 2, combined.length);
            assertTrue("Paused wall time must not become audio", resumed.samples - initialSamples < 8L * WavFile.RATE);
            assertTrue(interview.text().startsWith("Corrected interview introduction."));
            assertEquals("Saved", interview.status());
        } finally {
            getInstrumentation().runOnMainSync(() -> { context.startService(new Intent(context, RecordingService.class).setAction(RecordingService.STOP)); screen[0].finish(); });
        }
    }
    private static android.widget.EditText findEditor(View v) {

        if (v instanceof android.widget.EditText) return (android.widget.EditText) v;

        if (v instanceof ViewGroup) for (int i = 0; i < ((ViewGroup) v).getChildCount(); i++) {

            android.widget.EditText found = findEditor(((ViewGroup) v).getChildAt(i)); if (found != null) return found;

        }

        return null;

    }

    private static Button button(View view, String label) {

        if (view instanceof Button && label.contentEquals(((Button) view).getText())) return (Button) view;

        if (view instanceof ViewGroup) for (int i = 0; i < ((ViewGroup) view).getChildCount(); i++) {

            Button found = button(((ViewGroup) view).getChildAt(i), label); if (found != null) return found;

        }

        return null;

    }

}
