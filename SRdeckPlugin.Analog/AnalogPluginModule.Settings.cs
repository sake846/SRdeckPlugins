using System.Text.Json;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Analog;

public sealed partial class AnalogPluginModule
{
    internal async ValueTask UpdateReceiverOptionsAsync(
        Func<AnalogReceiverOptions, AnalogReceiverOptions> update,
        bool requestTuning,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        AnalogReceiverOptions? changedOptions = null;
        bool displayRequestChanged = false;
        await _receiverGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AnalogReceiverOptions before = Volatile.Read(ref _receiverOptions);
            AnalogReceiverOptions after = ValidateOptions(update(before));
            if (after == before) return;
            if (requestTuning && after.IsReceiverEnabled &&
                State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
            {
                PluginTuningResult? result = await ApplyTuningAsync(
                    after, _selectedProfileId, cancellationToken).ConfigureAwait(false);
                if (result?.Outcome == PluginTuningOutcome.Rejected) return;
            }
            Volatile.Write(ref _receiverOptions, after);
            changedOptions = after;
            displayRequestChanged = after.BandwidthHz != before.BandwidthHz;
            if (after.BandwidthHz != before.BandwidthHz ||
                after.IsAfcEnabled != before.IsAfcEnabled ||
                after.IsLowerSideband != before.IsLowerSideband ||
                after.IsSsbCarrierFrequency != before.IsSsbCarrierFrequency)
            {
                _demodulator.Reset();
            }
            if (after.IsMuted != before.IsMuted ||
                after.IsReceiverEnabled != before.IsReceiverEnabled ||
                after.IsSquelchEnabled != before.IsSquelchEnabled)
            {
                _hostContext?.Audio.Reset();
            }
        }
        finally
        {
            _receiverGate.Release();
        }
        if (changedOptions is not null)
        {
            await SaveSettingsAsync(
                _selectedProfileId, changedOptions, cancellationToken).ConfigureAwait(false);
            NotifyReceiverStateChanged(force: true);
            if (displayRequestChanged)
                WaterfallDisplayRequestChanged?.Invoke(this, EventArgs.Empty);
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

    private async ValueTask<PluginTuningResult?> ApplyTuningAsync(
        AnalogReceiverOptions options,
        string profileId,
        CancellationToken cancellationToken)
    {
        if (_hostContext is null) return null;
        if (options.FrequencyHz <= 0) return null;
        int minimumSampleRate = Math.Clamp(
            Math.Max(96_000, options.BandwidthHz * 3),
            96_000,
            2_400_000);
        bool isSsbCarrierFrequency = profileId == "ssb" && options.IsSsbCarrierFrequency;
        long preferredCenterFrequencyHz = options.FrequencyHz;
        if (isSsbCarrierFrequency)
        {
            long offsetHz = options.BandwidthHz / 2L;
            preferredCenterFrequencyHz = options.IsLowerSideband
                ? Math.Max(1L, options.FrequencyHz - offsetHz)
                : Math.Min((long)int.MaxValue, options.FrequencyHz + offsetHz);
        }
        var request = new PluginTuningRequest(
            $"analog.{profileId}",
            $"アナログ受信 {profileId.ToUpperInvariant()}",
            [new TuningTarget(options.FrequencyHz, options.BandwidthHz)],
            preferredCenterFrequencyHz,
            minimumSampleRate,
            options.StepHz,
            true,
            false,
            PluginGainPreference.Automatic,
            PreservePreferredCenterFrequency: isSsbCarrierFrequency);
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
            long targetFreqHz = result.TargetFrequencyHz > 0
                ? result.TargetFrequencyHz
                : options.IsSsbCarrierFrequency && _selectedProfileId == "ssb"
                    ? result.CenterFrequencyHz + (options.IsLowerSideband ? options.BandwidthHz / 2L : -options.BandwidthHz / 2L)
                    : result.CenterFrequencyHz;
            long roundedFreqHz = ((targetFreqHz + stepHz / 2) / stepHz) * stepHz;
            if (roundedFreqHz != options.FrequencyHz)
            {
                options = options with { FrequencyHz = roundedFreqHz };
                Volatile.Write(ref _receiverOptions, options);
                _ = SaveSettingsAsync(
                    _selectedProfileId, options, CancellationToken.None);
            }
        }
        _tuningStatus = result.Outcome == PluginTuningOutcome.Rejected
            ? $"同調できません: {result.Message}"
            : $"中心 {result.CenterFrequencyHz:N0} Hz / SR {result.SampleRateHz:N0}";
        NotifyReceiverStateChanged(force: true);
    }

    private void UpdateSignalLevel(
        float channelLevelDbfs,
        double blockDurationSeconds,
        AnalogReceiverOptions options)
    {
        if (!float.IsFinite(channelLevelDbfs)) return;
        long previousMeasurementTicks = Interlocked.Read(ref _lastInputMeasuredUtcTicks);
        float alpha = 1f - MathF.Exp(-(float)Math.Max(blockDurationSeconds, 0) / 0.25f);
        alpha = Math.Clamp(alpha, 0.01f, 1f);
        if (previousMeasurementTicks == 0)
            _signalLevelDbfs = channelLevelDbfs;
        else
            _signalLevelDbfs += alpha * (channelLevelDbfs - _signalLevelDbfs);
        Interlocked.Exchange(ref _lastInputMeasuredUtcTicks, DateTime.UtcNow.Ticks);
        float? calibratedDbm = _hostContext?.ReceiverTelemetry?.SignalLevelDbm;
        bool hasCalibration = calibratedDbm is not null && float.IsFinite(calibratedDbm.Value);
        if (hasCalibration)
        {
            // Host telemetry already integrates the tuned receive bandwidth.
            float channelLevelDbm = calibratedDbm!.Value;
            if (previousMeasurementTicks == 0 || !_hasCalibratedSignalLevelDbm)
                _signalLevelDbm = channelLevelDbm;
            else
                _signalLevelDbm += alpha * (channelLevelDbm - _signalLevelDbm);
        }
        Volatile.Write(ref _hasCalibratedSignalLevelDbm, hasCalibration);
        float signalLevelForSquelch = hasCalibration
            ? calibratedDbm!.Value
            : channelLevelDbfs;
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
                    settings.IsLowerSideband,
                    settings.IsStereoEnabled,
                    settings.IsSsbCarrierFrequency));
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

    private ValueTask SaveSettingsAsync(CancellationToken cancellationToken) =>
        SaveSettingsAsync(
            _selectedProfileId,
            Volatile.Read(ref _receiverOptions),
            cancellationToken);

    private async ValueTask SaveSettingsAsync(
        string profileId,
        AnalogReceiverOptions options,
        CancellationToken cancellationToken)
    {
        IPluginHostContext? context = _hostContext;
        if (context is null) return;
        bool gateTaken = false;
        try
        {
            await _settingsSaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateTaken = true;
            await context.Settings.SaveAsync(
                new PluginSettingsDocument(3, JsonSerializer.Serialize(new AnalogSettings(
                    profileId,
                    options.FrequencyHz,
                    options.StepHz,
                    options.BandwidthHz,
                    options.IsReceiverEnabled,
                    options.IsMuted,
                    options.IsSquelchEnabled,
                    options.SquelchThresholdDbm,
                    options.IsAfcEnabled,
                    options.IsLowerSideband,
                    options.IsStereoEnabled,
                    options.IsSsbCarrierFrequency))),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            context.Logger.Log(PluginLogLevel.Warning, "analog.settings.save-failed",
                "Analog settings could not be saved.", exception);
        }
        finally
        {
            if (gateTaken) _settingsSaveGate.Release();
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
        bool IsStereoEnabled = true,
        bool IsSsbCarrierFrequency = false,
        float? SquelchThresholdDbfs = null);

    public async ValueTask ResetSettingsAsync()
    {
        bool displayRequestChanged = false;
        await _profileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool wasStreaming = State == PluginLifecycleState.Streaming;
            if (wasStreaming) await StopStreamAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await _settingsSaveGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_hostContext is not null)
                        await _hostContext.Settings.DeleteAsync().ConfigureAwait(false);

                    await _receiverGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        int previousBandwidthHz = Volatile.Read(ref _receiverOptions).BandwidthHz;
                        _selectedProfileId = "am";
                        Volatile.Write(ref _receiverOptions,
                            ValidateOptions(new AnalogReceiverOptions()));
                        displayRequestChanged =
                            Volatile.Read(ref _receiverOptions).BandwidthHz != previousBandwidthHz;
                        ResetDemodulatorState();
                        _pretriggerBuffer.Reset();
                        _isSquelchOpen = true;
                        _hostContext?.Audio.Reset();
                    }
                    finally
                    {
                        _receiverGate.Release();
                    }
                }
                finally
                {
                    _settingsSaveGate.Release();
                }
            }
            finally
            {
                if (wasStreaming) await StartStreamAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _profileGate.Release();
        }
        NotifyReceiverStateChanged(force: true);
        SelectedProfileChanged?.Invoke(this, EventArgs.Empty);
        if (displayRequestChanged)
            WaterfallDisplayRequestChanged?.Invoke(this, EventArgs.Empty);
    }
}
