using System.Runtime.CompilerServices;

namespace SRdeckPlugin.Meshtastic.Dsp;

/// <summary>
/// Owns header/payload symbol buffers and frame FEC/CRC assembly for one detector.
/// Called serially by its detector. Reset discards a partial frame and retains reusable arrays.
/// Acquisition timing, signal quality, diagnostics and state transitions belong to the detector.
/// </summary>
internal sealed class LoRaFrameDecoder
{
    private readonly int _spreadingFactor;
    private readonly LoRaSoftDecodingOptions? _softOptions;
    private readonly LoRaCrcListDecodingOptions? _crcListOptions;
    private readonly ushort[] _headerSymbols = new ushort[8];
    private readonly LoRaSoftSymbol[]? _headerSoftSymbols;
    private readonly ushort[] _payloadBlockSymbols = new ushort[8];
    private readonly LoRaSoftSymbol[]? _payloadBlockSoftSymbols;
    private List<byte>? _payloadNibbles;
    private List<LoRaSoftSymbol>? _payloadSoftSymbols;
    private int _payloadCorrectedCodewords;

    public int HeaderSymbolCount { get; private set; }
    public int PayloadBlockSymbolCount { get; private set; }
    public LoRaExplicitHeader? ActiveHeader { get; private set; }
    public bool HasPayload => ActiveHeader is not null && _payloadNibbles is not null;

    public LoRaFrameDecoder(int spreadingFactor, LoRaSoftDecodingOptions? softOptions,
        LoRaCrcListDecodingOptions? crcListOptions)
    {
        _spreadingFactor = spreadingFactor;
        _softOptions = softOptions;
        _crcListOptions = crcListOptions;
        if (softOptions is not null)
        {
            _headerSoftSymbols = new LoRaSoftSymbol[8];
            _payloadBlockSoftSymbols = new LoRaSoftSymbol[8];
        }
    }

    public void BeginHeader() => HeaderSymbolCount = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LoRaExplicitHeader? PushHeader(ushort symbol, LoRaSoftSymbol? softSymbol)
    {
        _headerSymbols[HeaderSymbolCount] = symbol;
        if (_headerSoftSymbols is not null) _headerSoftSymbols[HeaderSymbolCount] = softSymbol!;
        HeaderSymbolCount++;
        if (HeaderSymbolCount < _headerSymbols.Length) return null;
        return _softOptions is null
            ? LoRaExplicitHeaderDecoder.Decode(_headerSymbols, _spreadingFactor)
            : LoRaExplicitHeaderDecoder.DecodeSoft(
                _headerSoftSymbols!, _spreadingFactor, _softOptions.MissingCandidatePenalty);
    }

    public void BeginPayload(LoRaExplicitHeader header)
    {
        int requiredNibbles = (header.PayloadLength * 2) + (header.HasPayloadCrc ? 4 : 0);
        ActiveHeader = header;
        _payloadNibbles = new List<byte>(requiredNibbles);
        if (_softOptions is not null)
            _payloadSoftSymbols = new List<LoRaSoftSymbol>(header.CodingRateDenominator * 2);
        foreach (byte nibble in header.InitialPayloadNibbles)
        {
            if (_payloadNibbles.Count == requiredNibbles) break;
            _payloadNibbles.Add(nibble);
        }
        _payloadCorrectedCodewords = header.CorrectedCodewords;
        PayloadBlockSymbolCount = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PushPayload(ushort symbol, LoRaSoftSymbol? softSymbol)
    {
        LoRaExplicitHeader header = ActiveHeader!;
        _payloadBlockSymbols[PayloadBlockSymbolCount] = symbol;
        if (_payloadBlockSoftSymbols is not null)
        {
            _payloadBlockSoftSymbols[PayloadBlockSymbolCount] = softSymbol!;
            _payloadSoftSymbols!.Add(softSymbol!);
        }
        PayloadBlockSymbolCount++;
        if (PayloadBlockSymbolCount < header.CodingRateDenominator) return false;

        byte[] nibbles = _softOptions is null
            ? LoRaPayloadDecoder.DecodeBlock(
                _payloadBlockSymbols.AsSpan(0, header.CodingRateDenominator).ToArray(),
                header.CodingRateDenominator, _spreadingFactor, out int correctedCodewords)
            : LoRaPayloadDecoder.DecodeBlockSoft(
                _payloadBlockSoftSymbols!.AsSpan(0, header.CodingRateDenominator).ToArray(),
                header.CodingRateDenominator, _spreadingFactor,
                _softOptions.MissingCandidatePenalty, out correctedCodewords);
        _payloadCorrectedCodewords += correctedCodewords;
        int requiredNibbles = (header.PayloadLength * 2) + (header.HasPayloadCrc ? 4 : 0);
        foreach (byte nibble in nibbles)
        {
            if (_payloadNibbles!.Count == requiredNibbles) break;
            _payloadNibbles.Add(nibble);
        }
        PayloadBlockSymbolCount = 0;
        return true;
    }

    public LoRaPayloadFrame? BuildCompletedPayload(out LoRaCrcListDecodingResult? listResult)
    {
        listResult = null;
        LoRaExplicitHeader? header = ActiveHeader;
        List<byte>? nibbles = _payloadNibbles;
        if (header is null || nibbles is null) return null;
        int requiredNibbles = (header.PayloadLength * 2) + (header.HasPayloadCrc ? 4 : 0);
        if (nibbles.Count < requiredNibbles) return null;

        LoRaSoftFrameInformation? softInformation = _softOptions is null
            ? null
            : new LoRaSoftFrameInformation(_softOptions.DecisionMode, _softOptions.CandidateCount,
                _headerSoftSymbols!.ToArray(), _payloadSoftSymbols!.ToArray());
        LoRaPayloadFrame frame = LoRaPayloadDecoder.BuildFrame(
            header, nibbles, _payloadCorrectedCodewords, softInformation);
        if (_crcListOptions is not null && frame.HasPayloadCrc &&
            frame.IsPayloadCrcValid is false && softInformation is not null)
        {
            listResult = LoRaCrcListDecoder.TryRecover(header, softInformation, _crcListOptions);
            if (listResult.RecoveredFrame is not null) frame = listResult.RecoveredFrame;
        }
        return frame;
    }

    public void Reset()
    {
        HeaderSymbolCount = 0;
        PayloadBlockSymbolCount = 0;
        _payloadNibbles = null;
        _payloadSoftSymbols = null;
        ActiveHeader = null;
        _payloadCorrectedCodewords = 0;
    }
}
