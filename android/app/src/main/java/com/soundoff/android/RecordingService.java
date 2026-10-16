package com.soundoff.android;

import android.app.*;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.media.*;
import android.net.Uri;
import android.os.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;

public final class RecordingService extends Service {
    static final String STOP = "com.soundoff.android.STOP";
    static final String FINISH = "com.soundoff.android.FINISH";
    static final String RESUME = "com.soundoff.android.RESUME";
    static final String IMPORT = "com.soundoff.android.IMPORT";
    static volatile RecordingService current;
    volatile boolean stopRequested, skipRemaining, capturing, finished;
    volatile long samples, processed;
    volatile boolean importing;
    volatile long expectedSamples;
    volatile String message = "Starting…", transcript = "";
    private volatile String captureFailure;
    volatile SessionStore.Session session;
    private String language, model;
    private PowerManager.WakeLock wakeLock;
    private final LinkedBlockingQueue<Long> boundaries = new LinkedBlockingQueue<>();
    private boolean started;

    @Override public IBinder onBind(Intent intent) { return null; }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) { stopSelf(); return START_NOT_STICKY; }
        if (STOP.equals(intent.getAction()) || FINISH.equals(intent.getAction())) {
            if (!started) { stopSelf(); return START_NOT_STICKY; }
            stopRequested = true;
            if (importing || FINISH.equals(intent.getAction())) skipRemaining = true;
            message = "Finishing transcript…";
            updateNotification();
            return START_NOT_STICKY;
        }
        if (started && !finished) {
            if (RESUME.equals(intent.getAction()) && !importing && !capturing && session != null
                    && session.dir.getName().equals(intent.getStringExtra("session"))) {
                beginCapture();
            }
            return START_NOT_STICKY;
        }
        finished = false; stopRequested = false; skipRemaining = false; captureFailure = null;
        boundaries.clear(); samples = 0; processed = 0; expectedSamples = 0;
        boolean resuming = RESUME.equals(intent.getAction());
        SessionStore.Session target = null;
        if (resuming) for (SessionStore.Session candidate : SessionStore.list(this))
            if (candidate.dir.getName().equals(intent.getStringExtra("session"))) target = candidate;
        if (resuming && target == null) { message = "Recording not found"; finished = true; stopSelf(); return START_NOT_STICKY; }
        if (session == null || target == null || !session.dir.equals(target.dir)) transcript = "";
        started = true; current = this;
        importing = IMPORT.equals(intent.getAction());
        language = target == null ? intent.getStringExtra("language") : target.metadata().optString("language", "auto");
        if (!"en".equals(language) && !"tl".equals(language)) language = "auto";
        int selectedModel = Math.max(0, Math.min(ModelStore.NAMES.length - 1, intent.getIntExtra("model", 0)));
        if (target != null) {
            String previousModel = target.metadata().optString("model", ModelStore.NAMES[selectedModel]);
            for (int i = 0; i < ModelStore.NAMES.length; i++) if (previousModel.equals(ModelStore.NAMES[i])) selectedModel = i;
        }
        model = ModelStore.NAMES[selectedModel];
        final int modelIndex = selectedModel;
        try {
            NotificationManager manager = getSystemService(NotificationManager.class);
            manager.createNotificationChannel(new NotificationChannel("recording", "Recording", NotificationManager.IMPORTANCE_LOW));
            PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class), PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
            PendingIntent stop = PendingIntent.getService(this, 1, new Intent(this, RecordingService.class).setAction(STOP), PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
            Notification notification = new Notification.Builder(this, "recording").setSmallIcon(com.soundoff.android.R.drawable.ic_soundoff)
                .setContentTitle(importing ? "SoundOff is transcribing audio" : "SoundOff is recording").setContentText("Audio stays on this device").setContentIntent(open)
                .setOngoing(true).addAction(new Notification.Action.Builder(null, "Stop", stop).build()).build();
            if (importing && Build.VERSION.SDK_INT >= 35) startForeground(1, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROCESSING);
            else if (importing && Build.VERSION.SDK_INT >= 29) startForeground(1, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
            else if (!importing && Build.VERSION.SDK_INT >= 30) startForeground(1, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE);
            else startForeground(1, notification);
            wakeLock = getSystemService(PowerManager.class).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "SoundOff:recording");
            wakeLock.acquire(6 * 60 * 60 * 1000L);
            session = target == null ? SessionStore.create(this) : target;
            if (target != null) {
                WavFile.repair(session.audio());
                samples = (session.audio().length() - 44) / 2;
                processed = Math.min(samples, Math.max(0, session.processed()));
                for (long end = Math.min(samples, processed + 8L * WavFile.RATE); end > processed;) {
                    boundaries.add(end); if (end == samples) break; end = Math.min(samples, end + 8L * WavFile.RATE);
                }
            }
            session.saveProgress(language, model, importing ? "Importing" : "Recording", processed);
            capturing = true;
            if (importing) {
                Uri source = intent.getData();
                new Thread(() -> importAudio(source), "SoundOff-import").start();
            } else new Thread(this::capture, "SoundOff-microphone").start();
            new Thread(() -> recognize(ModelStore.file(this, modelIndex)), "SoundOff-transcription").start();
        } catch (Exception e) { message = "Could not start: " + e.getMessage(); finished = true; stopRequested = true; cleanup(); }
        return START_NOT_STICKY;
    }

    private void importAudio(Uri source) {
        final long[] lastBoundary = {0};
        try (WavFile wav = new WavFile(session.audio())) {
            if (source == null) throw new IOException("No audio file selected");
            AudioImporter.decode(this, source, new File(session.dir, "source.tmp"), () -> stopRequested,
                (pcm, count) -> {
                    if (wav.samples() + count > 4L * 60 * 60 * WavFile.RATE) throw new IOException("Four-hour audio limit reached");
                    wav.write(pcm, count); samples = wav.samples();
                    if (samples - lastBoundary[0] >= 20L * WavFile.RATE) {
                        wav.checkpoint(); boundaries.add(samples); lastBoundary[0] = samples;
                    }
                }, duration -> expectedSamples = duration * WavFile.RATE / 1_000_000);
            if (samples == 0) throw new IOException("The selected file contains no audio samples");
        } catch (Exception e) { captureFailure = e.getMessage(); message = "Import stopped: " + captureFailure; }
        finally {
            if (samples > lastBoundary[0]) boundaries.add(samples);
            capturing = false;
        }
    }

    private void capture() {
        AudioRecord recorder = null;
        long lastBoundary = samples, silence = 0;
        try (WavFile wav = new WavFile(session.audio(), session.audio().isFile())) {
            if (checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) != android.content.pm.PackageManager.PERMISSION_GRANTED)
                throw new SecurityException("Microphone permission is required");
            int min = AudioRecord.getMinBufferSize(WavFile.RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
            if (min <= 0) throw new IOException("16 kHz microphone capture unavailable");
            recorder = new AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION, WavFile.RATE,
                AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, Math.max(min * 2, 6400));
            if (recorder.getState() != AudioRecord.STATE_INITIALIZED) throw new IOException("Microphone unavailable");
            recorder.startRecording();
            if (recorder.getRecordingState() != AudioRecord.RECORDSTATE_RECORDING) throw new IOException("Microphone did not start");
            short[] buffer = new short[1600];
            while (!stopRequested && samples < 4L * 60 * 60 * WavFile.RATE) {
                int n = recorder.read(buffer, 0, buffer.length, AudioRecord.READ_BLOCKING);
                if (n < 0) throw new IOException("Microphone read failed (" + n + ")");
                if (n == 0) continue;
                wav.write(buffer, n); samples = wav.samples();
                double energy = 0;
                for (int i = 0; i < n; i++) energy += (double) buffer[i] * buffer[i];
                silence = Math.sqrt(energy / n) < 220 ? silence + n : 0;
                long span = samples - lastBoundary;
                // Prefer a pause after 3 seconds; cap latency at 8 seconds of captured audio.
                if ((span >= 3 * WavFile.RATE && silence >= 6400) || span >= 8 * WavFile.RATE) {
                    wav.checkpoint(); boundaries.add(samples); lastBoundary = samples;
                }
                if (samples >= 4L * 60 * 60 * WavFile.RATE) { stopRequested = true; message = "Four-hour recording limit reached. Finishing…"; }
            }
        } catch (Exception e) { captureFailure = e.getMessage(); message = "Recording stopped: " + captureFailure; stopRequested = true; }
        finally {
            if (recorder != null) { try { recorder.stop(); } catch (Exception ignored) {} recorder.release(); }
            if (samples > lastBoundary) boundaries.add(samples);
            capturing = false;
            new Handler(getMainLooper()).post(this::updateNotification);
        }
    }

    private void recognize(File modelFile) {
        long engine = 0;
        boolean failed = false;
        try {
            message = "Loading speech model…";
            engine = WhisperEngine.open(modelFile.getAbsolutePath());
            if (engine == 0) throw new IOException("Speech model could not load");
            message = importing ? "Importing and transcribing on this device" : "Listening · transcribing on this device";
            while (capturing || !boundaries.isEmpty()) {
                Long end = boundaries.poll(250, TimeUnit.MILLISECONDS);
                if (end == null) continue;
                if (skipRemaining) break;
                float[] audio = WavFile.read(session.audio(), processed, (int) (end - processed));
                double energy = 0; for (float value : audio) energy += value * value;
                // Avoid hallucinated text on near-silence. Quiet speech still needs device testing.
                if (Math.sqrt(energy / audio.length) > 0.003) {
                    String hint = "auto".equals(language) ? "English, Filipino, Tagalog. Kumusta, pag-usapan natin ang meeting ngayon." : "";
                    byte[] result = WhisperEngine.transcribe(engine, audio, language, hint);
                    if (result == null) throw new IOException("Speech engine failed");
                    String text = new String(result, StandardCharsets.UTF_8).trim();
                    if (!text.isEmpty()) appendRecognized(text);
                }
                processed = end;
                session.saveProgress(language, model, capturing ? "Recording" : "Transcribing", processed);
            }
        } catch (Throwable e) {
            failed = true;
            message = "Transcription unavailable: " + e.getMessage() + ". Audio is still saved. Stop when ready.";
            // Keep the independent audio writer alive even if inference fails.
        } finally {
            if (engine != 0) WhisperEngine.close(engine);
            // Completion is decided on the same thread as Resume. If capture was
            // resumed just as this worker drained, start a fresh worker instead of
            // shutting down the new recording.
            final boolean error = failed;
            new Handler(getMainLooper()).post(() -> finishRecognition(modelFile, error));
        }
    }
    private void beginCapture() {
        try {
            if (samples >= 4L * 3600 * WavFile.RATE) { message = "Four-hour recording limit reached"; return; }
            stopRequested = false; captureFailure = null; capturing = true;
            session.saveProgress(language, model, "Recording", processed);
            new Thread(this::capture, "SoundOff-microphone").start();
            updateNotification();
        } catch (IOException e) { capturing = false; message = "Could not resume: " + e.getMessage(); }
    }
    private void finishRecognition(File modelFile, boolean failed) {
        if (capturing || !boundaries.isEmpty() && !skipRemaining && !failed) {
            if (failed && capturing) {
                new Handler(getMainLooper()).postDelayed(() -> finishRecognition(modelFile, true), 250);
            } else new Thread(() -> recognize(modelFile), "SoundOff-transcription").start();
            return;
        }
        try {
            session.saveProgress(language, model, captureFailure != null ? "Interrupted" : failed || processed < samples ? "Partial transcript" : "Saved", processed);
            if (captureFailure != null) message = "Recording interrupted: " + captureFailure + ". Audio was kept.";
            else if (failed) message = "Audio saved; transcription unavailable";
            else message = processed < samples ? "Audio saved; transcript is partial" : "Recording and transcript saved";
        } catch (IOException e) { message = "Audio saved; session details could not save: " + e.getMessage(); }
        finished = true;
        cleanup();
    }

    // The editor tracks how much raw recognition it has seen. A queued edit must
    // retain passages recognized after that snapshot, even if inference wins the race.
    synchronized void appendRecognized(String text) throws IOException {
        String addition = (transcript.isEmpty() && session.transcript().length() == 0 ? "" : "\n\n") + text;
        session.appendRaw(addition);
        transcript += addition;
    }
    synchronized void saveEdited(String text, int seenCharacters) throws IOException {
        session.saveText(TranscriptMerge.preserveUnseen(text, transcript, seenCharacters));
    }

    private void updateNotification() {
        if (finished || session == null) return;
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class).putExtra("session", session.dir.getName()), PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        Notification.Builder builder = new Notification.Builder(this, "recording")
            .setSmallIcon(com.soundoff.android.R.drawable.ic_soundoff).setContentIntent(open).setOngoing(true)
            .setContentTitle(capturing && !stopRequested && !importing ? "SoundOff is recording" : "SoundOff is transcribing")
            .setContentText(capturing && !stopRequested ? "Audio stays on this device" : "Recording stopped. Open SoundOff to resume this interview.");
        if (capturing && !stopRequested) {
            PendingIntent stop = PendingIntent.getService(this, 1, new Intent(this, RecordingService.class).setAction(STOP), PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
            builder.addAction(new Notification.Action.Builder(null, "Stop", stop).build());
        }
        getSystemService(NotificationManager.class).notify(1, builder.build());
    }

    private void cleanup() {
        if (wakeLock != null && wakeLock.isHeld()) wakeLock.release();
        stopForeground(STOP_FOREGROUND_REMOVE); stopSelf();
    }
    @Override public void onTimeout(int startId, int fgsType) {
        stopRequested = true; skipRemaining = true;
        captureFailure = "Android's background processing time limit was reached";
        cleanup();
    }
    @Override public void onDestroy() { stopRequested = true; super.onDestroy(); }
}
