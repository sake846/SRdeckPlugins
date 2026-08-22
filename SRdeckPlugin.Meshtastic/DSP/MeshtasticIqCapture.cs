using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.Meshtastic.Dsp;

internal sealed class MeshtasticIqCapture(string path, int sampleRateHz, TimeSpan duration)
    : BoundedIqWavWriter(path, sampleRateHz, duration);
