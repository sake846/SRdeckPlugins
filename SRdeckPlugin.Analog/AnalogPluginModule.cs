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

/// <summary>
/// Analog-demodulation plugin. AM, FM, and SSB are plugin-owned operating
/// profiles exposed through the plugin's own workspace view.
/// </summary>
public sealed partial class AnalogPluginModule :
    PluginModuleBase,
    IIqBlockConsumer,
    IPluginProfileProvider,
    IPluginViewProvider,
    IPluginProcessingDiagnosticsProvider,
    IPluginProcessingWarmup
{
    private static readonly IReadOnlyList<PluginProfileDescriptor> SupportedProfiles =
    [
        new("am", "AM", "Amplitude modulation", true),
        new("fm", "FM", "Frequency modulation"),
        new("ssb", "SSB", "Single-sideband modulation")
    ];

    private IPluginHostContext? _hostContext;
    private AnalogViewModel? _viewModel;
    private string _selectedProfileId = "am";
    private AnalogReceiverOptions _receiverOptions = new();
    private readonly AnalogDemodulator _demodulator = new();
    private readonly PackedIqHistoryBuffer _pretriggerBuffer = new(3);
    private int _captureSaveInProgress;
    private IqBlockMetadata? _lastCaptureMetadata;
    private string _captureStatus = "IQ録音: 直前3秒を常時保持中";
    private Guid _streamId;
    private long _streamGeneration = -1;
    private int _inputSampleRateHz;
    private long _audioSequence;
    private float _signalLevelDbfs = -120f;
    private long _lastInputMeasuredUtcTicks;
    private long _lastAudioOutputUtcTicks;
    private float _lastAudioRms;
    private float _lastAudioPeak;
    private bool _isSquelchOpen = true;
    private long _lastUiUpdateTimestamp;
    private string _tuningStatus = "受信周波数を設定してください。";
    private readonly SemaphoreSlim _profileGate = new(1, 1);
    private readonly SemaphoreSlim _receiverGate = new(1, 1);

    public AnalogPluginModule() => RegisterStreamReset(_pretriggerBuffer.Reset);

    public string CaptureStatus => _captureStatus;
    internal IPluginRuntimeDiagnostics RuntimeDiagnostics =>
        _hostContext?.RuntimeDiagnostics ?? NullPluginRuntimeDiagnostics.Instance;
    internal event EventHandler? CaptureStatusChanged;

    public override PluginDescriptor Descriptor { get; } = new(
        "analog",
        "アナログ復調",
        "AM, FM, and SSB audio demodulation",
        new Version(1, 0),
        new Version(1, 0),
        new Version(1, 0),
        PluginCapabilities.IqConsumer | PluginCapabilities.AudioProducer |
        PluginCapabilities.MainView | PluginCapabilities.SettingsView | PluginCapabilities.Headless,
        "SRdeck",
        "GPL-3.0",
        IsEnabledByDefault: true);

    public PluginProcessingStageDefinition ProcessingStage { get; } = new(
        "周波数変換・レート変換・AM/FM/SSB復調",
        PluginComputeDevice.Cpu,
        ".NET CPU",
        "選択プロファイルの復調、スケルチ、AGC、音声フィルターと出力");
    public PluginIqPreferences IqPreferences { get; } = new(3);
    public IReadOnlyList<PluginProfileDescriptor> Profiles => SupportedProfiles;
    public string? SelectedProfileId => _selectedProfileId;
    internal event EventHandler? SelectedProfileChanged;
    internal event EventHandler? ReceiverStateChanged;

    private void EnsureViewModel()
    {
        if (_viewModel is not null) return;
        _viewModel = new AnalogViewModel(this);
    }

    public FrameworkElement CreateMainView()
    {
        EnsureViewModel();
        return new AnalogPluginView(this) { DataContext = _viewModel };
    }

    public FrameworkElement? CreateSettingsView()
    {
        EnsureViewModel();
        return new AnalogSettingsView(this) { DataContext = _viewModel };
    }

    protected override async ValueTask OnInitializeAsync(
        IPluginHostContext hostContext,
        CancellationToken cancellationToken)
    {
        _hostContext = hostContext;
        await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        hostContext.Tuning.AppliedConfigurationChanged += HandleAppliedConfigurationChanged;
    }

    protected override async ValueTask OnActivateAsync(CancellationToken cancellationToken)
    {
        AnalogReceiverOptions options = Volatile.Read(ref _receiverOptions);
        if (!options.IsReceiverEnabled || options.FrequencyHz <= 0) return;

        PluginTuningResult? result = await ApplyTuningAsync(options, cancellationToken)
            .ConfigureAwait(false);
        if (result?.Outcome == PluginTuningOutcome.Rejected)
            throw new PluginActivationRejectedException(
                $"Analog tuning was rejected: {result.Message}");
    }

    public async ValueTask SelectProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State == PluginLifecycleState.Streaming)
            throw new InvalidOperationException("The analog profile cannot change while streaming.");
        if (!SupportedProfiles.Any(profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal)))
            throw new ArgumentException($"Unknown analog profile '{profileId}'.", nameof(profileId));
        _selectedProfileId = profileId;
        _receiverOptions = ApplyProfileDefaults(_receiverOptions, profileId);
        ResetDemodulatorState();
        _pretriggerBuffer.Reset();
        await SaveSettingsAsync(cancellationToken).ConfigureAwait(false);
        SelectedProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    internal async ValueTask ChangeProfileFromViewAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        await _profileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(_selectedProfileId, profileId, StringComparison.Ordinal)) return;
            if (!SupportedProfiles.Any(profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal)))
                throw new ArgumentException($"Unknown analog profile '{profileId}'.", nameof(profileId));
            AnalogReceiverOptions proposed = ApplyProfileDefaults(
                Volatile.Read(ref _receiverOptions), profileId);
            if (State is PluginLifecycleState.Active or PluginLifecycleState.Streaming &&
                proposed.IsReceiverEnabled && proposed.FrequencyHz > 0)
            {
                PluginTuningResult? result = await ApplyTuningAsync(proposed, cancellationToken).ConfigureAwait(false);
                if (result?.Outcome == PluginTuningOutcome.Rejected)
                    throw new InvalidOperationException($"Analog profile tuning was rejected: {result.Message}");
            }
            bool wasStreaming = State == PluginLifecycleState.Streaming;
            if (wasStreaming) await StopStreamAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await SelectProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (wasStreaming) await StartStreamAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _profileGate.Release();
        }
    }

    protected override async ValueTask OnStartStreamAsync(CancellationToken cancellationToken)
    {
        await _receiverGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _audioSequence = 0;
            ResetDemodulatorState();
        }
        finally
        {
            _receiverGate.Release();
        }
    }

    protected override async ValueTask OnStopStreamAsync(CancellationToken cancellationToken)
    {
        await _receiverGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResetDemodulatorState();
        }
        finally
        {
            _receiverGate.Release();
        }
    }

    public ValueTask ConsumeAsync(IIqBlockLease block, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (State != PluginLifecycleState.Streaming || _hostContext is null)
            return ValueTask.CompletedTask;

        _receiverGate.Wait(cancellationToken);
        try
        {
            if (State != PluginLifecycleState.Streaming || _hostContext is null)
                return ValueTask.CompletedTask;

            int inputRate = block.Metadata.SampleRateHz;
            if (inputRate <= 0) return ValueTask.CompletedTask;
            AnalogReceiverOptions options = Volatile.Read(ref _receiverOptions);
            if (options.FrequencyHz <= 0)
            {
                options = options with { FrequencyHz = block.Metadata.CenterFrequencyHz };
                Volatile.Write(ref _receiverOptions, options);
                NotifyReceiverStateChanged(force: true);
            }

            UpdateSignalLevel(block.Samples.Span, options);
            if (!options.IsReceiverEnabled)
            {
                NotifyReceiverStateChanged();
                return ValueTask.CompletedTask;
            }
            _lastCaptureMetadata = block.Metadata;
            _pretriggerBuffer.Write(block.Samples.Span, inputRate);
            if (RequiresReset(block.Metadata))
            {
                _demodulator.Reset();
                _pretriggerBuffer.Reset();
                _hostContext.Audio.Reset();
            }
            _streamId = block.Metadata.StreamId;
            _streamGeneration = block.Metadata.Generation;
            _inputSampleRateHz = inputRate;

            int capacity = checked((int)Math.Ceiling(
                block.Samples.Length * (double)AnalogDemodulator.OutputSampleRateHz / inputRate) + 2) * 2;
            float[] audio = ArrayPool<float>.Shared.Rent(capacity);
            try
            {
                int outputCount = _demodulator.Process(
                    block.Samples.Span,
                    audio.AsSpan(0, capacity),
                    out int channels,
                    inputRate,
                    _selectedProfileId,
                    options.BandwidthHz,
                    options.IsLowerSideband,
                    options.IsAfcEnabled,
                    options.FrequencyHz - block.Metadata.CenterFrequencyHz,
                    options.IsStereoEnabled);
                byte[] pcm = new byte[outputCount * sizeof(short)];
                double audioPower = 0;
                float audioPeak = 0;
                for (int index = 0; index < outputCount; index++)
                {
                    float sample = audio[index];
                    audioPower += sample * sample;
                    audioPeak = Math.Max(audioPeak, Math.Abs(sample));
                    short value = (short)MathF.Round(audio[index] * short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(
                        pcm.AsSpan(index * sizeof(short), sizeof(short)),
                        value);
                }
                Volatile.Write(ref _lastAudioRms, outputCount > 0 ? (float)Math.Sqrt(audioPower / outputCount) : 0);
                Volatile.Write(ref _lastAudioPeak, audioPeak);

                if (outputCount > 0 && !options.IsMuted && _isSquelchOpen)
                {
                    _hostContext.Audio.TrySubmit(new PcmAudioFrame(
                        _hostContext.PluginId,
                        block.Metadata.StreamId,
                        Interlocked.Increment(ref _audioSequence),
                        AnalogDemodulator.OutputSampleRateHz,
                        channels,
                        PcmSampleFormat.Signed16LittleEndian,
                        pcm,
                        block.Metadata.Discontinuity != IqDiscontinuity.None));
                    Interlocked.Exchange(ref _lastAudioOutputUtcTicks, DateTime.UtcNow.Ticks);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(audio);
            }
            NotifyReceiverStateChanged();
        }
        finally
        {
            _receiverGate.Release();
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask WarmUpProcessingAsync(
        PluginProcessingWarmupContext context,
        CancellationToken cancellationToken)
    {
        int blockCount = Math.Clamp(context.BlockCount, 1, 8);
        AnalogReceiverOptions options = Volatile.Read(ref _receiverOptions);
        int inputSampleRateHz = Math.Max(
            Math.Max(96_000, options.BandwidthHz * 3),
            context.SampleRateHz);
        int inputSampleCount = inputSampleRateHz / 10;
        int outputCapacity = checked((int)Math.Ceiling(
            inputSampleCount * (double)AnalogDemodulator.OutputSampleRateHz /
            inputSampleRateHz) + 2) * 2;
        return new ValueTask(Task.Run(() =>
        {
            Complex32[] samples = ArrayPool<Complex32>.Shared.Rent(inputSampleCount);
            float[] audio = ArrayPool<float>.Shared.Rent(outputCapacity);
            bool gateTaken = false;
            try
            {
                samples.AsSpan(0, inputSampleCount).Clear();
                _receiverGate.Wait(cancellationToken);
                gateTaken = true;
                for (int block = 0; block < blockCount; block++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _demodulator.Process(
                        samples.AsSpan(0, inputSampleCount),
                        audio.AsSpan(0, outputCapacity),
                        out _,
                        inputSampleRateHz,
                        _selectedProfileId,
                        options.BandwidthHz,
                        options.IsLowerSideband,
                        options.IsAfcEnabled,
                        0,
                        options.IsStereoEnabled);
                }
            }
            finally
            {
                if (gateTaken)
                {
                    _demodulator.Reset();
                    _receiverGate.Release();
                }
                ArrayPool<float>.Shared.Return(audio);
                ArrayPool<Complex32>.Shared.Return(samples);
            }
        }, cancellationToken));
    }

    protected override ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        if (_hostContext is not null)
        {
            _hostContext.Tuning.AppliedConfigurationChanged -= HandleAppliedConfigurationChanged;
        }
        _hostContext = null;
        return ValueTask.CompletedTask;
    }

    private bool RequiresReset(IqBlockMetadata metadata) =>
        _streamId != metadata.StreamId ||
        _streamGeneration != metadata.Generation ||
        _inputSampleRateHz != metadata.SampleRateHz ||
        metadata.Discontinuity != IqDiscontinuity.None;

    private void ResetDemodulatorState()
    {
        _demodulator.Reset();
        _streamId = Guid.Empty;
        _streamGeneration = -1;
        _inputSampleRateHz = 0;
    }
}
