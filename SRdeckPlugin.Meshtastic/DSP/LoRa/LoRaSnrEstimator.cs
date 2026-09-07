using SRdeck.DSP;

namespace SRdeckPlugin.Meshtastic.Dsp;

/// <summary>
/// Estimates in-band SNR from consecutive, identically windowed preamble chirps.
/// Their complex cross-correlation estimates the repeating signal power; the
/// remaining mean-square power estimates noise. Accumulation is linear, not dB.
/// A constant carrier offset rotates the correlation but not its magnitude.
/// Assumes stationary, uncorrelated noise between symbols and a stable channel;
/// fading, clock error and coherent interference can bias this estimate.
/// </summary>
internal sealed class LoRaSnrEstimator
{
    private readonly Complex[] previous;
    private readonly double noiseBandwidthFraction;
    private bool hasPrevious;
    private int pairs;
    private double correlationI, correlationQ, totalPower;

    public LoRaSnrEstimator(int symbolSamples, int bandwidthHz, int inputNoiseBandwidthHz)
    {
        if (symbolSamples <= 0 || bandwidthHz <= 0 || inputNoiseBandwidthHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(symbolSamples));
        previous = new Complex[symbolSamples];
        // The runtime shares a 250 kHz channelizer with 125 kHz detectors.
        // Refer noise to the selected LoRa bandwidth, not the sample rate or
        // the FFT-bin bandwidth. Raw-IQ tests instead pass their noise bandwidth.
        noiseBandwidthFraction = bandwidthHz / (double)inputNoiseBandwidthHz;
    }

    public float? EstimatedSnrDb
    {
        get
        {
            // At least three chirps; never publish an FFT peak or a stale frame
            // value as an SNR when there was insufficient training data.
            if (pairs < 2 || !double.IsFinite(totalPower) || totalPower <= 0) return null;
            double signal = Math.Sqrt(correlationI * correlationI + correlationQ * correlationQ);
            double noise = (totalPower - signal) * noiseBandwidthFraction;
            if (!double.IsFinite(signal) || signal <= 0 || noise <= 0) return null;
            double snr = 10 * Math.Log10(signal / noise);
            return double.IsFinite(snr) ? (float)snr : null;
        }
    }

    /// <summary>
    /// Selected-band mean signal-plus-noise power, relative to unit complex IQ
    /// power (dBFS). This is not an antenna-level dBm measurement without host
    /// calibration. Like SNR, it is measured on repeated preamble chirps.
    /// </summary>
    public float? EstimatedRssiDbfs
    {
        get
        {
            if (pairs < 2 || !double.IsFinite(totalPower) || totalPower <= 0) return null;
            double signal = Math.Sqrt(correlationI * correlationI + correlationQ * correlationQ);
            if (!double.IsFinite(signal)) return null;
            signal = Math.Min(signal, totalPower);
            double inBandPower = (signal + (totalPower - signal) * noiseBandwidthFraction) /
                                 (pairs * (double)previous.Length);
            double rssi = 10 * Math.Log10(inBandPower);
            return double.IsFinite(rssi) ? (float)rssi : null;
        }
    }

    public void Observe(ReadOnlySpan<Complex> samples)
    {
        if (samples.Length != previous.Length) throw new ArgumentException("Expected one complete chirp.", nameof(samples));
        if (hasPrevious)
        {
            for (int n = 0; n < samples.Length; n++)
            {
                double ai = previous[n].X, aq = previous[n].Y;
                double bi = samples[n].X, bq = samples[n].Y;
                correlationI += ai * bi + aq * bq;
                correlationQ += ai * bq - aq * bi;
                totalPower += (ai * ai + aq * aq + bi * bi + bq * bq) * 0.5;
            }
            pairs++;
        }
        samples.CopyTo(previous);
        hasPrevious = true;
    }

    public void Reset()
    {
        hasPrevious = false;
        pairs = 0;
        correlationI = correlationQ = totalPower = 0;
    }

    // A timing adjustment changes the window's phase within the chirp. Do not
    // correlate across it, but retain valid pairs measured before adjustment.
    public void BreakSequence() => hasPrevious = false;
}
