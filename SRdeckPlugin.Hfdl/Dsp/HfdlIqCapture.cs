using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.Hfdl.Dsp;

internal sealed class HfdlIqCapture(string path, int sampleRateHz, TimeSpan duration)
    : BoundedIqWavWriter(path, sampleRateHz, duration);
