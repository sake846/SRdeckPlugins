using SRdeck.Codecs.Ambe;
using System.IO;
using SRdeckPlugin.AribT98.Models;

namespace SRdeckPlugin.AribT98.Codec;

/// <summary>Audio policy and per-call codec state, independent of WPF and the host.</summary>
public sealed class AribT98Voice : IDisposable
{
    private readonly string deviceName;
    private AmbeDongleDecoder? dongle;
    private string? dongleError;
    private readonly byte[] parameters = new byte[4 * 49];
    private long lastPosition = long.MinValue;
    private (int? Uc, int? Manufacturer, int? Privacy)? lastCall;
    public string Status { get; private set; } = "音声待ち";
    public int CorrectedBits { get; private set; }

    public AribT98Voice(AribT98Settings? settings = null)
    {
        deviceName = AmbeDongleDecoder.DeviceName(settings?.AmbePort);
        if (settings is { AmbePort.Length: > 0 })
            try { dongle = new AmbeDongleDecoder(settings.AmbePort, settings.AmbeBaudRate); }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { dongleError = $"{deviceName}接続失敗 ({e.Message})"; }
        Reset();
    }

    private string IdleStatus => dongle is not null ? $"AMBE+2 / {deviceName}・音声待ち" :
        $"{(dongleError is null ? $"{deviceName}未接続・" : dongleError + "・")}受信状態の表示のみ";

    public bool TryRender(AribT98Frame frame, AribT98Settings settings, Span<short> pcm,
        int? privacyKeyOverride = null)
    {
        if (pcm.Length < 640) throw new ArgumentException("An 80 ms frame needs 640 PCM samples.");
        int privacyKey = privacyKeyOverride ?? settings.PrivacyKey;
        if (privacyKey is < 0 or > 32767) throw new ArgumentOutOfRangeException(nameof(privacyKeyOverride));
        pcm[..640].Clear();
        string? muted = frame.Modulation != AribT98Modulation.FourFsk ? "PSK音声形式未対応・TCH抽出のみ" :
            dongle is null ? IdleStatus :
            !settings.MonitorAudioEnabled ? "音声モニター OFF" :
            frame.IsEnd ? "終話" : !frame.IsCommunication ? "同期バースト" :
            !frame.FirstUnit || frame.RemainingUnits != 0 || frame.MessageType != 1 ? "標準音声メッセージ以外" :
            frame.CallType is not (0 or 1) ? "未対応の通話識別" :
            settings.UserCode != 0 && frame.UserCode != settings.UserCode ? "ユーザコード不一致" :
            frame.CallType == 1 && privacyKey == 0 ? "秘話鍵未設定・消音" :
            !frame.IsVoiceSlot(0) && !frame.IsVoiceSlot(1) &&
            !frame.IsVoiceSlot(2) && !frame.IsVoiceSlot(3) ? "データ通信" : null;
        if (muted is not null) { Reset(); Status = muted; return false; }
        var call = (frame.UserCode, frame.Manufacturer, frame.CallType);
        if (lastCall != call || lastPosition == long.MinValue || frame.SamplePosition - lastPosition is < 3700 or > 3980)
        { dongle?.Reset(); }
        lastCall = call;
        lastPosition = frame.SamplePosition;
        CorrectedBits = 0;
        byte voiceMask = 0;
        for (int slot = 0; slot < 4; slot++)
        {
            if (!frame.IsVoiceSlot(slot)) continue; // Preserve 80 ms, including mixed data/voice slots.
            voiceMask |= (byte)(1 << slot);
            var block = parameters.AsSpan(slot * 49, 49);
            CorrectedBits += AmbeDecoder.UnpackVoice(frame, slot, block, out _, out _);
            if (frame.CallType == 1) AmbeDecoder.Descramble(block, privacyKey, slot);
        }
        if (dongle is not null)
            try { dongle.Synthesize(parameters, voiceMask, pcm); }
            catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
            {
                dongle.Dispose(); dongle = null;
                dongleError = $"{deviceName}通信失敗 ({e.Message})";
                pcm[..640].Clear();
                Reset(); return false;
            }
        Status = frame.CallType == 1 ? $"AMBE+2 / {deviceName}・指定鍵で復号（鍵の正否は検証不可）" : $"AMBE+2 / {deviceName}音声出力";
        return true;
    }

    public void Reset() { dongle?.Reset(); lastCall = null; lastPosition = long.MinValue; CorrectedBits = 0; Status = IdleStatus; }
    public void Dispose() { dongle?.Dispose(); dongle = null; }
}
