#include <jni.h>
#include <whisper.h>
#include <string>
#include <vector>
#include <algorithm>
#include <thread>

extern "C" JNIEXPORT jlong JNICALL
Java_com_soundoff_android_WhisperEngine_open(JNIEnv *env, jclass, jstring path) {
    const char *p = env->GetStringUTFChars(path, nullptr);
    auto params = whisper_context_default_params();
    params.use_gpu = false;
    whisper_context *ctx = nullptr;
    try { ctx = whisper_init_from_file_with_params(p, params); } catch (...) { /* Java reports model failure. */ }
    env->ReleaseStringUTFChars(path, p);
    return reinterpret_cast<jlong>(ctx);
}

extern "C" JNIEXPORT jbyteArray JNICALL
Java_com_soundoff_android_WhisperEngine_transcribe(JNIEnv *env, jclass, jlong handle,
        jfloatArray audio, jstring language, jstring prompt) {
    auto *ctx = reinterpret_cast<whisper_context *>(handle);
    if (!ctx) return nullptr;
    const auto n = env->GetArrayLength(audio);
    // Pad very short final chunks to one second for Whisper's encoder.
    std::vector<float> pcm(std::max(16000, n), 0.0f);
    env->GetFloatArrayRegion(audio, 0, n, pcm.data());
    const char *lang = env->GetStringUTFChars(language, nullptr);
    const char *hint = env->GetStringUTFChars(prompt, nullptr);
    auto params = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
    params.n_threads = std::max(1u, std::min(4u, std::thread::hardware_concurrency()));
    params.translate = false;
    params.language = lang;
    params.initial_prompt = hint;
    params.no_context = true;
    params.single_segment = true;
    params.print_progress = params.print_realtime = params.print_timestamps = params.print_special = false;
    int result = -1;
    // Never unwind a C++ exception through ART; recording must survive inference failure.
    try { result = whisper_full(ctx, params, pcm.data(), static_cast<int>(pcm.size())); } catch (...) { result = -1; }
    env->ReleaseStringUTFChars(language, lang);
    env->ReleaseStringUTFChars(prompt, hint);
    if (result != 0) return nullptr;
    std::string text;
    for (int i = 0; i < whisper_full_n_segments(ctx); ++i) text += whisper_full_get_segment_text(ctx, i);
    // Return real UTF-8 bytes (JNI NewStringUTF expects modified UTF-8).
    auto bytes = env->NewByteArray(static_cast<jsize>(text.size()));
    env->SetByteArrayRegion(bytes, 0, static_cast<jsize>(text.size()), reinterpret_cast<const jbyte *>(text.data()));
    return bytes;
}

extern "C" JNIEXPORT void JNICALL
Java_com_soundoff_android_WhisperEngine_close(JNIEnv *, jclass, jlong handle) {
    if (handle) whisper_free(reinterpret_cast<whisper_context *>(handle));
}
