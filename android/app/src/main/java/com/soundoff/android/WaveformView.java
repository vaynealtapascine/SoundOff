package com.soundoff.android;

import android.content.Context;
import android.graphics.*;
import android.view.*;

/** Drag the audio beneath a fixed playhead; time is clamped at both ends. */
final class WaveformView extends View {
    interface Listener { void scrub(int position, boolean finished); }
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private WaveformData data;
    private Listener listener;
    private int position;
    private float windowMs = 15000, downX;
    private int downPosition;
    private boolean dragging;
    private final boolean dark;
    WaveformView(Context context, boolean dark) {
        super(context); this.dark = dark; setContentDescription("Audio waveform. Drag left or right to scrub; use the seek slider for accessible seeking.");
    }
    void setData(WaveformData value) { data = value; invalidate(); }
    void setListener(Listener value) { listener = value; }
    void setPosition(int value) { if (!dragging) { position = value; invalidate(); } }
    void zoom(float factor) { windowMs = Math.max(3000, Math.min(120000, windowMs * factor)); invalidate(); }
    @Override protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        float w = getWidth(), h = getHeight();
        canvas.drawColor(dark ? 0xff294638 : 0xffe8f1e9);
        if (data != null) {
            float start = position - windowMs / 2;
            for (int x = 0; x < w; x += 3) {
                int first = Math.max(0, (int) ((start + x / w * windowMs) / 100));
                int last = Math.min(data.peaks.length - 1, (int) ((start + (x + 3) / w * windowMs) / 100));
                float peak = 0;
                if (start + (x + 3) / w * windowMs < 0) continue;
                for (int i = first; i <= last; i++) peak = Math.max(peak, data.peaks[i]);
                if (first >= data.peaks.length) continue;
                paint.setColor(x < w / 2 ? (dark ? 0xffa1e5c8 : 0xff176b52) : 0xff749b88);
                float bar = Math.max(1, peak * h * .42f);
                canvas.drawRect(x, h / 2 - bar, x + 2, h / 2 + bar, paint);
            }
        }
        paint.setColor(dark ? 0xfff1f5ef : 0xff182b25); paint.setStrokeWidth(2); canvas.drawLine(w / 2, 0, w / 2, h, paint);
    }
    @Override public boolean onTouchEvent(android.view.MotionEvent event) {
        if (data == null || getWidth() == 0) return false;
        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                downX = event.getX(); downPosition = position; dragging = true;
                getParent().requestDisallowInterceptTouchEvent(true);
                if (listener != null) listener.scrub(position, false);
                return true;
            case MotionEvent.ACTION_MOVE:
                position = Math.max(0, Math.min(data.durationMs, downPosition + Math.round((downX - event.getX()) / getWidth() * windowMs)));
                if (listener != null) listener.scrub(position, false);
                invalidate(); return true;
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                dragging = false;
                if (listener != null) listener.scrub(position, true);
                performClick(); return true;
            default: return true;
        }
    }
    @Override public boolean performClick() { super.performClick(); return true; }
}
