package com.soundoff.android;

import android.content.Context;
import java.io.*;
import java.net.HttpURLConnection;
import java.net.URL;
import java.security.MessageDigest;
import java.util.concurrent.Executors;

final class ModelStore {
    private static final java.util.concurrent.ExecutorService DOWNLOADS = Executors.newSingleThreadExecutor();
    static final String[] NAMES = {"tiny", "base", "small-q5_1"};
    private static final String[] HASHES = {
        "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21",
        "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
        "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb"
    };
    static volatile boolean busy;
    static volatile String status = "";
    static File file(Context context, int model) {
        File dir = new File(context.getFilesDir(), "models"); dir.mkdirs();
        return new File(dir, "ggml-" + NAMES[model] + ".bin");
    }
    static synchronized void download(Context context, int model) {
        if (busy) return;
        busy = true;
        status = "Preparing model download…";
        File target = file(context, model);
        DOWNLOADS.execute(() -> {
            File temp = new File(target.getPath() + ".part");
            HttpURLConnection connection = null;
            try {
                connection = (HttpURLConnection) new URL("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-" + NAMES[model] + ".bin").openConnection();
                connection.setConnectTimeout(30000); connection.setReadTimeout(30000);
                if (connection.getResponseCode() != 200) throw new IOException("Download server returned " + connection.getResponseCode());
                MessageDigest digest = MessageDigest.getInstance("SHA-256");
                long total = 0;
                try (InputStream in = connection.getInputStream(); FileOutputStream out = new FileOutputStream(temp)) {
                    byte[] buffer = new byte[65536]; int n;
                    while ((n = in.read(buffer)) != -1) {
                        out.write(buffer, 0, n); digest.update(buffer, 0, n); total += n;
                        if (total > 200_000_000) throw new IOException("Unexpected model size");
                        status = "Downloading " + NAMES[model] + " · " + total / 1_000_000 + " MB";
                    }
                    out.getFD().sync();
                }
                StringBuilder hash = new StringBuilder();
                for (byte b : digest.digest()) hash.append(String.format(java.util.Locale.ROOT, "%02x", b & 255));
                if (!HASHES[model].equals(hash.toString())) throw new IOException("Model checksum mismatch. Please retry.");
                if (!temp.renameTo(target)) throw new IOException("Could not save model");
                status = "Model ready. Recording and transcription work offline.";
            } catch (Exception e) { status = "Download failed: " + e.getMessage(); }
            finally { if (connection != null) connection.disconnect(); temp.delete(); busy = false; }
        });
    }
}
