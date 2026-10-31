using SRdeckPlugin.Aprs.Protocols;

namespace SRdeckPlugin.Aprs.Dsp;

/// <summary>LSB-first HDLC framing. Buffer bounds include worst-case bit stuffing.</summary>
public sealed class HdlcDecoder
{
    private readonly byte[] bits = new byte[4096];
    private readonly byte[] frame = new byte[330];
    private int count, shift, ones;
    private bool collecting;
    public long ValidFrames { get; private set; }
    public long RejectedFrames { get; private set; }
    public event Action<byte[]>? FrameDecoded;

    public void Reset()
    {
        count = shift = ones = 0;
        collecting = false;
        ValidFrames = RejectedFrames = 0;
    }

    public void PushBit(bool bit)
    {
        shift = ((shift >> 1) | (bit ? 0x80 : 0)) & 255;
        ones = bit ? ones + 1 : 0;
        if (collecting)
        {
            if (count == bits.Length) { collecting = false; count = 0; RejectedFrames++; }
            else bits[count++] = (byte)(bit ? 1 : 0);
        }
        if (ones >= 7) { collecting = false; count = 0; }
        if (shift != 0x7e) return;
        if (collecting && count > 8) Complete(count - 8);
        collecting = true;
        count = 0;
    }

    private void Complete(int length)
    {
        int outputBits = 0, run = 0;
        Array.Clear(frame);
        for (int i = 0; i < length; i++)
        {
            int bit = bits[i];
            if (run == 5)
            {
                if (bit != 0) { RejectedFrames++; return; }
                run = 0;
                continue;
            }
            run = bit == 1 ? run + 1 : 0;
            if (outputBits >= frame.Length * 8) { RejectedFrames++; return; }
            frame[outputBits >> 3] |= (byte)(bit << (outputBits & 7));
            outputBits++;
        }
        if (run == 5 || (outputBits & 7) != 0 || outputBits < 18 * 8 ||
            Ax25Decoder.ComputeFcs(frame.AsSpan(0, outputBits / 8 - 2)) !=
            (ushort)(frame[outputBits / 8 - 2] | frame[outputBits / 8 - 1] << 8))
        { RejectedFrames++; return; }
        ValidFrames++;
        FrameDecoded?.Invoke(frame.AsSpan(0, outputBits / 8).ToArray());
    }
}
