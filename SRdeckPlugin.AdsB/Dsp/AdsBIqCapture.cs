using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.AdsB.Dsp;

internal sealed class AdsBIqCapture(string path, int sampleRateHz, TimeSpan duration)
    : BoundedIqWavWriter(path, sampleRateHz, duration);
