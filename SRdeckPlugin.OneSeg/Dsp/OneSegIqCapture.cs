using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.OneSeg.Dsp;

internal sealed class OneSegIqCapture(string path, int sampleRateHz, TimeSpan duration)
    : BoundedIqWavWriter(path, sampleRateHz, duration);
