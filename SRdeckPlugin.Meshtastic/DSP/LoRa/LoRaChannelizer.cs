using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

// Owned by the standalone Meshtastic plugin assembly.
namespace SRdeckPlugin.Meshtastic.Dsp;

internal readonly record struct LoRaChannelizerTiming(long ChannelizationTicks, long DetectionTicks);

/// <summary>
/// Stateful multistage DDC for one LoRa channel. The high-rate input is mixed
/// and cheaply reduced to at most 1 MS/s before the selective FIR runs, then
/// converted to the fixed decoder rate. This keeps adjacent slots separated
/// without applying every FIR tap at the SDR input rate.
/// </summary>
internal sealed class LoRaChannelizer
{
    private const int TapCount = 47;
    private const int MaximumIntermediateRateHz = 1_000_000;

    internal static int CalculateIntermediateSampleRateHz(int inputSampleRateHz)
    {
        if (inputSampleRateHz <= 0) return 0;
        int decimation = Math.Max(1, inputSampleRateHz / MaximumIntermediateRateHz);
        return inputSampleRateHz / decimation;
    }
    private readonly float[] _delayI = new float[TapCount * 2];
    private readonly float[] _delayQ = new float[TapCount * 2];
    private readonly float[] _taps = new float[TapCount];
    private Vector<float>[] _tapVectors = [];
    private float[] _outputI = [];
    private float[] _outputQ = [];
    private int _delayIndex;
    private int _inputSampleRateHz;
    private double _frequencyOffsetHz;
    private int _bandwidthHz;
    private double _oscillatorI = 1.0;
    private double _oscillatorQ;
    private double _stepI = 1.0;
    private double _stepQ;
    private int _intermediateRateHz;
    private int _stageOneDecimation;
    private double _cicOutputScale = 1.0 / 32768.0;
    private int _stageOneCount;
    private long _cicIntegrator1I, _cicIntegrator1Q;
    private long _cicIntegrator2I, _cicIntegrator2Q;
    private long _cicIntegrator3I, _cicIntegrator3Q;
    private long _cicCombDelay1I, _cicCombDelay1Q;
    private long _cicCombDelay2I, _cicCombDelay2Q;
    private long _cicCombDelay3I, _cicCombDelay3Q;
    private long _outputAccumulator;
    private int _normalizationCounter;

    public void Configure(int inputSampleRateHz, double frequencyOffsetHz, int bandwidthHz)
    {
        if (inputSampleRateHz == _inputSampleRateHz && bandwidthHz == _bandwidthHz && Math.Abs(frequencyOffsetHz - _frequencyOffsetHz) < 0.5) return;

        _inputSampleRateHz = inputSampleRateHz;
        _frequencyOffsetHz = frequencyOffsetHz;
        _bandwidthHz = bandwidthHz;
        _stageOneDecimation = Math.Max(1, inputSampleRateHz / MaximumIntermediateRateHz);
        _cicOutputScale = 1.0 / (32768.0 * _stageOneDecimation * _stageOneDecimation * _stageOneDecimation);
        _intermediateRateHz = inputSampleRateHz / _stageOneDecimation;
        double step = -2.0 * Math.PI * frequencyOffsetHz / inputSampleRateHz;
        _stepI = Math.Cos(step);
        _stepQ = Math.Sin(step);
        DesignLowPass(_intermediateRateHz);
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_delayI);
        Array.Clear(_delayQ);
        _delayIndex = 0;
        _oscillatorI = 1.0;
        _oscillatorQ = 0.0;
        _stageOneCount = 0;
        _cicIntegrator1I = _cicIntegrator1Q = 0;
        _cicIntegrator2I = _cicIntegrator2Q = 0;
        _cicIntegrator3I = _cicIntegrator3Q = 0;
        _cicCombDelay1I = _cicCombDelay1Q = 0;
        _cicCombDelay2I = _cicCombDelay2Q = 0;
        _cicCombDelay3I = _cicCombDelay3Q = 0;
        _outputAccumulator = 0;
        _normalizationCounter = 0;
    }

    public LoRaChannelizerTiming Process(ReadOnlySpan<short> inputI, ReadOnlySpan<short> inputQ, IReadOnlyList<LoRaPreambleDetector> detectors)
        => ProcessCore(inputI, inputQ, detectors, null);

    internal LoRaChannelizerTiming ProcessForTest(ReadOnlySpan<short> inputI, ReadOnlySpan<short> inputQ, Action<float, float> output)
        => ProcessCore(inputI, inputQ, null, output);

    private LoRaChannelizerTiming ProcessCore(ReadOnlySpan<short> inputI, ReadOnlySpan<short> inputQ,
        IReadOnlyList<LoRaPreambleDetector>? detectors, Action<float, float>? output)
    {
        long channelizationStarted = Stopwatch.GetTimestamp();
        int count = Math.Min(inputI.Length, inputQ.Length);
        int outputCount = 0;
        if (detectors is not null)
            EnsureOutputCapacity((int)Math.Ceiling((double)count * MeshtasticJpLongFastProfile.DecoderSampleRateHz /
                                                   (double)Math.Max(1, _inputSampleRateHz)) + 2);
        for (int n = 0; n < count; n++)
        {
            double sampleI = inputI[n] * (1.0 / 32768.0);
            double sampleQ = inputQ[n] * (1.0 / 32768.0);
            float mixedI = (float)((sampleI * _oscillatorI) - (sampleQ * _oscillatorQ));
            float mixedQ = (float)((sampleI * _oscillatorQ) + (sampleQ * _oscillatorI));

            double nextI = (_oscillatorI * _stepI) - (_oscillatorQ * _stepQ);
            _oscillatorQ = (_oscillatorI * _stepQ) + (_oscillatorQ * _stepI);
            _oscillatorI = nextI;
            if (++_normalizationCounter == 4096)
            {
                double magnitude = Math.Sqrt((_oscillatorI * _oscillatorI) + (_oscillatorQ * _oscillatorQ));
                if (magnitude > 0.0)
                {
                    _oscillatorI /= magnitude;
                    _oscillatorQ /= magnitude;
                }
                _normalizationCounter = 0;
            }

            // A CIC integrator may wrap indefinitely. Two's-complement modular
            // arithmetic preserves the final comb result as long as that result
            // fits in Int64, which it does for 16-bit IQ and this decimation.
            long fixedI = (long)(mixedI * 32768.0f);
            long fixedQ = (long)(mixedQ * 32768.0f);
            unchecked
            {
                _cicIntegrator1I += fixedI;
                _cicIntegrator1Q += fixedQ;
                _cicIntegrator2I += _cicIntegrator1I;
                _cicIntegrator2Q += _cicIntegrator1Q;
                _cicIntegrator3I += _cicIntegrator2I;
                _cicIntegrator3Q += _cicIntegrator2Q;
            }
            if (++_stageOneCount < _stageOneDecimation) continue;
            _stageOneCount = 0;
            long comb1I = unchecked(_cicIntegrator3I - _cicCombDelay1I);
            long comb1Q = unchecked(_cicIntegrator3Q - _cicCombDelay1Q);
            _cicCombDelay1I = _cicIntegrator3I;
            _cicCombDelay1Q = _cicIntegrator3Q;
            long comb2I = unchecked(comb1I - _cicCombDelay2I);
            long comb2Q = unchecked(comb1Q - _cicCombDelay2Q);
            _cicCombDelay2I = comb1I;
            _cicCombDelay2Q = comb1Q;
            long comb3I = unchecked(comb2I - _cicCombDelay3I);
            long comb3Q = unchecked(comb2Q - _cicCombDelay3Q);
            _cicCombDelay3I = comb2I;
            _cicCombDelay3Q = comb2Q;
            float intermediateI = (float)(comb3I * _cicOutputScale);
            float intermediateQ = (float)(comb3Q * _cicOutputScale);

            _delayI[_delayIndex] = intermediateI;
            _delayQ[_delayIndex] = intermediateQ;
            _delayI[_delayIndex + TapCount] = intermediateI;
            _delayQ[_delayIndex + TapCount] = intermediateQ;
            _delayIndex++;
            if (_delayIndex == TapCount) _delayIndex = 0;

            _outputAccumulator += MeshtasticJpLongFastProfile.DecoderSampleRateHz;
            if (_outputAccumulator < _intermediateRateHz) continue;
            _outputAccumulator -= _intermediateRateHz;

            float outputI = DotFir(_delayI, _delayIndex);
            float outputQ = DotFir(_delayQ, _delayIndex);

            if (detectors is not null)
            {
                _outputI[outputCount] = outputI;
                _outputQ[outputCount] = outputQ;
                outputCount++;
            }
            output?.Invoke(outputI, outputQ);
        }
        long channelizationTicks = Stopwatch.GetTimestamp() - channelizationStarted;
        long detectionStarted = Stopwatch.GetTimestamp();
        if (detectors is not null && outputCount > 0)
            for (int detectorIndex = 0; detectorIndex < detectors.Count; detectorIndex++)
                detectors[detectorIndex].PushBlock(
                    _outputI.AsSpan(0, outputCount), _outputQ.AsSpan(0, outputCount));
        return new LoRaChannelizerTiming(
            channelizationTicks,
            Stopwatch.GetTimestamp() - detectionStarted);
    }

    private void EnsureOutputCapacity(int required)
    {
        if (_outputI.Length >= required) return;
        int capacity = Math.Max(required, Math.Max(1024, _outputI.Length * 2));
        _outputI = new float[capacity];
        _outputQ = new float[capacity];
    }

    private float DotFir(float[] delay, int start)
    {
        int vectorWidth = Vector<float>.Count;
        int vectorCount = _tapVectors.Length;
        float sum = 0;
        for (int vectorIndex = 0; vectorIndex < vectorCount; vectorIndex++)
            sum += Vector.Dot(new Vector<float>(delay, start + vectorIndex * vectorWidth), _tapVectors[vectorIndex]);
        for (int tap = vectorCount * vectorWidth; tap < TapCount; tap++)
            sum += delay[start + tap] * _taps[tap];
        return sum;
    }

    private void DesignLowPass(int sampleRateHz)
    {
        // Preserve the LoRa occupied band while rejecting the adjacent slot
        // before the final conversion to 500 kS/s.
        double cutoffHz = _bandwidthHz * 0.52;
        double normalizedCutoff = cutoffHz / sampleRateHz;
        int midpoint = (TapCount - 1) / 2;
        double sum = 0.0;
        for (int i = 0; i < TapCount; i++)
        {
            int m = i - midpoint;
            double sinc = m == 0
                ? 2.0 * normalizedCutoff
                : Math.Sin(2.0 * Math.PI * normalizedCutoff * m) / (Math.PI * m);
            double window = 0.54 - (0.46 * Math.Cos(2.0 * Math.PI * i / (TapCount - 1)));
            _taps[i] = (float)(sinc * window);
            sum += _taps[i];
        }
        for (int i = 0; i < TapCount; i++) _taps[i] = (float)(_taps[i] / sum);
        int vectorWidth = Vector<float>.Count;
        _tapVectors = new Vector<float>[TapCount / vectorWidth];
        for (int index = 0; index < _tapVectors.Length; index++)
            _tapVectors[index] = new Vector<float>(_taps, index * vectorWidth);
    }

}
