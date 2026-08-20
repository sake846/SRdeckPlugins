using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Analog.Dsp;
using SRdeckPlugin.Wpf;
using SRdeckPlugin.Analog.Views;
using SRdeckPlugin.Analog.ViewModels;

namespace SRdeckPlugin.Analog;

public sealed partial class AnalogPluginModule
{
    internal async ValueTask UpdateReceiverOptionsAsync(
        Func<AnalogReceiverOptions, AnalogReceiverOptions> update,
        bool requestTuning,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _receiverGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AnalogReceiverOptions before = Volatile.Read(ref _receiverOptions);
            AnalogReceiverOptions after = ValidateOptions(update(before));
            if (after == before) return;
            if (requestTuning && after.IsReceiverEnabled &&
                State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
            {
                PluginTuningResult? result = await ApplyTuningAsync(after, cancellationToken).ConfigureAwait(false);
                if (result?.Outcome == PluginTuningOutcome.Rejected) return;
            }
            Volatile.Write(ref _receiverOptions, after);
            if (after.BandwidthHz != before.BandwidthHz ||
                after.IsAfcEnabled != before.IsAfcEnabled ||
                after.IsLowerSideband != before.IsLowerSideband)
            {
                _demodulator.Reset();
            }
            if (after.IsMuted != before.IsMuted ||
                after.IsReceiverEnabled != before.IsReceiverEnabled ||
                after.IsSquelchEnabled != before.IsSquelchEnabled)
            {
                _hostContext?.Audio.Reset();
            }
            await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
            NotifyReceiverStateChanged(force: true);
        }
        finally
        {
            _receiverGate.Release();
        }
    }

    internal ValueTask AdjustFrequencyAsync(long deltaHz, CancellationToken cancellationToken = default) =>
        UpdateReceiverOptionsAsync(
            options => options with
            {
                FrequencyHz = Math.Clamp(options.FrequencyHz + deltaHz, 1, int.MaxValue)
            },
            requestTuning: true,
            cancellationToken);

    private async ValueTask ApplyTuningAsync(CancellationToken cancellationToken)
    {
        await ApplyTuningAsync(Volatile.Read(ref _receiverOptions), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PluginTuningResult?> ApplyTuningAsync(
        AnalogReceiverOptions options,
        CancellationToken cancellationToken)
    {
        if (_hostContext is null) return null;
        if (options.FrequencyHz <= 0) return null;
        int minimumSampleRate = Math.Clamp(
            Math.Max(96_000, options.BandwidthHz * 3),
            96_000,
            2_400_000);
        var request = new PluginTuningRequest(
            $"analog.{_selectedProfileId}",
            $"アナログ受信 {_selectedProfileId.ToUpperInvariant()}",
            [new TuningTarget(options.FrequencyHz, options.BandwidthHz)],
            options.FrequencyHz,
            minimumSampleRate,
            options.StepHz,
            true,
            false,
            PluginGainPreference.Automatic);
        PluginTuningResult result = await _hostContext.Tuning.RequestAsync(request, cancellationToken)
            .ConfigureAwait(false);
        _tuningStatus = result.Outcome switch
        {
            PluginTuningOutcome.Accepted => "同調しました。",
            PluginTuningOutcome.Adjusted => $"ホスト調整: {result.Message}",
            PluginTuningOutcome.Deferred => $"保留: {result.Message}",
            _ => $"同調できません: {result.Message}"
        };
        NotifyReceiverStateChanged(force: true);
        return result;
    }

    private void HandleAppliedConfigurationChanged(object? sender, PluginTuningResult result)
    {
        if (result.Outcome != PluginTuningOutcome.Rejected && result.CenterFrequencyHz > 0)
        {
            AnalogReceiverOptions options = Volatile.Read(ref _receiverOptions);
            long stepHz = Math.Max(1, options.StepHz);
            long targetFreqHz = result.TargetFrequencyHz > 0 ? result.TargetFrequencyHz : result.CenterFrequencyHz;
            long roundedFreqHz = ((targetFreqHz + stepHz / 2) / stepHz) * stepHz;
            if (roundedFreqHz != options.FrequencyHz)
            {
                options = options with { FrequencyHz = roundedFreqHz };
                Volatile.Write(ref _receiverOptions, options);
                _ = SaveSettingsAsync(CancellationToken.None);
            }
        }
        _tuningStatus = result.Outcome == PluginTuningOutcome.Rejected
            ? $"同調できません: {result.Message}"
            : $"中心 {result.CenterFrequencyHz:N0} Hz / SR {result.SampleRateHz:N0}";
        NotifyReceiverStateChanged(force: true);
    }

    private void UpdateSignalLevel(ReadOnlySpan<Complex32> samples, AnalogReceiverOptions options)
    {
        if (samples.IsEmpty) return;
        double power = 0;
        foreach (Complex32 sample in samples)
            power += sample.I * sample.I + sample.Q * sample.Q;
        float level = 10f * MathF.Log10(MathF.Max((float)(power / samples.Length), 1e-12f));
        _signalLevelDbfs += 0.2f * (level - _signalLevelDbfs);
        Interlocked.Exchange(ref _lastInputMeasuredUtcTicks, DateTime.UtcNow.Ticks);
        float? calibratedDbm = _hostContext?.ReceiverTelemetry?.SignalLevelDbm;
        float signalLevelForSquelch = (calibratedDbm is not null && float.IsFinite(calibratedDbm.Value))
            ? calibratedDbm.Value
            : _signalLevelDbfs;
        bool wasOpen = _isSquelchOpen;
        _isSquelchOpen = !options.IsSquelchEnabled ||
            (_isSquelchOpen
                ? signalLevelForSquelch >= options.SquelchThresholdDbm - 2f
                : signalLevelForSquelch >= options.SquelchThresholdDbm + 2f);
        if (wasOpen && !_isSquelchOpen) _hostContext?.Audio.Reset();
    }

    private void NotifyReceiverStateChanged(bool force = false)
    {
        EventHandler? handler = ReceiverStateChanged;
        IPluginHostContext? context = _hostContext;
        if (handler is null || context is null) return;
        long now = context.TimeProvider.GetTimestamp();
        if (!force && context.TimeProvider.GetElapsedTime(_lastUiUpdateTimestamp, now) <
            TimeSpan.FromMilliseconds(100)) return;
        _lastUiUpdateTimestamp = now;
        context.Dispatcher.Post(() => handler(this, EventArgs.Empty));
    }

    private static AnalogReceiverOptions ApplyProfileDefaults(
        AnalogReceiverOptions options,
        string profileId) => profileId switch
    {
        "am" => options with { BandwidthHz = 10_000, StepHz = 1_000 },
        "fm" => options with { BandwidthHz = 15_000, StepHz = 12_500 },
        "ssb" => options with { BandwidthHz = 3_000, StepHz = 100 },
        _ => options
    };

    private static AnalogReceiverOptions ValidateOptions(AnalogReceiverOptions options) => options with
    {
        FrequencyHz = Math.Clamp(options.FrequencyHz, 0, int.MaxValue),
        StepHz = Math.Clamp(options.StepHz, 1, 1_000_000),
        BandwidthHz = Math.Clamp(options.BandwidthHz, 1_000, 250_000),
        SquelchThresholdDbm = Math.Clamp(options.SquelchThresholdDbm, -150f, 0f)
    };

    private async ValueTask LoadSettingsAsync(CancellationToken cancellationToken)
    {
        if (_hostContext is null) return;
        try
        {
            PluginSettingsDocument? document = await _hostContext.Settings.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (document is null) return;
            AnalogSettings? settings = JsonSerializer.Deserialize<AnalogSettings>(document.Json);
            if (settings is not null && SupportedProfiles.Any(profile => profile.Id == settings.ProfileId))
            {
                _selectedProfileId = settings.ProfileId;
                float squelchDbm = settings.SquelchThresholdDbm ?? settings.SquelchThresholdDbfs ?? -80f;
                _receiverOptions = ValidateOptions(new AnalogReceiverOptions(
                    settings.FrequencyHz,
                    settings.StepHz,
                    settings.BandwidthHz,
                    settings.IsReceiverEnabled,
                    settings.IsMuted,
                    settings.IsSquelchEnabled,
                    squelchDbm,
                    settings.IsAfcEnabled,
                    settings.IsLowerSideband));
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            _hostContext.Logger.Log(
                PluginLogLevel.Warning,
                "analog.settings.invalid",
                "Analog demodulator settings could not be loaded; AM will be used.",
                exception);
        }
    }

    private async ValueTask SaveSettingsAsync(CancellationToken cancellationToken)
    {
        if (_hostContext is null) return;
        try
        {
            await _hostContext.Settings.SaveAsync(
                new PluginSettingsDocument(2, JsonSerializer.Serialize(new AnalogSettings(
                    _selectedProfileId,
                    _receiverOptions.FrequencyHz,
                    _receiverOptions.StepHz,
                    _receiverOptions.BandwidthHz,
                    _receiverOptions.IsReceiverEnabled,
                    _receiverOptions.IsMuted,
                    _receiverOptions.IsSquelchEnabled,
                    _receiverOptions.SquelchThresholdDbm,
                    _receiverOptions.IsAfcEnabled,
                    _receiverOptions.IsLowerSideband))),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _hostContext.Logger.Log(PluginLogLevel.Warning, "analog.settings.save-failed",
                "Analog settings could not be saved.", exception);
        }
    }

    private sealed record AnalogSettings(
        string ProfileId,
        long FrequencyHz = 0,
        int StepHz = 1_000,
        int BandwidthHz = 10_000,
        bool IsReceiverEnabled = true,
        bool IsMuted = false,
        bool IsSquelchEnabled = false,
        float? SquelchThresholdDbm = null,
        bool IsAfcEnabled = true,
        bool IsLowerSideband = false,
        float? SquelchThresholdDbfs = null);

    public async ValueTask ResetSettingsAsync()
    {
        if (_hostContext is not null)
        {
            await _hostContext.Settings.DeleteAsync().ConfigureAwait(false);
        }
        await _profileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _selectedProfileId = "am";
            _receiverOptions = ValidateOptions(new AnalogReceiverOptions());
            _demodulator.Reset();
            _hostContext?.Audio.Reset();
        }
        finally
        {
            _profileGate.Release();
        }
        NotifyReceiverStateChanged(force: true);
        SelectedProfileChanged?.Invoke(this, EventArgs.Empty);
    }
}
