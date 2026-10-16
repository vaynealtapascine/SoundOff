package com.soundoff.android;

import android.app.*;
import android.content.*;
import android.graphics.Color;
import android.media.*;
import android.os.*;
import android.text.*;
import android.view.*;
import android.widget.*;
import java.util.Locale;
import java.io.*;
import android.net.Uri;
import android.Manifest;
import android.content.pm.PackageManager;

/** Audio stays visible while the transcript scrolls and the keyboard is open. */
public class ReviewActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private SessionStore.Session session;
    private MediaPlayer player;
    private AudioManager audio;
    private AudioFocusRequest focus;
    private WaveformView waveform;
    private SeekBar seek;
    private EditText editor;
    private TextView time, saveStatus;
    private Button play, speedButton, record;
    private TextView progress;
    private AudioAttributes attributes;
    private RecordingService observed;
    private int seenCharacters, ticks, generation;
    private boolean preparing, waveLoading, awaitingCapture;
    private long waveSamples = -1;
    private WaveformData.Reader waveReader = new WaveformData.Reader();
    private String pendingExport;
    private boolean pendingMp3;
    AlertDialog audioFormat;
    private boolean prepared, loading = true, dirty, scrubbing, resumeAfterScrub, stopped, seeking;
    private int position, duration, revision;
    private float speed = 1f;
    private int ink, muted, accent, surface;
    private final Runnable save = this::saveEdits;
    private final Runnable tick = new Runnable() {
        @Override public void run() {
            if (prepared && !scrubbing && !seeking) position = player.getCurrentPosition();
            if (++ticks % 5 == 0) refreshSession();
            updatePosition(); handler.postDelayed(this, 100);
        }
    };
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        if (state != null) { position = state.getInt("position"); speed = state.getFloat("speed", 1f); pendingExport = state.getString("export"); pendingMp3 = state.getBoolean("mp3"); }
        audio = getSystemService(AudioManager.class);
        attributes = new AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build();
        focus = new AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN).setAudioAttributes(attributes)
            .setOnAudioFocusChangeListener(change -> { if (change <= 0) { resumeAfterScrub = false; pause(); } }).build();
        boolean dark = (getResources().getConfiguration().uiMode & android.content.res.Configuration.UI_MODE_NIGHT_MASK) == android.content.res.Configuration.UI_MODE_NIGHT_YES;
        ink = dark ? 0xfff1f5ef : 0xff182b25; muted = dark ? 0xffb2c4ba : 0xff52665c;
        accent = dark ? 0xffa1e5c8 : 0xff176b52; surface = dark ? 0xff294638 : 0xffe8f1e9;
        int background = dark ? 0xff10221b : 0xfff3f5ef;
        getWindow().setStatusBarColor(background); getWindow().setNavigationBarColor(background);
        if (!dark) getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR);
        LinearLayout page = new LinearLayout(this); page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(16), dp(8), dp(16), dp(8)); page.setBackgroundColor(background);
        setContentView(page);
        page.setOnApplyWindowInsetsListener((v, insets) -> {
            v.setPadding(dp(16) + insets.getSystemWindowInsetLeft(), dp(8) + insets.getSystemWindowInsetTop(), dp(16) + insets.getSystemWindowInsetRight(), dp(8) + insets.getSystemWindowInsetBottom());
            return insets.consumeSystemWindowInsets();
        });
        LinearLayout header = row(page);
        TextView title = new TextView(this); title.setText("SoundOff"); title.setTextSize(22); title.setTextColor(ink); header.addView(title, new LinearLayout.LayoutParams(0, -2, 2));
        Button more = button(header, "More"); more.setOnClickListener(v -> showMore(more));
        progress = label(page, "Ready to record");
        waveform = new WaveformView(this, dark); page.addView(waveform, new LinearLayout.LayoutParams(-1, dp(getResources().getConfiguration().orientation == android.content.res.Configuration.ORIENTATION_LANDSCAPE ? 44 : 92)));
        waveform.setListener((value, finished) -> {
            if (!prepared) return;
            if (!scrubbing) { scrubbing = true; resumeAfterScrub = player.isPlaying(); pause(); }
            position = value; updatePosition();
            if (finished) { scrubbing = false; seekTo(value); if (resumeAfterScrub) startPlayback(); resumeAfterScrub = false; }
        });
        time = label(page, "Loading waveform…");
        seek = new SeekBar(this); seek.setContentDescription("Playback position"); page.addView(seek, new LinearLayout.LayoutParams(-1, dp(40)));
        seek.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            public void onStartTrackingTouch(SeekBar bar) { scrubbing = true; resumeAfterScrub = prepared && player.isPlaying(); pause(); }
            public void onProgressChanged(SeekBar bar, int value, boolean user) { if (user) { position = value; updatePosition(); if (!scrubbing) seekTo(value); } }
            public void onStopTrackingTouch(SeekBar bar) { scrubbing = false; seekTo(bar.getProgress()); if (resumeAfterScrub) startPlayback(); resumeAfterScrub = false; }
        });
        LinearLayout controls = row(page);
        record = button(controls, "Record"); record.setOnClickListener(v -> recordAction());
        button(controls, "−5 s").setOnClickListener(v -> seekTo(position - 5000));
        play = button(controls, "Play"); play.setEnabled(false); play.setOnClickListener(v -> { if (player.isPlaying()) pause(); else startPlayback(); });
        button(controls, "+5 s").setOnClickListener(v -> seekTo(position + 5000));
        speedButton = button(controls, speedLabel()); speedButton.setContentDescription("Playback speed"); speedButton.setOnClickListener(v -> chooseSpeed());
        LinearLayout zoom = row(page);
        button(zoom, "Zoom in").setOnClickListener(v -> waveform.zoom(.5f));
        button(zoom, "Zoom out").setOnClickListener(v -> waveform.zoom(2));
        button(zoom, "Copy text").setOnClickListener(v -> copyTranscript());
        saveStatus = label(page, "Record or import audio to begin.");
        page.getViewTreeObserver().addOnGlobalLayoutListener(() -> {
            boolean compact = page.getHeight() > 0 && page.getHeight() - page.getPaddingTop() - page.getPaddingBottom() < dp(460);
            zoom.setVisibility(compact ? View.GONE : View.VISIBLE);
            int height = dp(compact ? 44 : 92);
            if (waveform.getLayoutParams().height != height) { waveform.getLayoutParams().height = height; waveform.requestLayout(); }
        });
        saveStatus.setOnClickListener(v -> saveEdits());
        editor = new EditText(this); editor.setId(View.generateViewId()); editor.setSaveEnabled(false);
        editor.setTextColor(ink); editor.setTextSize(18); editor.setGravity(Gravity.TOP);
        editor.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE | InputType.TYPE_TEXT_FLAG_CAP_SENTENCES);
        editor.setHint("Transcript — add or correct words while listening"); editor.setHintTextColor(muted);
        editor.setEnabled(false); page.addView(editor, new LinearLayout.LayoutParams(-1, 0, 1));
        editor.addTextChangedListener(new TextWatcher() {
            public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
            public void onTextChanged(CharSequence s, int start, int before, int count) {
                if (loading) return;
                dirty = true; revision++; saveStatus.setText("Saving edits…"); handler.removeCallbacks(save); handler.postDelayed(save, 400);
            }
            public void afterTextChanged(Editable text) { }
        });
        String id = state == null ? getIntent().getStringExtra("session") : state.getString("session");
        RecordingService live = RecordingService.current;
        if (live != null && !live.finished && live.session != null) { observed = live; id = live.session.dir.getName(); }
        for (SessionStore.Session candidate : SessionStore.list(this)) if (candidate.dir.getName().equals(id)) session = candidate;
        if (session != null) loadSession(session, state == null ? null : state.getString("draft"), state == null ? -1 : state.getInt("seen", -1));
        else { loading = false; waveform.setData(new WaveformData(new float[0], 0)); }
    }
    private RecordingService sessionService() {
        RecordingService service = RecordingService.current;
        return service != null && service.session != null && session != null && service.session.dir.equals(session.dir) ? service : null;
    }
    private boolean busy() { RecordingService s = RecordingService.current; return s != null && !s.finished; }
    private void releasePlayer() {
        pause(); prepared = false; preparing = false; seeking = false;
        if (player != null) { player.release(); player = null; }
        play.setEnabled(false);
    }
    private void loadSession(SessionStore.Session target, String draft, int draftSeen) {
        saveEdits(); releasePlayer(); session = target; generation++; int ticket = generation;
        loading = true; editor.setEnabled(false); waveReader = new WaveformData.Reader(); waveSamples = -1;
        RecordingService service = sessionService();
        SessionStore.IO.execute(() -> {
            try {
                String text; int seen;
                if (service != null) synchronized (service) {
                    seen = service.transcript.length();
                    text = draft != null && draftSeen >= 0 ? TranscriptMerge.preserveUnseen(draft, service.transcript, draftSeen) : target.text();
                } else { text = draft == null ? target.text() : draft; seen = 0; }
                String restored = text; int rawSeen = seen;
                runOnUiThread(() -> {
                    if (isDestroyed() || ticket != generation) return;
                    editor.setText(restored); seenCharacters = rawSeen; loading = false; dirty = draft != null; editor.setEnabled(true);
                    saveStatus.setText("Edits save automatically"); refreshSession();
                });
            } catch (Exception e) { runOnUiThread(() -> { if (ticket == generation) saveStatus.setText("Couldn't open transcript: " + e.getMessage()); }); }
        });
    }
    private void refreshSession() {
        RecordingService service = RecordingService.current;
        if (service != null && !service.finished && (service != observed || awaitingCapture) && service.session != null) {
            awaitingCapture = false; observed = service; position = 0; loadSession(service.session, null, -1); return;
        }
        RecordingService own = sessionService();
        boolean active = service != null && !service.finished;
        boolean capturing = own != null && own.capturing;
        boolean stopping = active && service.capturing && service.stopRequested;
        record.setText(active && service.importing ? "Importing..." : stopping ? "Stopping..." : capturing ? "Stop" : session != null ? "Resume" : "Record");
        record.setEnabled(!stopping && !(active && service.importing));
        if (capturing && (prepared || preparing)) releasePlayer();
        if (own != null && !loading) {
            String raw = own.transcript;
            if (raw.length() > seenCharacters) {
                loading = true; editor.getText().append(raw.substring(seenCharacters)); seenCharacters = raw.length(); loading = false;
            }
        }
        if (active) {
            long waiting = Math.max(0, service.samples - service.processed) * 1000 / WavFile.RATE;
            progress.setText((service.importing ? "Importing" : service.capturing && !service.stopRequested ? "Recording" : "Catching up")
                + " · " + format((int) waiting) + " waiting for text");
        } else if (ModelStore.busy) progress.setText(ModelStore.status);
        else if (own != null) progress.setText(own.message);
        else progress.setText(session == null ? "Ready to record" : "Saved recording");
        if (session == null || loading) return;
        long samples = Math.max(0, session.audio().length() - 44) / 2;
        if (!waveLoading && samples != waveSamples && session.audio().isFile()) {
            waveLoading = true; int ticket = generation; SessionStore.Session target = session; WaveformData.Reader reader = waveReader;
            SessionStore.IO.execute(() -> {
                try {
                    WaveformData data = reader.update(target.audio());
                    runOnUiThread(() -> { waveLoading = false; if (ticket != generation || isDestroyed()) return;
                        waveSamples = samples; waveform.setData(data); duration = data.durationMs; seek.setMax(duration);
                        if (capturing) { position = duration; updatePosition(); }
                    });
                } catch (Exception e) { runOnUiThread(() -> { waveLoading = false; if (ticket == generation) saveStatus.setText("Waveform unavailable: " + e.getMessage()); }); }
            });
        }
        seek.setEnabled(!capturing && prepared);
        if (!capturing && !prepared && !preparing && samples > 0) {
            preparing = true; int ticket = generation; SessionStore.Session target = session;
            SessionStore.IO.execute(() -> {
                try { WavFile.repair(target.audio()); runOnUiThread(() -> { if (ticket == generation && !isDestroyed()) { position = Math.min(position, duration); preparePlayer(attributes); } }); }
                catch (Exception e) { runOnUiThread(() -> saveStatus.setText("Couldn't open audio: " + e.getMessage())); }
            });
        }
    }
    private void preparePlayer(AudioAttributes attributes) {
        try {
            player = new MediaPlayer(); player.setAudioAttributes(attributes); player.setDataSource(session.audio().getAbsolutePath());
            player.setOnPreparedListener(p -> { preparing = false; prepared = true; duration = p.getDuration(); seek.setMax(duration); seekTo(position); play.setEnabled(true); });
            player.setOnSeekCompleteListener(p -> { seeking = false; });
            player.setOnCompletionListener(p -> { play.setText("Play"); audio.abandonAudioFocusRequest(focus); });
            player.setOnErrorListener((p, what, extra) -> { prepared = false; play.setEnabled(false); saveStatus.setText("Audio playback unavailable. Transcript edits still work."); return true; });
            player.prepareAsync();
        } catch (Exception e) { saveStatus.setText("Couldn't play recording: " + e.getMessage()); }
    }
    void seekTo(int value) {
        position = Math.max(0, Math.min(duration, value));
        if (prepared) { seeking = true; player.seekTo(position, MediaPlayer.SEEK_CLOSEST); }
        updatePosition();
    }
    private void startPlayback() {
        if (!prepared || stopped || audio.requestAudioFocus(focus) != AudioManager.AUDIOFOCUS_REQUEST_GRANTED) return;
        try {
            if (position >= duration) seekTo(0);
            player.setPlaybackParams(new PlaybackParams().setPitch(1f).setSpeed(speed)); player.start(); play.setText("Pause");
        } catch (RuntimeException e) { pause(); saveStatus.setText("This phone couldn't play at that speed. Try another value."); }
    }
    private void pause() { if (prepared && player.isPlaying()) player.pause(); if (play != null) play.setText("Play"); if (audio != null && focus != null) audio.abandonAudioFocusRequest(focus); }
    void setSpeed(float value) {
        if (!Float.isFinite(value) || value < .1f || value > 4f) throw new IllegalArgumentException("Enter a speed from 0.1 to 4.0");
        boolean playing = prepared && player.isPlaying(); speed = value; speedButton.setText(speedLabel());
        if (playing) startPlayback();
    }
    private void chooseSpeed() {
        EditText input = new EditText(this); input.setInputType(InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_DECIMAL); input.setText(Float.toString(speed)); input.selectAll();
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle("Playback speed").setMessage("Any value from 0.1× to 4.0×. Pitch stays unchanged.").setView(input).setNegativeButton("Cancel", null).setPositiveButton("Apply", null).create();
        dialog.setOnShowListener(d -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v -> {
            try { setSpeed(Float.parseFloat(input.getText().toString().trim().replace(',', '.'))); dialog.dismiss(); }
            catch (IllegalArgumentException e) { input.setError("Enter a number from 0.1 to 4.0"); }
        })); dialog.show();
    }
    private String speedLabel() { return String.format(Locale.ROOT, "%.2f×", speed); }
    private void updatePosition() {
        if (time == null) return;
        time.setText(format(position) + " / " + format(duration)); waveform.setPosition(position);
        if (!scrubbing) seek.setProgress(position);
    }
    private static String format(int ms) { int s = ms / 1000; return String.format(Locale.ROOT, "%d:%02d:%02d", s / 3600, s / 60 % 60, s % 60); }
    private void saveEdits() {
        handler.removeCallbacks(save);
        if (!dirty || loading || session == null) return;
        String text = editor.getText().toString(); int savedRevision = revision; dirty = false;
        SessionStore.Session target = session; RecordingService service = sessionService(); int seen = seenCharacters; int ticket = generation;
        SessionStore.IO.execute(() -> {
            try { if (service == null) target.saveText(text); else service.saveEdited(text, seen); runOnUiThread(() -> { if (ticket == generation && revision == savedRevision) saveStatus.setText("All edits saved on this device"); }); }
            catch (Exception e) { runOnUiThread(() -> { if (ticket != generation) return; dirty = true; saveStatus.setText("Edits couldn't be saved. Tap this message to retry."); }); }
        });
    }
    @Override protected void onStart() { super.onStart(); stopped = false; handler.post(tick); }
    @Override protected void onPause() { saveEdits(); pause(); super.onPause(); }
    @Override protected void onStop() { stopped = true; resumeAfterScrub = false; handler.removeCallbacks(tick); super.onStop(); }
    @Override protected void onDestroy() { handler.removeCallbacksAndMessages(null); if (player != null) player.release(); super.onDestroy(); }
    @Override protected void onSaveInstanceState(Bundle state) {
        super.onSaveInstanceState(state); state.putInt("position", position); state.putFloat("speed", speed);
        if (session != null) state.putString("session", session.dir.getName());
        state.putInt("seen", seenCharacters); state.putString("export", pendingExport); state.putBoolean("mp3", pendingMp3);
        if (!loading) state.putString("draft", editor.getText().toString());
    }
    private android.content.SharedPreferences preferences() { return getSharedPreferences("MainActivity", MODE_PRIVATE); }
    private int selectedModel() { return Math.max(0, Math.min(ModelStore.NAMES.length - 1, preferences().getInt("model", 0))); }
    private String selectedLanguage() { return new String[]{"auto", "en", "tl"}[Math.max(0, Math.min(2, preferences().getInt("language", 0)))]; }
    private void recordAction() {
        RecordingService service = RecordingService.current;
        if (service != null && !service.finished && service.capturing) {
            if (!service.stopRequested && !service.importing) startService(new Intent(this, RecordingService.class).setAction(RecordingService.STOP));
            return;
        }
        if (service != null && !service.finished && service.importing) return;
        int modelIndex = selectedModel();
        if (session != null) {
            String savedModel = session.metadata().optString("model", ModelStore.NAMES[modelIndex]);
            for (int i = 0; i < ModelStore.NAMES.length; i++) if (ModelStore.NAMES[i].equals(savedModel)) modelIndex = i;
        }
        if (!ModelStore.file(this, modelIndex).isFile()) { showSettings(); return; }
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO}, 10); return;
        }
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED && !preferences().getBoolean("notificationAsked", false)) {
            preferences().edit().putBoolean("notificationAsked", true).apply(); requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 11); return;
        }
        saveEdits(); releasePlayer();
        Intent capture = new Intent(this, RecordingService.class).putExtra("language", selectedLanguage()).putExtra("model", modelIndex);
        if (session != null) capture.setAction(RecordingService.RESUME).putExtra("session", session.dir.getName());
        // Flush queued corrections before a restarted service can append new text.
        SessionStore.IO.execute(() -> runOnUiThread(() -> {
            if (isDestroyed() || stopped) return;
            try { awaitingCapture = true; loading = true; editor.setEnabled(false); startForegroundService(capture); }
            catch (Exception e) { awaitingCapture = false; loading = false; editor.setEnabled(session != null); toast("Couldn't start recording: " + e.getMessage()); }
        }));
    }
    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] grants) {
        super.onRequestPermissionsResult(request, permissions, grants);
        if (request == 11 || request == 10 && grants.length > 0 && grants[0] == PackageManager.PERMISSION_GRANTED) recordAction();
        else if (request == 10) new AlertDialog.Builder(this).setTitle("Microphone permission needed")
            .setMessage("Allow microphone access in Android settings to record.").setNegativeButton("Cancel", null)
            .setPositiveButton("App settings", (d, w) -> startActivity(new Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + getPackageName())))).show();
    }
    private void showMore(View anchor) {
        PopupMenu menu = new PopupMenu(this, anchor);
        String[] labels = {"New recording", "Import audio", "Saved recordings", "Copy transcript", "Save audio to files", "Language & model"};
        for (int i = 0; i < labels.length; i++) menu.getMenu().add(0, i, i, labels[i]);
        menu.getMenu().findItem(0).setEnabled(!busy()); menu.getMenu().findItem(1).setEnabled(!busy());
        menu.getMenu().findItem(2).setEnabled(!busy()); menu.getMenu().findItem(3).setEnabled(editor.length() > 0);
        RecordingService own = sessionService();
        menu.getMenu().findItem(4).setEnabled(session != null && session.audio().length() > 44 && (own == null || !own.capturing));
        menu.getMenu().findItem(5).setEnabled(!busy());
        menu.setOnMenuItemClickListener(item -> {
            switch (item.getItemId()) {
                case 0: newRecording(); break;
                case 1: importAudio(); break;
                case 2: showLibrary(); break;
                case 3: copyTranscript(); break;
                case 4: showExport(); break;
                case 5: showSettings(); break;
            }
            return true;
        }); menu.show();
    }
    private void newRecording() {
        if (busy()) return;
        saveEdits(); releasePlayer(); session = null; generation++; position = 0; duration = 0; seenCharacters = 0; dirty = false;
        loading = true; editor.setText(""); loading = false; editor.setEnabled(false);
        waveform.setData(new WaveformData(new float[0], 0)); seek.setProgress(0); seek.setEnabled(false);
        saveStatus.setText("Record or import audio to begin."); refreshSession(); updatePosition();
    }
    void importAudio() {
        if (busy()) return;
        if (!ModelStore.file(this, selectedModel()).isFile()) { showSettings(); return; }
        try { startActivityForResult(new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("audio/*").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION), 21); }
        catch (ActivityNotFoundException e) { toast("No file picker installed"); }
    }
    void copyTranscript() {
        getSystemService(android.content.ClipboardManager.class).setPrimaryClip(ClipData.newPlainText("SoundOff transcript", editor.getText().toString())); toast("Transcript copied");
    }
    private void showLibrary() {
        java.util.List<SessionStore.Session> sessions = SessionStore.list(this);
        if (sessions.isEmpty()) { toast("No saved recordings yet"); return; }
        String[] labels = new String[sessions.size()];
        for (int i = 0; i < labels.length; i++) labels[i] = sessions.get(i).title + "\n" + sessions.get(i).status().replace("Recording", "Interrupted");
        new AlertDialog.Builder(this).setTitle("Saved recordings").setItems(labels, (d, which) -> { position = 0; loadSession(sessions.get(which), null, -1); }).setNegativeButton("Close", null).show();
    }
    private void showSettings() {
        LinearLayout settings = new LinearLayout(this); settings.setOrientation(LinearLayout.VERTICAL); settings.setPadding(dp(24), dp(8), dp(24), dp(8));
        label(settings, "Conversation language").setTextColor(0xff52665c);
        Spinner language = new Spinner(this); language.setAdapter(new ArrayAdapter<>(this, android.R.layout.simple_spinner_dropdown_item, new String[]{"Mixed English + Filipino", "English", "Filipino / Tagalog"}));
        language.setSelection(preferences().getInt("language", 0)); settings.addView(language);
        label(settings, "Multilingual speech model").setTextColor(0xff52665c);
        Spinner model = new Spinner(this); model.setAdapter(new ArrayAdapter<>(this, android.R.layout.simple_spinner_dropdown_item, new String[]{"Tiny · 78 MB", "Base · 148 MB", "Small Q5 · 190 MB"}));
        model.setSelection(selectedModel()); settings.addView(model);
        TextView note = label(settings, "Larger models may take longer. Audio keeps recording, and text catches up after you stop. Download a model once for offline use.");
        note.setTextColor(0xff52665c); settings.setBackgroundColor(android.graphics.Color.WHITE);
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle("Language & model").setView(settings).setNegativeButton("Close", null).setPositiveButton("Use model", null).setNeutralButton("Download", null).create();
        dialog.setOnShowListener(d -> {
            Runnable persist = () -> preferences().edit().putInt("model", model.getSelectedItemPosition()).putInt("language", language.getSelectedItemPosition()).apply();
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v -> { persist.run(); if (!ModelStore.file(this, selectedModel()).isFile()) note.setText("Download this model first, then tap Use model."); else dialog.dismiss(); });
            dialog.getButton(AlertDialog.BUTTON_NEUTRAL).setOnClickListener(v -> { persist.run(); if (!ModelStore.file(this, selectedModel()).isFile()) ModelStore.download(getApplicationContext(), selectedModel()); });
            Runnable poll = new Runnable() { public void run() { if (!dialog.isShowing()) return; if (ModelStore.busy) note.setText(ModelStore.status); else if (ModelStore.file(ReviewActivity.this, model.getSelectedItemPosition()).isFile()) note.setText("Model ready. Audio stays on this device; text catches up after recording."); else if (!ModelStore.status.isEmpty()) note.setText(ModelStore.status); handler.postDelayed(this, 500); } }; handler.post(poll);
        }); dialog.show();
    }
    void showExport() {
        if (session == null) return;
        audioFormat = new AlertDialog.Builder(this).setTitle("Audio format").setItems(new String[]{"WAV · original PCM quality", "MP3 · smaller file, 64 kbps"}, (d, which) -> exportAudio(which == 1)).show();
    }
    private void exportAudio(boolean mp3) {
        pendingExport = session.dir.getName(); pendingMp3 = mp3;
        try { startActivityForResult(new Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType(mp3 ? "audio/mpeg" : "audio/wav")
            .putExtra(Intent.EXTRA_TITLE, "SoundOff-" + pendingExport + (mp3 ? ".mp3" : ".wav")), 20); }
        catch (ActivityNotFoundException e) { toast("No file picker installed"); }
    }
    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (result != RESULT_OK || data == null || data.getData() == null) return;
        if (request == 21) {
            if (busy()) return;
            saveEdits(); releasePlayer();
            try { startForegroundService(new Intent(this, RecordingService.class).setAction(RecordingService.IMPORT).setData(data.getData()).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                .putExtra("language", selectedLanguage()).putExtra("model", selectedModel())); }
            catch (Exception e) { toast("Couldn't import audio: " + e.getMessage()); }
        } else if (request == 20 && pendingExport != null) {
            File source = null;
            for (SessionStore.Session candidate : SessionStore.list(this)) if (candidate.dir.getName().equals(pendingExport)) source = candidate.audio();
            pendingExport = null; if (source == null) { toast("Recording not found"); return; }
            File file = source; boolean mp3 = pendingMp3; Uri uri = data.getData(); toast(mp3 ? "Encoding MP3…" : "Saving WAV…");
            SessionStore.IO.execute(() -> {
                try {
                    WavFile.repair(file);
                    try (OutputStream out = getContentResolver().openOutputStream(uri, "wt")) {
                        if (out == null) throw new IOException("File destination unavailable");
                        if (mp3) Mp3Encoder.export(file, out);
                        else try (InputStream in = new FileInputStream(file)) { byte[] buffer = new byte[65536]; int n; while ((n = in.read(buffer)) != -1) out.write(buffer, 0, n); }
                    }
                    runOnUiThread(() -> toast("Audio saved to files"));
                } catch (Exception e) { runOnUiThread(() -> toast("Export failed: " + e.getMessage())); }
            });
        }
    }
    private void toast(String message) { Toast.makeText(this, message, Toast.LENGTH_LONG).show(); }

    private int dp(int n) { return Math.round(n * getResources().getDisplayMetrics().density); }
    private LinearLayout row(LinearLayout page) { LinearLayout row = new LinearLayout(this); row.setGravity(Gravity.CENTER_VERTICAL); page.addView(row, new LinearLayout.LayoutParams(-1, -2)); return row; }
    private TextView label(LinearLayout page, String text) { TextView label = new TextView(this); label.setText(text); label.setTextColor(muted); label.setTextSize(13); page.addView(label); return label; }
    private Button button(LinearLayout row, String text) { Button b = new Button(this); b.setText(text); b.setAllCaps(false); b.setTextSize(13); b.setPadding(0, 0, 0, 0); b.setMinWidth(0); b.setMinimumWidth(0); b.setMinHeight(dp(48)); b.setTextColor(accent); b.setBackgroundTintList(android.content.res.ColorStateList.valueOf(surface)); row.addView(b, new LinearLayout.LayoutParams(0, -2, 1)); return b; }
}
