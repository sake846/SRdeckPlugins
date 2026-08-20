using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.Vdl.Dsp;

/// <summary>Bounded raw and channel-IQ WAV capture used for offline VDL analysis.</summary>
internal sealed class VdlIqCapture : IDisposable
{
    private readonly BoundedIqWavWriter rawWriter;
    private readonly BoundedIqWavWriter channelWriter;
    private bool disposed;

    public VdlIqCapture(string basePath, int rawSampleRateHz, TimeSpan duration)
    {
        BasePath = basePath;
        RawPath = basePath + "-raw-iq.wav";
        ChannelPath = basePath + "-channel-iq.wav";
        DiagnosticsPath = basePath + "-diagnostics.json";
        rawWriter = new(RawPath, rawSampleRateHz, duration);
        channelWriter = new(ChannelPath, VdlMode2Receiver.WorkingSampleRate, duration);
    }

    public string BasePath { get; }
    public string RawPath { get; }
    public string ChannelPath { get; }
    public string DiagnosticsPath { get; }
    public int RawSampleRateHz => rawWriter.SampleRateHz;
    public bool IsComplete => rawWriter.IsComplete;

    public void WriteRaw(ReadOnlySpan<Complex32> samples) => rawWriter.Write(samples);
    public void WriteChannel(ReadOnlySpan<Complex32> samples) => channelWriter.Write(samples);
    public void WriteRawPcm(ReadOnlySpan<short> interleaved) => rawWriter.WritePcm(interleaved);
    public void WriteChannelPcm(ReadOnlySpan<short> interleaved) => channelWriter.WritePcm(interleaved);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        rawWriter.Dispose();
        channelWriter.Dispose();
    }
}
