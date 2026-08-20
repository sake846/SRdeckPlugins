using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.Analog.Dsp;

internal sealed class AnalogIqCapture(string path, int sampleRateHz, TimeSpan duration)
    : BoundedIqWavWriter(path, sampleRateHz, duration);
