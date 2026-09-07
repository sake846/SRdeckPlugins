using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.Cw.Dsp;

internal sealed class CwIqCapture(string path, int sampleRateHz, TimeSpan duration)
    : BoundedIqWavWriter(path, sampleRateHz, duration);
