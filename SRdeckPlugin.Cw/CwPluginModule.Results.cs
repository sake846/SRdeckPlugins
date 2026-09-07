using System.IO;
using System.Buffers.Binary;
using System.Text.Json;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Cw.Dsp;
using SRdeckPlugin.Cw.Models;
using SRdeckPlugin.Cw.ViewModels;
using SRdeckPlugin.Cw.Views;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Cw;

public sealed partial class CwPluginModule
{
    private void OnTextDecoded(object? sender, CwDecodedEventArgs eventArgs)
    {
        CwDecodeResult result = eventArgs.Result;
        IPluginHostContext? context = HostContext;
        bool annotationsAdded;
        lock (resultGate)
        {
            IReadOnlyList<WaterfallAnnotationItem> annotations =
                CreateWaterfallAnnotations(result, ref nextWaterfallAnnotationId);
            foreach (WaterfallAnnotationItem annotation in annotations)
                waterfallCharacters.Add(new CwWaterfallCharacter(result.StreamId, annotation));
            int removeCount = waterfallCharacters.Count - MaximumWaterfallAnnotations;
            if (removeCount > 0) waterfallCharacters.RemoveRange(0, removeCount);
            annotationsAdded = annotations.Count > 0;
        }
        if (annotationsAdded) WaterfallAnnotationsChanged?.Invoke(this, EventArgs.Empty);
        context?.Dispatcher.Post(() => viewModel.Apply(result));
        if (!result.AppendedText.Contains(' ')) return;

        foreach (string callsign in CwCallsignExtractor.Extract(result.Text))
        {
            DateTimeOffset now = context?.TimeProvider.GetUtcNow() ?? result.ReceivedAt;
            bool publish;
            lock (resultGate)
            {
                publish = !publishedCallsigns.TryGetValue(callsign, out DateTimeOffset previous) ||
                    now - previous >= TimeSpan.FromMinutes(2);
                if (publish) publishedCallsigns[callsign] = now;
                foreach (string stale in publishedCallsigns
                    .Where(item => now - item.Value > TimeSpan.FromMinutes(10))
                    .Select(item => item.Key).ToArray())
                    publishedCallsigns.Remove(stale);
            }
            if (!publish) continue;

            string details = JsonSerializer.Serialize(new
            {
                callsign,
                result.FrequencyHz,
                result.SignalToNoiseDb,
                result.SpeedWpm,
                result.Text
            });
            ResultPublished?.Invoke(this, new PluginResultPublishedEventArgs(new PluginResultSummary(
                Guid.NewGuid().ToString("N"),
                Descriptor.Id,
                result.ReceivedAt,
                result.StreamId,
                "cw.callsign",
                PluginResultSeverity.Notice,
                callsign,
                $"{callsign} / {result.FrequencyHz / 1_000.0:F3} kHz / " +
                    $"{result.SpeedWpm} WPM / S/N {result.SignalToNoiseDb:F1} dB",
                result.FrequencyHz,
                result.SignalToNoiseDb,
                1,
                details)));
        }
    }

    private void ApplySettings(CwSettings value)
    {
        long previousCenter = settings.CenterFrequencyHz;
        int previousBandwidth = settings.ReceiveBandwidthHz;
        bool wasMonitorAudioEnabled = settings.MonitorAudioEnabled;
        settings = value.Normalize();
        bool bandwidthChanged = settings.ReceiveBandwidthHz != previousBandwidth;
        if (bandwidthChanged)
        {
            lock (processingGate)
            {
                receiver.Reset();
                pretriggerBuffer.Reset();
                lastCaptureMetadata = null;
                lastCaptureChannelConfiguration = null;
            }
            FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
        }
        if (wasMonitorAudioEnabled && !settings.MonitorAudioEnabled)
        {
            HostContext?.Audio.Reset();
            lock (processingGate) ResetMonitorAudioState();
        }
        if (settings.CenterFrequencyHz > 0 && settings.CenterFrequencyHz != previousCenter)
        {
            SetCenterFrequency(settings.CenterFrequencyHz);
        }
        IPluginHostContext? context = HostContext;
        long targetFrequencyHz = settings.CenterFrequencyHz > 0
            ? settings.CenterFrequencyHz
            : Volatile.Read(ref centerFrequencyHz);
        if (context is not null &&
            State is PluginLifecycleState.Active or PluginLifecycleState.Streaming &&
            (bandwidthChanged || IsOutsideAppliedPassband(
                targetFrequencyHz, context.Tuning.Current, settings.ReceiveBandwidthHz)))
        {
            _ = RetuneOutsidePassbandAsync(context, targetFrequencyHz);
        }
        _ = SaveSettingsAsync(settings);
    }

    private async Task RetuneOutsidePassbandAsync(IPluginHostContext context, long targetFrequencyHz)
    {
        PluginTuningResult result;
        try
        {
            result = await context.Tuning.RequestAsync(
                CreateTuningRequest(targetFrequencyHz), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            context.Logger.Log(PluginLogLevel.Warning, "cw.tuning.failed",
                "CW skimmer could not retune to the configured frequency.", exception);
            context.Dispatcher.Post(() => viewModel.Status = $"選局失敗 / {exception.Message}");
            return;
        }

        if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred ||
            IsOutsideAppliedPassband(targetFrequencyHz, result, settings.ReceiveBandwidthHz))
        {
            context.Dispatcher.Post(() =>
                viewModel.Status = $"選局失敗 / {result.Message}");
            return;
        }

        context.Dispatcher.Post(() =>
        {
            string stateText = State == PluginLifecycleState.Streaming ? "受信中" : "待機中";
            viewModel.Status = $"{stateText} / {targetFrequencyHz / 1_000_000.0:F6} MHz / " +
                $"{CwBandwidth.Format(settings.ReceiveBandwidthHz)}";
        });
    }

    private PluginTuningRequest CreateTuningRequest(long targetFrequencyHz) => new(
        ChannelRequestId,
        $"CW Skimmer / {CwBandwidth.Format(settings.ReceiveBandwidthHz)}",
        [new TuningTarget(targetFrequencyHz, settings.ReceiveBandwidthHz)],
        targetFrequencyHz,
        CwBandwidth.GetOutputSampleRateHz(settings.ReceiveBandwidthHz),
        10,
        true,
        false,
        PluginGainPreference.Automatic);

    private static bool IsOutsideAppliedPassband(
        long targetFrequencyHz,
        PluginTuningResult result,
        int bandwidthHz)
    {
        long halfBandwidthHz = bandwidthHz / 2L;
        return result.PassbandLowerFrequencyHz <= 0 ||
               result.PassbandUpperFrequencyHz <= result.PassbandLowerFrequencyHz ||
               targetFrequencyHz - halfBandwidthHz < result.PassbandLowerFrequencyHz ||
               targetFrequencyHz + halfBandwidthHz > result.PassbandUpperFrequencyHz;
    }

    private void HandleAppliedConfigurationChanged(object? sender, PluginTuningResult result)
    {
        if (result.Outcome == PluginTuningOutcome.Rejected) return;
        long targetFrequencyHz = result.TargetFrequencyHz > 0
            ? result.TargetFrequencyHz
            : result.CenterFrequencyHz;
        if (targetFrequencyHz <= 0 || targetFrequencyHz == Volatile.Read(ref centerFrequencyHz))
            return;

        settings = (settings with { CenterFrequencyHz = targetFrequencyHz }).Normalize();
        SetCenterFrequency(targetFrequencyHz);
        _ = SaveSettingsAsync(settings);
        HostContext?.Dispatcher.Post(() =>
        {
            string stateText = State == PluginLifecycleState.Streaming
                ? "受信中"
                : State == PluginLifecycleState.Active ? "待機中" : "設定済み";
            viewModel.Status = $"{stateText} / {targetFrequencyHz / 1_000_000.0:F6} MHz / " +
                $"{CwBandwidth.Format(settings.ReceiveBandwidthHz)}";
        });
    }

    private void SubmitMonitorAudio(
        ReadOnlySpan<Complex32> samples,
        ChannelIqBlockMetadata metadata,
        CwSettings currentSettings,
        bool discontinuous)
    {
        IPluginHostContext? context = HostContext;
        if (samples.IsEmpty || context is null || !currentSettings.MonitorAudioEnabled) return;

        float volume = currentSettings.MonitorAudioVolume / 100f;
        double phase = monitorAudioPhase;
        float power = monitorPower;
        float gain = monitorGain;
        double sampleRateHz = Math.Max(1, metadata.Configuration.OutputSampleRateHz);
        float monitorPowerAttackCoefficient =
            MonitorTimeConstant(0.025, sampleRateHz);
        float monitorPowerReleaseCoefficient =
            MonitorTimeConstant(0.500, sampleRateHz);
        float monitorGainAttackCoefficient =
            MonitorTimeConstant(0.005, sampleRateHz);
        float monitorGainReleaseCoefficient =
            MonitorTimeConstant(0.100, sampleRateHz);
        double phaseStep = 2 * Math.PI * MonitorAudioCenterHz /
            sampleRateHz;
        byte[] pcm = new byte[samples.Length * sizeof(short)];
        for (int index = 0; index < samples.Length; index++)
        {
            Complex32 sample = samples[index];
            float raw = sample.I * (float)Math.Cos(phase) -
                        sample.Q * (float)Math.Sin(phase);
            phase += phaseStep;
            if (phase >= 2 * Math.PI) phase -= 2 * Math.PI;
            if (!float.IsFinite(raw))
            {
                power = 0;
                gain = 1f;
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan(index * sizeof(short), sizeof(short)), 0);
                continue;
            }

            float instantaneousPower = raw * raw;
            if (!float.IsFinite(instantaneousPower))
            {
                power = 0;
                gain = 1f;
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan(index * sizeof(short), sizeof(short)), 0);
                continue;
            }

            float powerCoefficient = instantaneousPower > power
                ? monitorPowerAttackCoefficient
                : monitorPowerReleaseCoefficient;
            power += powerCoefficient * (instantaneousPower - power);
            if (!float.IsFinite(power))
            {
                power = 0;
                gain = 1f;
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan(index * sizeof(short), sizeof(short)), 0);
                continue;
            }

            float desiredGain = MonitorTargetRms /
                                MathF.Sqrt(MathF.Max(power, 1e-12f));
            if (!float.IsFinite(desiredGain))
            {
                power = 0;
                gain = 1f;
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan(index * sizeof(short), sizeof(short)), 0);
                continue;
            }
            desiredGain = Math.Clamp(desiredGain,
                MonitorMinimumGain, MonitorMaximumGain);
            float gainCoefficient = desiredGain < gain
                ? monitorGainAttackCoefficient
                : monitorGainReleaseCoefficient;
            gain += gainCoefficient * (desiredGain - gain);
            if (!float.IsFinite(gain))
            {
                power = 0;
                gain = 1f;
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan(index * sizeof(short), sizeof(short)), 0);
                continue;
            }

            // A soft limiter keeps a sudden strong signal from producing a
            // full-scale click while the AGC is reducing its gain.
            float limited = MonitorPeakLimit *
                            MathF.Tanh(raw * gain / MonitorPeakLimit);
            short value = (short)MathF.Round(
                Math.Clamp(limited * volume, -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm.AsSpan(index * sizeof(short), sizeof(short)), value);
        }
        monitorAudioPhase = phase;
        monitorPower = power;
        monitorGain = gain;

        context.Audio.TrySubmit(new PcmAudioFrame(
            context.PluginId,
            metadata.Source.StreamId,
            Interlocked.Increment(ref audioSequence),
            metadata.Configuration.OutputSampleRateHz,
            1,
            PcmSampleFormat.Signed16LittleEndian,
            pcm,
            discontinuous));
    }

    private void ResetMonitorAudioState()
    {
        monitorAudioPhase = 0;
        ResetMonitorAudioLevel();
    }

    private void ResetMonitorAudioLevel()
    {
        monitorPower = 0;
        monitorGain = 1f;
    }

    private static float MonitorTimeConstant(double seconds, double sampleRateHz) =>
        (float)(1 - Math.Exp(-1 / (seconds * sampleRateHz)));
}
