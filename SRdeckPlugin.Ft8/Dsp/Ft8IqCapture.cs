using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.Ft8.Dsp;

internal sealed class Ft8IqCapture(string path, int sampleRateHz, TimeSpan duration)
    : BoundedIqWavWriter(path, sampleRateHz, duration);
