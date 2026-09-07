using System.Buffers;
using SRdeckCore.SignalProcessing;
using SRdeckPlugin.Contracts;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Dsp;

/// <summary>
/// Produces an audible representation of one selected LoRa channel. Channel
/// extraction remains linear IQ processing; FM phase-difference detection is
/// applied only to the monitor path and does not feed the packet decoder.
/// </summary>
internal sealed class MeshtasticFmMonitor
{
    public const int OutputSampleRateHz = 48_000;

    private const float AudioGain = 0.8f;
    private const float AudioHighPassHz = 20f;
    private const float AudioLowPassHz = 12_000f;
    private const float SquelchOpenPowerRatio = 2.5f;
    private const float SquelchKeepOpenPowerRatio = 1.6f;
    private const float SquelchMinimumExcessPower = 1e-5f;
    private const float SquelchOpenNoiseTrackingFactor = 0.000002f;
    private const float SquelchReleaseSeconds = 0.15f;
    private const float SquelchFadeSeconds = 0.005f;

    public bool IsSquelchEnabled { get; set; } = true;
    internal bool IsSquelchOpen => !IsSquelchEnabled || _isSquelchOpen;

    private readonly LoRaChannelizer _channelizer = new();
    private readonly PolyphaseRationalResampler _audioResampler = new(
        tapsPerPhase: 48,
        maximumExactPhases: 256,
        allowUpsampling: false);
    private int _inputSampleRateHz;
    private int _bandwidthHz;
    private long _frequencyOffsetHz;
    private bool _isConfigured;
    private bool _hasPrevious;
    private float _previousI;
    private float _previousQ;
    private float _dcPreviousInput;
    private float _dcPreviousOutput;
    private float _dcPole;
    private float _smoothPower;
    private float _noiseFloorPower = 1e-4f;
    private int _squelchHoldSamples;
    private bool _isSquelchOpen;
    private float _squelchGain;
    private float[]? _activeOutput;
    private int _activeOutputCount;

    public int Process(
        ReadOnlySpan<Complex32> input,
        float[] output,
        int inputSampleRateHz,
        int bandwidthHz,
        long frequencyOffsetHz,
        out bool configurationChanged)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (inputSampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(inputSampleRateHz));
        if (bandwidthHz <= 0) throw new ArgumentOutOfRangeException(nameof(bandwidthHz));

        configurationChanged = Configure(inputSampleRateHz, bandwidthHz, frequencyOffsetHz);
        if (input.IsEmpty) return 0;

        short[] samplesI = ArrayPool<short>.Shared.Rent(input.Length);
        short[] samplesQ = ArrayPool<short>.Shared.Rent(input.Length);
        try
        {
            for (int index = 0; index < input.Length; index++)
            {
                samplesI[index] = NormalizeToInt16(input[index].I);
                samplesQ[index] = NormalizeToInt16(input[index].Q);
            }

            _activeOutput = output;
            _activeOutputCount = 0;
            _channelizer.ProcessForTest(
                samplesI.AsSpan(0, input.Length),
                samplesQ.AsSpan(0, input.Length),
                ProcessChannelSample);
            return _activeOutputCount;
        }
        finally
        {
            _activeOutput = null;
            ArrayPool<short>.Shared.Return(samplesI);
            ArrayPool<short>.Shared.Return(samplesQ);
        }
    }

    public void Reset()
    {
        _channelizer.Reset();
        if (_isConfigured) _audioResampler.Reset();
        ResetDemodulator();
    }

    private bool Configure(int inputSampleRateHz, int bandwidthHz, long frequencyOffsetHz)
    {
        if (_isConfigured &&
            _inputSampleRateHz == inputSampleRateHz &&
            _bandwidthHz == bandwidthHz &&
            _frequencyOffsetHz == frequencyOffsetHz)
        {
            return false;
        }

        _inputSampleRateHz = inputSampleRateHz;
        _bandwidthHz = bandwidthHz;
        _frequencyOffsetHz = frequencyOffsetHz;
        _channelizer.Configure(inputSampleRateHz, frequencyOffsetHz, bandwidthHz);
        _audioResampler.Configure(
            MeshtasticJpLongFastProfile.DecoderSampleRateHz,
            1,
            OutputSampleRateHz,
            AudioLowPassHz);
        _dcPole = MathF.Exp(
            -2f * MathF.PI * AudioHighPassHz /
            MeshtasticJpLongFastProfile.DecoderSampleRateHz);
        _isConfigured = true;
        ResetDemodulator();
        return true;
    }

    private void ProcessChannelSample(float sampleI, float sampleQ)
    {
        float power = (sampleI * sampleI) + (sampleQ * sampleQ);
        _smoothPower += 0.01f * (power - _smoothPower);
        // Avoid teaching a packet to the noise estimator. A very slow update while
        // open still lets the gate recover from a persistent SDR gain/noise step.
        if (!_isSquelchOpen && _smoothPower < _noiseFloorPower)
        {
            _noiseFloorPower += 0.001f * (_smoothPower - _noiseFloorPower);
        }
        else if (!_isSquelchOpen)
        {
            _noiseFloorPower += 0.00005f * (_smoothPower - _noiseFloorPower);
        }
        else
        {
            _noiseFloorPower += SquelchOpenNoiseTrackingFactor *
                                (_smoothPower - _noiseFloorPower);
        }

        float requiredRatio = _isSquelchOpen
            ? SquelchKeepOpenPowerRatio
            : SquelchOpenPowerRatio;
        if (_smoothPower > _noiseFloorPower * requiredRatio + SquelchMinimumExcessPower)
        {
            _isSquelchOpen = true;
            _squelchHoldSamples = checked((int)(
                MeshtasticJpLongFastProfile.DecoderSampleRateHz * SquelchReleaseSeconds));
        }
        else if (_squelchHoldSamples > 0)
        {
            _squelchHoldSamples--;
        }
        else
        {
            _isSquelchOpen = false;
        }

        if (!_hasPrevious)
        {
            _previousI = sampleI;
            _previousQ = sampleQ;
            _hasPrevious = true;
            return;
        }

        float dot = (sampleI * _previousI) + (sampleQ * _previousQ);
        float cross = (sampleQ * _previousI) - (sampleI * _previousQ);
        _previousI = sampleI;
        _previousQ = sampleQ;

        // Convert phase difference to instantaneous frequency and normalize the
        // selected LoRa bandwidth's +/-BW/2 sweep to approximately +/-1.
        float phase = MathF.Atan2(cross, dot);
        float demodulated = phase * MeshtasticJpLongFastProfile.DecoderSampleRateHz /
                            (MathF.PI * _bandwidthHz);
        float dcRemoved = demodulated - _dcPreviousInput + (_dcPole * _dcPreviousOutput);
        _dcPreviousInput = demodulated;
        _dcPreviousOutput = dcRemoved;

        if (!_audioResampler.TryProcess(dcRemoved, 0f, out float audio, out _)) return;
        float[] output = _activeOutput ??
            throw new InvalidOperationException("The FM monitor output buffer is not active.");
        if (_activeOutputCount >= output.Length)
            throw new ArgumentException("The FM monitor output buffer is too small.", nameof(output));

        float targetGain = !IsSquelchEnabled || _isSquelchOpen ? 1f : 0f;
        float fadeStep = 1f / Math.Max(1f, OutputSampleRateHz * SquelchFadeSeconds);
        _squelchGain = targetGain > _squelchGain
            ? Math.Min(targetGain, _squelchGain + fadeStep)
            : Math.Max(targetGain, _squelchGain - fadeStep);

        output[_activeOutputCount++] = Math.Clamp(audio * AudioGain * _squelchGain, -1f, 1f);
    }

    private void ResetDemodulator()
    {
        _hasPrevious = false;
        _previousI = 0;
        _previousQ = 0;
        _dcPreviousInput = 0;
        _dcPreviousOutput = 0;
        _smoothPower = 0f;
        _noiseFloorPower = 1e-4f;
        _squelchHoldSamples = 0;
        _isSquelchOpen = false;
        _squelchGain = 0f;
        _activeOutput = null;
        _activeOutputCount = 0;
    }

    private static short NormalizeToInt16(float value) =>
        (short)Math.Clamp(
            (int)MathF.Round(value * 32768f),
            short.MinValue,
            short.MaxValue);
}
