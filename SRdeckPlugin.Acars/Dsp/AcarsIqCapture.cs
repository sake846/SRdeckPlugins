using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.Acars.Dsp;

internal sealed class AcarsIqCapture(string path, int sampleRateHz, TimeSpan duration)
    : BoundedIqWavWriter(path, sampleRateHz, duration);
