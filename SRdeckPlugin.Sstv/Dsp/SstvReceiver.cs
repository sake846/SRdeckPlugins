using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sstv.Models;

namespace SRdeckPlugin.Sstv.Dsp;

internal sealed class SstvReceiver
{
    public const int SampleRateHz = 24_000;
    public const int AudioCenterHz = 1_900;
    private const double SamplesPerMs = SampleRateHz / 1_000.0;
    private readonly float[] tones = new float[SampleRateHz];
    private readonly SstvDemodulator demodulator = new();
    private long position;
    private int leaderSamples;
    private int transitionSamples;
    private int visState;
    private long visStart;
    private long breakStart;
    private long syncStart;
    private int syncSamples;
    private int missingSamples;
    private double lineStart;
    private SstvMode? mode;
    private byte[] pixels = [];
    private byte[] robotY = [];
    private byte[] robotCr = [];
    private int row;
    private string imageId = string.Empty;
    private DateTimeOffset startedAt;

    public string Status { get; private set; } = "VIS待機";
    public double ToneHz { get; private set; }
    public int ReceivedLines => row;
    public SstvImage? CurrentImage => mode is null || row == 0 ? null : Snapshot(false);
    public event Action<SstvImage>? ImageUpdated;

    public void Reset()
    {
        position = 0;
        leaderSamples = transitionSamples = visState = syncSamples = missingSamples = row = 0;
        mode = null;
        pixels = robotY = robotCr = [];
        Array.Clear(tones);
        demodulator.Reset();
        ToneHz = 0;
        Status = "VIS待機";
    }

    public void Process(ReadOnlySpan<Complex32> samples, bool fm, DateTimeOffset time)
    {
        foreach (Complex32 sample in samples)
            ProcessTone(demodulator.Process(sample, fm), time);
    }

    internal void ProcessTone(double frequency, DateTimeOffset time)
    {
        ToneHz = double.IsFinite(frequency) ? frequency : 0;
        tones[position % tones.Length] = (float)ToneHz;
        DetectVis(time);
        if (mode is not null)
        {
            if (ToneHz is < 900 or > 2_600)
                missingSamples++;
            else missingSamples = 0;
            if (missingSamples > SampleRateHz / 4)
            {
                mode = null;
                Status = "信号断 / VIS待機";
            }
            else
            {
                TrackSync();
                if (position >= lineStart + mode.LineMs * SamplesPerMs)
                {
                    DecodeLine();
                    lineStart += mode.LineMs * SamplesPerMs;
                    bool complete = row >= mode.Height;
                    if (mode.Family != SstvFamily.Robot || row % 2 == 0)
                        ImageUpdated?.Invoke(Snapshot(complete));
                    if (complete)
                    {
                        Status = $"{mode.Name} 受信完了 / VIS待機";
                        mode = null;
                    }
                }
            }
        }
        position++;
    }

    private void DetectVis(DateTimeOffset time)
    {
        bool leader = Math.Abs(ToneHz - 1_900) < 55;
        bool sync = Math.Abs(ToneHz - 1_200) < 45;
        if (visState == 3)
        {
            if (position < visStart + 300 * SamplesPerMs) return;
            int code = 0;
            bool valid = Math.Abs(Mean(visStart + 10 * SamplesPerMs, 10 * SamplesPerMs) - 1_200) < 50 &&
                Math.Abs(Mean(visStart + 280 * SamplesPerMs, 10 * SamplesPerMs) - 1_200) < 50;
            for (int bit = 0; bit < 8; bit++)
            {
                double tone = Mean(visStart + (40 + 30 * bit) * SamplesPerMs, 10 * SamplesPerMs);
                if (Math.Abs(tone - 1_100) < 50) code |= 1 << bit;
                else if (Math.Abs(tone - 1_300) >= 50) valid = false;
            }
            visState = leaderSamples = 0;
            if (!valid || System.Numerics.BitOperations.PopCount((uint)code) % 2 != 0)
            {
                if (mode is null) Status = "VISエラー / 再待機";
                return;
            }
            SstvMode? detected = SstvMode.All.FirstOrDefault(item => item.Vis == (code & 127));
            if (detected is null)
            {
                mode = null;
                Status = $"未対応VIS {code & 127} / 再待機";
                return;
            }
            mode = detected;
            pixels = new byte[mode.Width * mode.Height * 3];
            robotY = new byte[mode.Width];
            robotCr = new byte[mode.Width];
            imageId = Guid.NewGuid().ToString("N");
            startedAt = time;
            row = syncSamples = missingSamples = 0;
            lineStart = visStart + 300 * SamplesPerMs;
            Status = $"{mode.Name} 受信中";
            return;
        }
        if (visState == 1)
        {
            double duration = (position - breakStart) / SamplesPerMs;
            if (duration <= 25)
            {
                if (leader && duration >= 5)
                {
                    visState = 2;
                    leaderSamples = 1;
                    transitionSamples = 0;
                }
                return;
            }
            visState = leaderSamples = 0;
            return;
        }
        if (leader)
        {
            leaderSamples = Math.Min(leaderSamples + 1, SampleRateHz);
            transitionSamples = 0;
            return;
        }
        // The FM audio filter slews through intermediate frequencies at tone boundaries.
        if (!sync && (visState == 2 || leaderSamples >= 240 * SamplesPerMs) &&
            ++transitionSamples <= 10 * SamplesPerMs)
            return;
        if (sync && leaderSamples >= 240 * SamplesPerMs)
        {
            if (visState == 2)
            {
                visStart = position;
                visState = 3;
            }
            else
            {
                breakStart = position;
                visState = 1;
            }
        }
        else visState = 0;
        leaderSamples = 0;
    }

    private void TrackSync()
    {
        if (Math.Abs(ToneHz - 1_200) < 65)
        {
            if (syncSamples++ == 0) syncStart = position;
            return;
        }
        if (mode is not null && Math.Abs(syncSamples / SamplesPerMs - mode.SyncMs) < 1.5)
        {
            double expected = lineStart + mode.SyncEndMs * SamplesPerMs;
            // Some Scottie transmitters prepend a sync to the first green scan.
            if (mode.Family == SstvFamily.Scottie && row == 0 &&
                Math.Abs(syncStart - lineStart) < 3 * SamplesPerMs)
                lineStart = position;
            else if (Math.Abs(position - expected) < 8 * SamplesPerMs)
                lineStart = position - mode.SyncEndMs * SamplesPerMs;
        }
        syncSamples = 0;
    }

    private void DecodeLine()
    {
        SstvMode m = mode!;
        for (int x = 0; x < m.Width; x++)
        {
            double pixel = (x + 0.5) * m.ScanMs / m.Width;
            if (m.Family is SstvFamily.Martin or SstvFamily.Scottie)
            {
                double green = m.Family == SstvFamily.Martin ? 5.434 : 1.5;
                double gap = m.Family == SstvFamily.Martin ? 0.572 : 1.5;
                double blue = green + m.ScanMs + gap;
                double red = blue + m.ScanMs + gap +
                    (m.Family == SstvFamily.Scottie ? m.SyncMs : 0);
                int offset = (row * m.Width + x) * 3;
                pixels[offset] = Level(red + pixel, m.ScanMs / m.Width);
                pixels[offset + 1] = Level(green + pixel, m.ScanMs / m.Width);
                pixels[offset + 2] = Level(blue + pixel, m.ScanMs / m.Width);
            }
            else if (m.Family == SstvFamily.Pd)
            {
                byte y0 = Level(22.08 + pixel, m.ScanMs / m.Width);
                byte cr = Level(22.08 + m.ScanMs + pixel, m.ScanMs / m.Width);
                byte cb = Level(22.08 + 2 * m.ScanMs + pixel, m.ScanMs / m.Width);
                byte y1 = Level(22.08 + 3 * m.ScanMs + pixel, m.ScanMs / m.Width);
                WriteYuv(row, x, y0, cr, cb);
                WriteYuv(row + 1, x, y1, cr, cb);
            }
            else
            {
                byte y = Level(12 + pixel, 88.0 / m.Width);
                byte chroma = Level(106 + pixel / 2, 44.0 / m.Width);
                if (row % 2 == 0)
                {
                    robotY[x] = y;
                    robotCr[x] = chroma;
                }
                else
                {
                    WriteYuv(row - 1, x, robotY[x], robotCr[x], chroma);
                    WriteYuv(row, x, y, robotCr[x], chroma);
                }
            }
        }
        row += m.Family == SstvFamily.Pd ? 2 : 1;
    }

    private byte Level(double milliseconds, double pixelMs) => ClampByte(
        (Mean(lineStart + milliseconds * SamplesPerMs - pixelMs * SamplesPerMs * 0.25,
            Math.Max(1, pixelMs * SamplesPerMs * 0.5)) - 1_500) * 255 / 800);

    private double Mean(double start, double length)
    {
        long first = Math.Max(0, (long)Math.Round(start));
        long last = Math.Min(position, first + Math.Max(1, (long)Math.Round(length)) - 1);
        if (first > last || first <= position - tones.Length) return 0;
        double sum = 0;
        for (long i = first; i <= last; i++) sum += tones[i % tones.Length];
        return sum / (last - first + 1);
    }

    private void WriteYuv(int y, int x, byte luminance, byte cr, byte cb)
    {
        int offset = (y * mode!.Width + x) * 3;
        pixels[offset] = ClampByte(luminance + 1.402 * (cr - 128));
        pixels[offset + 1] = ClampByte(luminance - 0.714136 * (cr - 128) - 0.344136 * (cb - 128));
        pixels[offset + 2] = ClampByte(luminance + 1.772 * (cb - 128));
    }

    private static byte ClampByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    private SstvImage Snapshot(bool complete) => new(imageId, mode!.Name, mode.Width,
        mode.Height, row, (byte[])pixels.Clone(), startedAt, complete);
}
