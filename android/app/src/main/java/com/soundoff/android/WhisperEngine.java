package com.soundoff.android;

final class WhisperEngine {
    static { System.loadLibrary("soundoff"); }
    static native long open(String path);
    static native byte[] transcribe(long handle, float[] audio, String language, String prompt);
    static native void close(long handle);
}
