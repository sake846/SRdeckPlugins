using System.IO;
using System.Text.Json;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.AribT98;

public sealed record AribT98IqCaptureResult(string WavPath, string MetadataPath, string? GainPath, double DurationSeconds);

public sealed partial class AribT98PluginModule
{
    public const int IqHistorySeconds = 20;
    private readonly PackedIqHistoryBuffer iqHistory = new(IqHistorySeconds);
    private ChannelIqBlockMetadata? lastIqMetadata;
    private long iqHistorySamples;
    private DateTimeOffset iqEndUtc;
    private int captureSaveInProgress;

    private void ResetIqHistory()
    {
        iqHistory.Reset(); iqHistorySamples = 0; lastIqMetadata = null;
        HostContext?.Dispatcher.Post(() => viewModel.IqBufferedSeconds = 0);
    }

    private void CaptureIq(IChannelIqBlockLease block)
    {
        if (block.Samples.IsEmpty) return;
        var metadata = block.Metadata;
        iqHistory.Write(block.Samples.Span, metadata.Configuration.OutputSampleRateHz);
        iqHistorySamples = Math.Min(IqHistorySeconds * 48000L, iqHistorySamples + block.Samples.Length);
        lastIqMetadata = metadata;
        // Output positions are absolute within the channel stream; account for channelizer delay.
        long sourceEnd = metadata.MapOutputToSource(metadata.OutputSampleStart + block.Samples.Length);
        iqEndUtc = metadata.Source.UtcTimestamp.AddSeconds(
            (sourceEnd - metadata.Source.AbsoluteSampleStart) / (double)metadata.Source.SampleRateHz);
    }

    public async Task<AribT98IqCaptureResult> SaveRecentIqAsync()
    {
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref captureSaveInProgress, 1, 0) != 0)
            throw new InvalidOperationException("IQを保存中です。完了するまでお待ちください。");
        try
        {
            PackedIqHistorySnapshot snapshot;
            ChannelIqBlockMetadata metadata;
            DateTimeOffset endUtc;
            string directory;
            AribT98Modulation modulation;
            lock (processingGate)
            {
                ThrowIfDisposed();
                // Snapshot samples and metadata together before releasing the receiver lock.
                // A subsequent retune must not relabel the old capture with the new channel.
                snapshot = iqHistory.TakeSnapshot() ?? throw new InvalidOperationException("保存できるIQがありません。受信を開始してください。");
                metadata = lastIqMetadata ?? throw new InvalidOperationException("IQの受信情報がありません。");
                endUtc = iqEndUtc;
                modulation = settings.Modulation;
                directory = Path.Combine(HostContext!.Settings.DataDirectory, "captures");
            }
            return await Task.Run(() => WriteIqCapture(directory, snapshot, metadata, endUtc, modulation)).ConfigureAwait(false);
        }
        finally { Interlocked.Exchange(ref captureSaveInProgress, 0); }
    }

    private static AribT98IqCaptureResult WriteIqCapture(string directory, PackedIqHistorySnapshot snapshot,
        ChannelIqBlockMetadata metadata, DateTimeOffset endUtc, AribT98Modulation modulation)
    {
        Directory.CreateDirectory(directory);
        long frequency = metadata.Configuration.ChannelCenterFrequencyHz;
        string stem = Path.Combine(directory, $"arib-t98-iq-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{frequency}Hz-{Guid.NewGuid():N}");
        string wavPath = stem + ".wav", jsonPath = stem + ".json";
        string? gainPath = frequency <= int.MaxValue ? stem + ".gain" : null;
        // Stage all outputs, exposing the WAV last so playback cannot see an incomplete file.
        string wavTemp = wavPath + ".tmp", jsonTemp = jsonPath + ".tmp", gainTemp = stem + ".gain.tmp";
        var created = new List<string>();
        try
        {
            using (var writer = new BoundedIqWavWriter(wavTemp, snapshot.SampleRateHz, TimeSpan.FromSeconds(IqHistorySeconds)))
            {
                created.Add(wavTemp);
                writer.WritePcm(snapshot.RawInterleaved);
            }
            if (gainPath is not null)
            {
                // SRdeck playback's .gain record: sample index, gain, RF center. Zero gain
                // is an uncalibrated placeholder; only the RF center is known for channel IQ.
                using var gain = new BinaryWriter(new FileStream(gainTemp, FileMode.CreateNew, FileAccess.Write));
                created.Add(gainTemp);
                gain.Write(0L); gain.Write(0f); gain.Write((int)frequency);
            }
            var document = new
            {
                Format = "SRdeck ARIB STD-T98 channel IQ v1",
                SavedAtUtc = DateTimeOffset.Now,
                CaptureMode = "rolling pre-trigger",
                MaximumDurationSeconds = IqHistorySeconds,
                FrequencyHz = frequency,
                Channel = AribT98Channels.Find(frequency).Display,
                Modulation = modulation.ToString(),
                SampleFormat = "PCM16 little endian, stereo: left=I / right=Q",
                SampleRateHz = snapshot.SampleRateHz,
                SampleCount = snapshot.RawInterleaved.Length / 2,
                snapshot.DurationSeconds,
                StartUtc = endUtc.AddSeconds(-snapshot.DurationSeconds).ToLocalTime(),
                EndUtcExclusive = endUtc.ToLocalTime(),
                WavFile = Path.GetFileName(wavPath),
                GainFile = gainPath is null ? null : Path.GetFileName(gainPath),
                AmplitudeCalibration = "Uncalibrated channel IQ; gain=0 is a playback placeholder, not measured RF gain",
                InputSource = metadata.Source.InputSource.ToString(),
                metadata.Source.StreamId,
                metadata.Source.Generation,
                ChannelConfiguration = metadata.Configuration
                // Deliberately no PrivacyKey, automatic code, or PSK arbitrary seed.
            };
            using (var json = new FileStream(jsonTemp, FileMode.CreateNew, FileAccess.Write))
            {
                created.Add(jsonTemp);
                JsonSerializer.Serialize(json, document, new JsonSerializerOptions { WriteIndented = true });
            }
            File.Move(jsonTemp, jsonPath); created.Add(jsonPath);
            if (gainPath is not null) { File.Move(gainTemp, gainPath); created.Add(gainPath); }
            File.Move(wavTemp, wavPath); created.Add(wavPath);
            return new(wavPath, jsonPath, gainPath, snapshot.DurationSeconds);
        }
        catch
        {
            // Only files created by this unique save attempt are removed.
            foreach (string path in created)
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
