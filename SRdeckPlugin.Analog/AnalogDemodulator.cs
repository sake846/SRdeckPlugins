using SRdeckPlugin.Contracts;
using SRdeckCore.SignalProcessing;

namespace SRdeckPlugin.Analog;

/// <summary>
/// Stateful AM/FM/SSB demodulator. The input is normalized complex baseband
/// and the output is mono or stereo normalized audio at <see cref="OutputSampleRateHz"/>.
/// </summary>
internal sealed partial class AnalogDemodulator
{
    public const int OutputSampleRateHz = 48_000;

    private const float TwoPi = 2f * MathF.PI;
    private const float SsbLowFrequencyHz = 250f;

    private readonly Biquad[] _channelILowPass = [new(), new(), new(), new()];
    private readonly Biquad[] _channelQLowPass = [new(), new(), new(), new()];
    private readonly Biquad[] _audioLowPass = [new(), new(), new(), new()];
    private readonly Biquad[] _ssbILowPass = [new(), new(), new(), new()];
    private readonly Biquad[] _ssbQLowPass = [new(), new(), new(), new()];
    private readonly Biquad[] _audioDiffLowPass = [new(), new()];
    private readonly Biquad _cicCompensationI = new();
    private readonly Biquad _cicCompensationQ = new();
    // The CIC stage handles large integer decimation at O(1) cost per input.
    // The subsequent 8th-order channel filters remove its pass-band images.
    private readonly BoundedCicDecimator _inputDecimator = new();
    private readonly PolyphaseRationalResampler _audioResampler = new(tapsPerPhase: 48,
        maximumExactPhases: 256, allowUpsampling: false);

    private string _profileId = string.Empty;
    private int _inputSampleRateHz;
    private float _processingSampleRateHz;
    private int _bandwidthHz;
    private bool _isLowerSideband;
    private bool _isAfcEnabled;
    private long _frequencyOffsetHz;
    private bool _isAudioResamplerConfigured;
    private float _amCarrierLevel;
    private float _ssbSignalLevel;
    private Complex32 _fmPrevious;
    private bool _fmHasPrevious;
    private float _fmFrequencyOffset;
    private float _fmDeemphasis;
    private float _fmDeemphasisL;
    private float _fmDeemphasisR;
    private float _fmPilotPhase;
    private float _fmPilotStep;
    private float _fmPilotI;
    private float _fmPilotQ;
    private float _fmPilotLevel;
    private float _dcDiffPreviousInput;
    private float _dcDiffPreviousOutput;
    private float _ssbCosine = 1f;
    private float _ssbSine;
    private float _ssbStepCosine = 1f;
    private float _ssbStepSine;
    private int _ssbOscillatorSamples;
    private float _dcPreviousInput;
    private float _dcPreviousOutput;
    private float _tunerCosine = 1f;
    private float _tunerSine;
    private float _tunerStepCosine = 1f;
    private float _tunerStepSine;
    private int _tunerOscillatorSamples;

    public bool IsStereoDetected { get; private set; }
    public double DemodulationSampleRateHz => _processingSampleRateHz;
    public double AfcCorrectionHz => _processingSampleRateHz > 0
        ? -_fmFrequencyOffset * _processingSampleRateHz / TwoPi
        : 0;

    public int Process(
        ReadOnlySpan<Complex32> input,
        Span<float> output,
        int inputSampleRateHz,
        string profileId,
        int bandwidthHz,
        bool isLowerSideband,
        bool isAfcEnabled,
        long frequencyOffsetHz)
    {
        return Process(
            input,
            output,
            out _,
            inputSampleRateHz,
            profileId,
            bandwidthHz,
            isLowerSideband,
            isAfcEnabled,
            frequencyOffsetHz,
            isStereoEnabled: true);
    }

    public int Process(
        ReadOnlySpan<Complex32> input,
        Span<float> output,
        out int channels,
        int inputSampleRateHz,
        string profileId,
        int bandwidthHz,
        bool isLowerSideband,
        bool isAfcEnabled,
        long frequencyOffsetHz,
        bool isStereoEnabled)
    {
        if (inputSampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputSampleRateHz));
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        if (_inputSampleRateHz != inputSampleRateHz ||
            !string.Equals(_profileId, profileId, StringComparison.Ordinal) ||
            _bandwidthHz != bandwidthHz ||
            _isLowerSideband != isLowerSideband ||
            _isAfcEnabled != isAfcEnabled ||
            _frequencyOffsetHz != frequencyOffsetHz)
        {
            Configure(
                inputSampleRateHz,
                profileId,
                bandwidthHz,
                isLowerSideband,
                isAfcEnabled,
                frequencyOffsetHz);
        }

        bool isStereoCapable = profileId == "fm" && bandwidthHz > 50_000 && isStereoEnabled;
        // Keep the frame layout stable for a whole wide-FM block. Before the
        // pilot lock settles this is dual-mono; after lock it becomes stereo.
        channels = isStereoCapable ? 2 : 1;

        int outputCount = 0;
        foreach (Complex32 inputSample in input)
        {
            Complex32 tuned = MixToReceiverBaseband(inputSample);
            if (!_inputDecimator.TryProcess(tuned.I, tuned.Q, out float reducedI, out float reducedQ)) continue;
            Complex32 sample = CompensateCic(new Complex32(reducedI, reducedQ));
            sample = FilterReceiverChannel(sample);
            float demodulated = profileId switch
            {
                "am" => DemodulateAm(sample),
                "fm" => DemodulateFm(sample),
                "ssb" => DemodulateSsb(sample),
                _ => throw new ArgumentOutOfRangeException(nameof(profileId), profileId, "Unknown analog profile.")
            };

            float monoSample;
            float leftSample = 0f;
            float rightSample = 0f;

            if (isStereoCapable)
            {
                float diffSample = DemodulateFmStereoDiff(demodulated);
                monoSample = ProcessCascade(_audioLowPass, demodulated);
                monoSample = RemoveDc(monoSample);

                if (IsStereoDetected)
                {
                    leftSample = monoSample + diffSample;
                    rightSample = monoSample - diffSample;
                    channels = 2;
                }
                else
                {
                    leftSample = monoSample;
                    rightSample = monoSample;
                    channels = 2;
                }
            }
            else
            {
                monoSample = ProcessCascade(_audioLowPass, demodulated);
                monoSample = RemoveDc(monoSample);
                IsStereoDetected = false;
                channels = 1;
            }

            if (_audioResampler.TryProcess(
                channels == 2 ? leftSample : monoSample,
                channels == 2 ? rightSample : 0f,
                out float resampledLeft,
                out float resampledRight))
            {
                if (profileId == "fm" && bandwidthHz > 50_000)
                {
                    // The narrow post-CIC channel has a small, predictable
                    // wide-FM pass-band loss. Restore nominal broadcast level
                    // before the final de-emphasis and output limiter.
                    resampledLeft *= 1.15f;
                    resampledRight *= 1.15f;
                }
                if (channels == 2)
                {
                    if (outputCount + 1 >= output.Length)
                        throw new ArgumentException("The output buffer is too small.", nameof(output));
                    var (deemphL, deemphR) = ApplyBroadcastFmDeemphasisStereo(resampledLeft, resampledRight);
                    output[outputCount++] = Math.Clamp(deemphL, -1f, 1f);
                    output[outputCount++] = Math.Clamp(deemphR, -1f, 1f);
                }
                else
                {
                    if (outputCount >= output.Length)
                        throw new ArgumentException("The output buffer is too small.", nameof(output));
                    float outputSample = profileId == "fm" && bandwidthHz > 50_000
                        ? ApplyBroadcastFmDeemphasis(resampledLeft)
                        : resampledLeft;
                    output[outputCount++] = Math.Clamp(outputSample, -1f, 1f);
                }
            }
        }

        return outputCount;
    }

    public void Reset()
    {
        _amCarrierLevel = 0;
        _ssbSignalLevel = 0;
        _fmPrevious = default;
        _fmHasPrevious = false;
        _fmFrequencyOffset = 0;
        _fmDeemphasis = 0;
        _fmDeemphasisL = 0;
        _fmDeemphasisR = 0;
        _fmPilotPhase = 0;
        _fmPilotStep = 0;
        _fmPilotI = 0;
        _fmPilotQ = 0;
        _fmPilotLevel = 0;
        _dcDiffPreviousInput = 0;
        _dcDiffPreviousOutput = 0;
        IsStereoDetected = false;
        _ssbCosine = 1f;
        _ssbSine = 0;
        _ssbOscillatorSamples = 0;
        _dcPreviousInput = 0;
        _dcPreviousOutput = 0;
        _tunerCosine = 1f;
        _tunerSine = 0;
        _tunerOscillatorSamples = 0;
        _inputDecimator.Reset();
        if (_isAudioResamplerConfigured) _audioResampler.Reset();
        ResetFilters();
    }

    private void Configure(
        int inputSampleRateHz,
        string profileId,
        int bandwidthHz,
        bool isLowerSideband,
        bool isAfcEnabled,
        long frequencyOffsetHz)
    {
        _inputSampleRateHz = inputSampleRateHz;
        _profileId = profileId;
        _bandwidthHz = bandwidthHz;
        _isLowerSideband = isLowerSideband;
        _isAfcEnabled = isAfcEnabled;
        _frequencyOffsetHz = frequencyOffsetHz;
        int decimationFactor = SelectDecimationFactor(inputSampleRateHz, bandwidthHz);
        _processingSampleRateHz = inputSampleRateHz / decimationFactor;
        _inputDecimator.Configure(decimationFactor, stageCount: 5);

        float audioCutoffHz = profileId switch
        {
            "am" => Math.Clamp(bandwidthHz * 0.45f, 1_500f, 9_000f),
            "fm" => bandwidthHz > 50_000 ? 15_000f : 3_500f,
            "ssb" => Math.Clamp(bandwidthHz, 1_500f, 4_000f),
            _ => throw new ArgumentOutOfRangeException(nameof(profileId), profileId, "Unknown analog profile.")
        };
        audioCutoffHz = Math.Min(audioCutoffHz, _processingSampleRateHz * 0.20f);
        ConfigureButterworth(_audioLowPass, _processingSampleRateHz, audioCutoffHz);
        // The preceding 8th-order audio filter owns the programme bandwidth.
        // Leave a wide guard band here so the conversion FIR does not attenuate
        // the upper part of wide-FM audio before its 24 kHz Nyquist limit.
        float resamplerCutoffHz = OutputSampleRateHz * 0.475f;
        _audioResampler.Configure(
            inputSampleRateHz,
            decimationFactor,
            OutputSampleRateHz,
            resamplerCutoffHz);
        _isAudioResamplerConfigured = true;
        if (profileId == "fm" && bandwidthHz > 50_000)
        {
            ConfigureButterworth(
                _audioDiffLowPass,
                _processingSampleRateHz,
                Math.Min(15_000f, _processingSampleRateHz * 0.20f));
            _fmPilotStep = TwoPi * 19_000f / _processingSampleRateHz;
        }

        float channelCutoffHz = profileId switch
        {
            "am" => bandwidthHz * 0.5f,
            "fm" => bandwidthHz * 0.5f,
            "ssb" => bandwidthHz,
            _ => throw new ArgumentOutOfRangeException(nameof(profileId), profileId, "Unknown analog profile.")
        };
        channelCutoffHz = Math.Clamp(channelCutoffHz, 500f, _processingSampleRateHz * 0.20f);
        ConfigureCicCompensation(inputSampleRateHz, decimationFactor, channelCutoffHz);
        ConfigureButterworth(_channelILowPass, _processingSampleRateHz, channelCutoffHz);
        ConfigureButterworth(_channelQLowPass, _processingSampleRateHz, channelCutoffHz);

        float ssbHighFrequencyHz = Math.Clamp(bandwidthHz, 1_500f, 4_000f);
        float ssbHalfBandwidth = (ssbHighFrequencyHz - SsbLowFrequencyHz) / 2f;
        ConfigureButterworth(
            _ssbILowPass,
            _processingSampleRateHz,
            Math.Min(ssbHalfBandwidth, _processingSampleRateHz * 0.20f));
        ConfigureButterworth(
            _ssbQLowPass,
            _processingSampleRateHz,
            Math.Min(ssbHalfBandwidth, _processingSampleRateHz * 0.20f));
        float sidebandSign = isLowerSideband ? -1f : 1f;
        float ssbStep = sidebandSign * TwoPi * (SsbLowFrequencyHz + ssbHighFrequencyHz) *
            0.5f / _processingSampleRateHz;
        _ssbStepCosine = MathF.Cos(ssbStep);
        _ssbStepSine = MathF.Sin(ssbStep);
        double tunerStep = TwoPi * frequencyOffsetHz / inputSampleRateHz;
        _tunerStepCosine = (float)Math.Cos(tunerStep);
        _tunerStepSine = (float)Math.Sin(tunerStep);
        Reset();
    }
}
