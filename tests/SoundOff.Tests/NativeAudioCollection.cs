using Xunit;

namespace SoundOff.Tests;

// WaveOutEvent schedules its producer on the pool. Heavy inference/parallel UI tests must not
// compete with short device-clock observations; this does not replace any real-device assertion.
[CollectionDefinition("Native audio", DisableParallelization = true)]
public sealed class NativeAudioCollection { }
