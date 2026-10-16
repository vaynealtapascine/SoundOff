package com.soundoff.android;

import android.content.Context;
import android.util.AtomicFile;
import org.json.JSONObject;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.*;

final class SessionStore {
    static final java.util.concurrent.ExecutorService IO = java.util.concurrent.Executors.newSingleThreadExecutor();
    static final class Session {
        final File dir;
        final String title;
        Session(File dir) { this.dir = dir; title = dir.getName().replace('_', ' '); }
        File audio() { return new File(dir, "audio.wav"); }
        File transcript() { return new File(dir, "transcript.txt"); }
        String text() throws IOException { return transcript().exists() ? new String(new AtomicFile(transcript()).readFully(), StandardCharsets.UTF_8) : ""; }
        void saveText(String text) throws IOException {
            AtomicFile file = new AtomicFile(transcript());
            FileOutputStream out = null;
            try { out = file.startWrite(); out.write(text.getBytes(StandardCharsets.UTF_8)); file.finishWrite(out); }
            catch (IOException e) { if (out != null) file.failWrite(out); throw e; }
        }
        synchronized JSONObject metadata() {
            try { return new JSONObject(new String(new AtomicFile(new File(dir, "session.json")).readFully(), StandardCharsets.UTF_8)); }
            catch (Exception e) { return new JSONObject(); }
        }
        String status() { return metadata().optString("status", "Interrupted"); }
        long processed() { return metadata().optLong("processed", "Saved".equals(status()) ? Math.max(0, audio().length() - 44) / 2 : 0); }
        synchronized void save(String language, String model, String status) throws IOException {
            saveProgress(language, model, status, processed());
        }
        synchronized void saveProgress(String language, String model, String status, long processed) throws IOException {
            AtomicFile atomic = new AtomicFile(new File(dir, "session.json"));
            FileOutputStream out = null;
            try {
                JSONObject json = new JSONObject().put("language", language).put("model", model).put("status", status).put("processed", processed);
                out = atomic.startWrite(); out.write(json.toString().getBytes(StandardCharsets.UTF_8)); atomic.finishWrite(out);
            } catch (Exception e) { if (out != null) atomic.failWrite(out); throw new IOException("Could not save session", e); }
        }
        void append(String text) throws IOException {
            appendRaw(text + "\n\n");
        }
        void appendRaw(String text) throws IOException {
            try (FileOutputStream out = new FileOutputStream(transcript(), true)) {
                out.write(text.getBytes(StandardCharsets.UTF_8)); out.getFD().sync();
            }
        }
    }
    private static File root(Context c) { File root = new File(c.getFilesDir(), "recordings"); root.mkdirs(); return root; }
    static Session create(Context c) throws IOException {
        String id = new SimpleDateFormat("yyyy-MM-dd_HH-mm-ss", Locale.ROOT).format(new Date()) + "_" + UUID.randomUUID().toString().substring(0, 4);
        File dir = new File(root(c), id);
        if (!dir.mkdirs()) throw new IOException("Could not create recording");
        return new Session(dir);
    }
    static List<Session> list(Context c) {
        File[] dirs = root(c).listFiles(File::isDirectory);
        List<Session> result = new ArrayList<>();
        if (dirs != null) { Arrays.sort(dirs, Comparator.comparing(File::getName).reversed()); for (File dir : dirs) result.add(new Session(dir)); }
        return result;
    }
}
