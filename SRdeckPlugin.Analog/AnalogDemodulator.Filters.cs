using SRdeckPlugin.Contracts;
using SRdeckCore.SignalProcessing;

namespace SRdeckPlugin.Analog;

internal sealed partial class AnalogDemodulator
{
    private float DemodulateFmStereoDiff(float mpx)
    {
        float baseStep = TwoPi * 19_000f / _processingSampleRateHz;
        _fmPilotPhase += _fmPilotStep != 0 ? _fmPilotStep : baseStep;
        if (_fmPilotPhase >= TwoPi) _fmPilotPhase -= TwoPi;

        float cos19 = MathF.Cos(_fmPilotPhase);
        float sin19 = MathF.Sin(_fmPilotPhase);

        float i19 = mpx * cos19;
        float q19 = -mpx * sin19;

        float alpha = 1f - MathF.Exp(-TwoPi * 30f / _processingSampleRateHz);
        _fmPilotI += alpha * (i19 - _fmPilotI);
        _fmPilotQ += alpha * (q19 - _fmPilotQ);

        float magnitude = MathF.Sqrt(_fmPilotI * _fmPilotI + _fmPilotQ * _fmPilotQ);
        _fmPilotLevel += alpha * (magnitude - _fmPilotLevel);

        float threshold = IsStereoDetected ? 0.018f : 0.025f;
        IsStereoDetected = _fmPilotLevel > threshold;

        if (IsStereoDetected)
        {
            float error = MathF.Atan2(_fmPilotQ, _fmPilotI);
            _fmPilotStep = baseStep + error * 0.01f;
        }
        else
        {
            _fmPilotStep = baseStep;
        }

        float sin38 = 2f * sin19 * cos19;
        float rawDiff = -mpx * sin38 * 2.0f;

        float filteredDiff = ProcessCascade(_audioDiffLowPass, rawDiff);
        return RemoveDcDiff(filteredDiff);
    }

    private float RemoveDcDiff(float input)
    {
        float pole = MathF.Exp(-TwoPi * 30f / _processingSampleRateHz);
        float output = input - _dcDiffPreviousInput + pole * _dcDiffPreviousOutput;
        _dcDiffPreviousInput = input;
        _dcDiffPreviousOutput = output;
        return output;
    }

    private (float Left, float Right) ApplyBroadcastFmDeemphasisStereo(float leftInput, float rightInput)
    {
        const float timeConstantSeconds = 50e-6f;
        float alpha = 1f - MathF.Exp(-1f / (OutputSampleRateHz * timeConstantSeconds));
        _fmDeemphasisL += alpha * (leftInput - _fmDeemphasisL);
        _fmDeemphasisR += alpha * (rightInput - _fmDeemphasisR);
        return (_fmDeemphasisL, _fmDeemphasisR);
    }

    private float DemodulateAm(Complex32 sample)
    {
        float magnitude = MathF.Sqrt(sample.I * sample.I + sample.Q * sample.Q);
        float alpha = 1f - MathF.Exp(-TwoPi * 20f / _processingSampleRateHz);
        if (_amCarrierLevel == 0) _amCarrierLevel = magnitude;
        _amCarrierLevel += alpha * (magnitude - _amCarrierLevel);
        float normalizationGain = MathF.Min(32f, 1f / MathF.Max(_amCarrierLevel, 1e-6f));
        return (magnitude - _amCarrierLevel) * normalizationGain * 1.5f;
    }

    private float DemodulateFm(Complex32 sample)
    {
        if (!_fmHasPrevious)
        {
            _fmPrevious = sample;
            _fmHasPrevious = true;
            return 0;
        }

        float cross = sample.Q * _fmPrevious.I - sample.I * _fmPrevious.Q;
        float dot = sample.I * _fmPrevious.I + sample.Q * _fmPrevious.Q;
        _fmPrevious = sample;
        float deviationHz = _bandwidthHz > 50_000 ? 75_000f : 5_000f;
        float discriminator = MathF.Atan2(cross, dot) * _processingSampleRateHz / (TwoPi * deviationHz);

        float afcAlpha = 1f - MathF.Exp(-TwoPi * 2f / _processingSampleRateHz);
        if (_isAfcEnabled)
        {
            _fmFrequencyOffset += afcAlpha * (discriminator - _fmFrequencyOffset);
            discriminator -= _fmFrequencyOffset;
        }
        return discriminator;
    }

    private float DemodulateSsb(Complex32 sample)
    {
        float magnitude = MathF.Sqrt(sample.I * sample.I + sample.Q * sample.Q);
        float levelAlpha = 1f - MathF.Exp(-TwoPi * 20f / _processingSampleRateHz);
        if (_ssbSignalLevel == 0) _ssbSignalLevel = magnitude;
        _ssbSignalLevel += levelAlpha * (magnitude - _ssbSignalLevel);

        float cosine = _ssbCosine;
        float sine = _ssbSine;

        float downI = sample.I * cosine + sample.Q * sine;
        float downQ = sample.Q * cosine - sample.I * sine;
        downI = ProcessCascade(_ssbILowPass, downI);
        downQ = ProcessCascade(_ssbQLowPass, downQ);

        float audio = downI * cosine - downQ * sine;
        _ssbCosine = cosine * _ssbStepCosine - sine * _ssbStepSine;
        _ssbSine = sine * _ssbStepCosine + cosine * _ssbStepSine;
        if ((++_ssbOscillatorSamples & 4095) == 0)
        {
            float inverseMagnitude = 1f / MathF.Sqrt(
                _ssbCosine * _ssbCosine + _ssbSine * _ssbSine);
            _ssbCosine *= inverseMagnitude;
            _ssbSine *= inverseMagnitude;
        }
        float normalizationGain = MathF.Min(32f, 1f / MathF.Max(_ssbSignalLevel, 1e-6f));
        return audio * normalizationGain * 1.5f;
    }

    private Complex32 FilterReceiverChannel(Complex32 sample) => new(
        ProcessCascade(_channelILowPass, sample.I),
        ProcessCascade(_channelQLowPass, sample.Q));

    private Complex32 CompensateCic(Complex32 sample) => new(
        _cicCompensationI.Process(sample.I),
        _cicCompensationQ.Process(sample.Q));

    private void ConfigureCicCompensation(
        int inputSampleRateHz,
        int decimationFactor,
        float passbandEdgeHz)
    {
        if (decimationFactor <= 1)
        {
            _cicCompensationI.ConfigureIdentity();
            _cicCompensationQ.ConfigureIdentity();
            return;
        }

        const int cicStages = 5;
        double numerator = Math.Sin(Math.PI * passbandEdgeHz / _processingSampleRateHz);
        double denominator = decimationFactor * Math.Sin(Math.PI * passbandEdgeHz / inputSampleRateHz);
        double cicMagnitude = Math.Pow(Math.Abs(numerator / denominator), cicStages);
        float edgeGain = (float)Math.Clamp(1d / Math.Max(cicMagnitude, 1e-6), 1d, 2d);

        // An RBJ high-shelf has the square root of its endpoint gain at its
        // corner frequency. Squaring here restores the CIC loss at the channel
        // edge while the following 8th-order low-pass rejects the boosted stopband.
        float shelfGainDb = 40f * MathF.Log10(edgeGain);
        _cicCompensationI.ConfigureHighShelf(_processingSampleRateHz, passbandEdgeHz, shelfGainDb);
        _cicCompensationQ.ConfigureHighShelf(_processingSampleRateHz, passbandEdgeHz, shelfGainDb);
    }

    private static float ProcessCascade(Biquad[] sections, float input)
    {
        foreach (Biquad section in sections) input = section.Process(input);
        return input;
    }

    private float ApplyBroadcastFmDeemphasis(float input)
    {
        const float timeConstantSeconds = 50e-6f;
        float alpha = 1f - MathF.Exp(-1f / (OutputSampleRateHz * timeConstantSeconds));
        _fmDeemphasis += alpha * (input - _fmDeemphasis);
        return _fmDeemphasis;
    }

    private Complex32 MixToReceiverBaseband(Complex32 sample)
    {
        if (_frequencyOffsetHz == 0) return sample;

        float cosine = _tunerCosine;
        float sine = _tunerSine;
        var mixed = new Complex32(
            sample.I * cosine + sample.Q * sine,
            sample.Q * cosine - sample.I * sine);
        _tunerCosine = cosine * _tunerStepCosine - sine * _tunerStepSine;
        _tunerSine = sine * _tunerStepCosine + cosine * _tunerStepSine;
        if ((++_tunerOscillatorSamples & 4095) == 0)
        {
            float inverseMagnitude = 1f / MathF.Sqrt(
                _tunerCosine * _tunerCosine + _tunerSine * _tunerSine);
            _tunerCosine *= inverseMagnitude;
            _tunerSine *= inverseMagnitude;
        }
        return mixed;
    }

    private float RemoveDc(float input)
    {
        float pole = MathF.Exp(-TwoPi * 30f / _processingSampleRateHz);
        float output = input - _dcPreviousInput + pole * _dcPreviousOutput;
        _dcPreviousInput = input;
        _dcPreviousOutput = output;
        return output;
    }

    private static int SelectDecimationFactor(int inputSampleRateHz, int bandwidthHz)
    {
        int targetRateHz = Math.Max(96_000, checked(bandwidthHz * 4));
        int maximumFactor = Math.Max(1, inputSampleRateHz / targetRateHz);
        // Keep the intermediate rate integral so that the streaming rational
        // audio resampler has an exact clock and cannot drift over long runs.
        for (int factor = maximumFactor; factor > 1; factor--)
            if (inputSampleRateHz % factor == 0) return factor;
        return 1;
    }

    private static void ConfigureButterworth(Biquad[] sections, float sampleRateHz, float cutoffHz)
    {
        ReadOnlySpan<float> qValues = sections.Length switch
        {
            2 => [0.5411961f, 1.306563f],
            4 => [0.5097956f, 0.6013449f, 0.8999762f, 2.5629154f],
            _ => throw new ArgumentException("Only fourth- and eighth-order filters are supported.", nameof(sections))
        };
        for (int index = 0; index < sections.Length; index++)
            sections[index].ConfigureLowPass(sampleRateHz, cutoffHz, qValues[index]);
    }

    private void ResetFilters()
    {
        foreach (Biquad filter in _channelILowPass) filter.Reset();
        foreach (Biquad filter in _channelQLowPass) filter.Reset();
        foreach (Biquad filter in _audioLowPass) filter.Reset();
        foreach (Biquad filter in _ssbILowPass) filter.Reset();
        foreach (Biquad filter in _ssbQLowPass) filter.Reset();
        foreach (Biquad filter in _audioDiffLowPass) filter.Reset();
        _cicCompensationI.Reset();
        _cicCompensationQ.Reset();
    }

    private sealed class Biquad
    {
        private float _b0;
        private float _b1;
        private float _b2;
        private float _a1;
        private float _a2;
        private float _z1;
        private float _z2;

        public void ConfigureLowPass(float sampleRateHz, float cutoffHz, float q)
        {
            cutoffHz = Math.Clamp(cutoffHz, 1f, sampleRateHz * 0.45f);
            float omega = TwoPi * cutoffHz / sampleRateHz;
            float cosine = MathF.Cos(omega);
            float alpha = MathF.Sin(omega) / (2f * q);
            float inverseA0 = 1f / (1f + alpha);
            _b0 = (1f - cosine) * 0.5f * inverseA0;
            _b1 = (1f - cosine) * inverseA0;
            _b2 = _b0;
            _a1 = -2f * cosine * inverseA0;
            _a2 = (1f - alpha) * inverseA0;
            Reset();
        }

        public void ConfigureHighShelf(float sampleRateHz, float cornerHz, float gainDb)
        {
            cornerHz = Math.Clamp(cornerHz, 1f, sampleRateHz * 0.45f);
            float amplitude = MathF.Pow(10f, gainDb / 40f);
            float omega = TwoPi * cornerHz / sampleRateHz;
            float sine = MathF.Sin(omega);
            float cosine = MathF.Cos(omega);
            float alpha = sine * 0.5f * MathF.Sqrt(2f);
            float beta = 2f * MathF.Sqrt(amplitude) * alpha;
            float inverseA0 = 1f / ((amplitude + 1f) - (amplitude - 1f) * cosine + beta);

            _b0 = amplitude * ((amplitude + 1f) + (amplitude - 1f) * cosine + beta) * inverseA0;
            _b1 = -2f * amplitude * ((amplitude - 1f) + (amplitude + 1f) * cosine) * inverseA0;
            _b2 = amplitude * ((amplitude + 1f) + (amplitude - 1f) * cosine - beta) * inverseA0;
            _a1 = 2f * ((amplitude - 1f) - (amplitude + 1f) * cosine) * inverseA0;
            _a2 = ((amplitude + 1f) - (amplitude - 1f) * cosine - beta) * inverseA0;
            Reset();
        }

        public void ConfigureIdentity()
        {
            _b0 = 1f;
            _b1 = _b2 = _a1 = _a2 = 0f;
            Reset();
        }

        public float Process(float input)
        {
            float output = _b0 * input + _z1;
            _z1 = _b1 * input - _a1 * output + _z2;
            _z2 = _b2 * input - _a2 * output;
            return output;
        }

        public void Reset() => _z1 = _z2 = 0;
    }

}
