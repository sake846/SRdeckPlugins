using System.Diagnostics;
using System.Numerics;
using SRdeckPlugin.Contracts;
using SRdeckCore.SignalProcessing;
using SRdeckPlugin.Vdl.Models;
using SRdeckPlugin.Vdl.Protocols;

namespace SRdeckPlugin.Vdl.Dsp;

/// <summary>
/// Streaming VDL Mode 2 receiver. The physical layer is D8PSK at 10.5 ksym/s
/// with a 16-symbol preamble, a scrambled 25-bit length header and an
/// interleaved Reed-Solomon protected data field containing HDLC/AVLC frames.
/// </summary>
public sealed partial class VdlMode2Receiver
{
    private enum ReceiverState { Searching, Receiving }
    private enum HeaderDecodeStatus { Clean, Corrected, FecUncorrectable, InvalidLength }

    private sealed class AudioMonitor
    {
        private const int Decimation = WorkingSampleRate / MonitorAudioSampleRate;
        private const int HoldSamples = WorkingSampleRate / 10;
        private const double MonitorCenterHz = 1_500;
        private static readonly double RotationI = Math.Cos(2 * Math.PI * MonitorCenterHz / WorkingSampleRate);
        private static readonly double RotationQ = Math.Sin(2 * Math.PI * MonitorCenterHz / WorkingSampleRate);

        private int decimationCounter;
        private int squelchHold;
        private double signalPower;
        private double noiseAverage;
        private double oscillatorI = 1;
        private double oscillatorQ;
        private int normalizationCounter;

        public bool TryProcess(Complex32 sample, double noisePower, bool isReceiving, bool isSquelchEnabled, out float audio)
        {
            double power = sample.I * sample.I + sample.Q * sample.Q;

            if (signalPower == 0) signalPower = power;
            else signalPower += 0.002 * (power - signalPower);

            if (noiseAverage == 0) noiseAverage = power;
            else if (!isReceiving && squelchHold == 0) noiseAverage += 0.001 * (power - noiseAverage);

            bool signalPresent = !isSquelchEnabled || isReceiving ||
                (noiseAverage > 1e-20 && signalPower >= noiseAverage * 3.0);

            if (signalPresent) squelchHold = HoldSamples;
            else if (squelchHold > 0) squelchHold--;

            double audioRaw = sample.I * oscillatorI - sample.Q * oscillatorQ;
            double nextI = oscillatorI * RotationI - oscillatorQ * RotationQ;
            oscillatorQ = oscillatorI * RotationQ + oscillatorQ * RotationI;
            oscillatorI = nextI;
            if (++normalizationCounter == 4_096)
            {
                double inverseMagnitude = 1 / Math.Sqrt(oscillatorI * oscillatorI + oscillatorQ * oscillatorQ);
                oscillatorI *= inverseMagnitude;
                oscillatorQ *= inverseMagnitude;
                normalizationCounter = 0;
            }

            if (++decimationCounter < Decimation)
            {
                audio = 0;
                return false;
            }
            decimationCounter = 0;
            if (isSquelchEnabled && squelchHold == 0)
            {
                audio = 0;
                return true;
            }
            double gain = 0.18 / Math.Sqrt(Math.Max(signalPower, 1e-12));
            audio = (float)Math.Tanh(audioRaw * Math.Min(gain, 2_000));
            return true;
        }

        public void Reset()
        {
            decimationCounter = 0;
            squelchHold = 0;
            signalPower = 0;
            noiseAverage = 0;
            oscillatorI = 1;
            oscillatorQ = 0;
            normalizationCounter = 0;
        }
    }

    /// <summary>VDL2 matched filter: root-raised-cosine, alpha=0.6, span=8 symbols.</summary>
    private sealed class RootRaisedCosineFilter
    {
        private const int SymbolSpan = 8;
        private readonly float[] taps = CreateRootRaisedCosineTaps();
        private readonly Complex32[] history = new Complex32[SymbolSpan * SamplesPerSymbol + 1];
        private int position = -1;

        public Complex32 Process(Complex32 input)
        {
            if (++position == history.Length) position = 0;
            history[position] = input;
            double i = 0, q = 0;
            int index = position;
            for (int tap = 0; tap < taps.Length; tap++)
            {
                i += history[index].I * taps[tap];
                q += history[index].Q * taps[tap];
                if (--index < 0) index = history.Length - 1;
            }
            return new((float)i, (float)q);
        }

        public void Reset()
        {
            Array.Clear(history);
            position = -1;
        }
    }
}
