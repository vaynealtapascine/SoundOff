#include <jni.h>
#include <lame.h>
#include <vector>

static jbyteArray bytes(JNIEnv *env, const unsigned char *data, int count) {
    if (count < 0) {
        env->ThrowNew(env->FindClass("java/io/IOException"), "MP3 encoding failed");
        return nullptr;
    }
    auto result = env->NewByteArray(count);
    if (result) env->SetByteArrayRegion(result, 0, count, reinterpret_cast<const jbyte *>(data));
    return result;
}
extern "C" JNIEXPORT jlong JNICALL Java_com_soundoff_android_Mp3Encoder_open(JNIEnv *, jclass) {
    lame_t encoder = lame_init();
    if (!encoder) return 0;
    lame_set_in_samplerate(encoder, 16000);
    lame_set_out_samplerate(encoder, 16000);
    lame_set_num_channels(encoder, 1);
    lame_set_mode(encoder, MONO);
    lame_set_brate(encoder, 64);
    lame_set_quality(encoder, 2);
    lame_set_VBR(encoder, vbr_off);
    lame_set_bWriteVbrTag(encoder, 0); // Destinations may be non-seekable document-provider streams.
    if (lame_init_params(encoder) < 0) { lame_close(encoder); return 0; }
    return reinterpret_cast<jlong>(encoder);
}
extern "C" JNIEXPORT jbyteArray JNICALL Java_com_soundoff_android_Mp3Encoder_encode(JNIEnv *env, jclass, jlong handle, jshortArray pcm, jint count) {
    if (!handle || count < 0 || count > env->GetArrayLength(pcm) || count > 8192) return bytes(env, nullptr, -1);
    short input[8192]; unsigned char output[18000];
    env->GetShortArrayRegion(pcm, 0, count, input);
    return bytes(env, output, lame_encode_buffer(reinterpret_cast<lame_t>(handle), input, input, count, output, sizeof(output)));
}
extern "C" JNIEXPORT jbyteArray JNICALL Java_com_soundoff_android_Mp3Encoder_flush(JNIEnv *env, jclass, jlong handle) {
    if (!handle) return bytes(env, nullptr, -1);
    unsigned char output[7200];
    return bytes(env, output, lame_encode_flush(reinterpret_cast<lame_t>(handle), output, sizeof(output)));
}
extern "C" JNIEXPORT void JNICALL Java_com_soundoff_android_Mp3Encoder_close(JNIEnv *, jclass, jlong handle) {
    if (handle) lame_close(reinterpret_cast<lame_t>(handle));
}
