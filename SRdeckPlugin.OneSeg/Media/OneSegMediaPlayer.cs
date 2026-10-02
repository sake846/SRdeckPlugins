using LibVLCSharp.Shared;
using SRdeckPlugin.OneSeg.Models;

namespace SRdeckPlugin.OneSeg.Media;

internal sealed class OneSegMediaPlayer : IDisposable
{
    internal const int MainAudioChannelOptionId = 1001;
    internal const int SecondaryAudioChannelOptionId = 1002;
    internal const int MainAndSecondaryAudioChannelOptionId = 1003;
    internal const int MonoAudioChannelOptionId = 1004;
    // VLC 3 supports AOUT_VAR_CHAN_MONO (7), although LibVLCSharp 3.10 does
    // not yet expose that value in AudioOutputChannel.
    private const AudioOutputChannel MonoAudioOutputChannel = (AudioOutputChannel)7;
    private readonly object lifecycleGate = new();
    private readonly object streamGate = new();
    private readonly LibVLC libVlc;
    private readonly OneSegPatInjector patInjector = new();
    private ProducerConsumerStream? stream;
    private LibVLCSharp.Shared.Media? media;
    private byte? audioComponentType;
    private bool usesAudioChannelSelection;
    private int appliedAudioTrackId = -1;
    private int audioChannelSelectionApplyAttempts;

    public MediaPlayer Player { get; }
    public bool VideoSuperResolutionEnabled { get; }

    public OneSegMediaPlayer(bool videoSuperResolutionEnabled = false)
    {
        Core.Initialize();
        VideoSuperResolutionEnabled = videoSuperResolutionEnabled;
        // StreamMediaInput is a live, non-seekable source. Give its access
        // module enough headroom for SDR decode bursts and retain LibVLC's
        // normal clock-jitter correction instead of forcing zero tolerance.
        libVlc = videoSuperResolutionEnabled
            ? new LibVLC("--live-caching=1000", "--network-caching=1000",
                "--vout=direct3d11", "--d3d11-upscale-mode=super")
            : new LibVLC("--live-caching=1000", "--network-caching=1000");
        Player = new MediaPlayer(libVlc);
    }

    public void Start(int volume)
    {
        lock (lifecycleGate)
        {
            StopCore();
            audioComponentType = null;
            usesAudioChannelSelection = false;
            appliedAudioTrackId = -1;
            audioChannelSelectionApplyAttempts = 4;
            StartCore(Math.Clamp(volume, 0, 100));
        }
    }

    public void Submit(ReadOnlySpan<byte> packet)
    {
        lock (streamGate)
        {
            ProducerConsumerStream? target = stream;
            if (target is null) return;
            if (patInjector.TryCreateForPmt(packet, out byte[] pat)) target.WritePacket(pat);
            target.WritePacket(packet);
        }
    }

    public void SetVolume(int volume)
    {
        lock (lifecycleGate) Player.Volume = Math.Clamp(volume, 0, 100);
    }

    public IReadOnlyList<OneSegAudioTrackOption> GetAudioTracks()
    {
        lock (lifecycleGate) return GetAudioTracksCore();
    }

    private IReadOnlyList<OneSegAudioTrackOption> GetAudioTracksCore()
    {
        try
        {
            IReadOnlyList<OneSegAudioTrackOption> channelOptions =
                CreateBroadcastChannelOptions(audioComponentType);
            if (channelOptions.Count > 0)
            {
                // The one-seg PMT carries a single AAC stream. Once its ARIB
                // coding mode is known, the choices are output channels rather
                // than VLC elementary-stream tracks. Avoid polling the native
                // track list every 500 ms; that can contend with the AAC decoder.
                usesAudioChannelSelection = true;
                if (appliedAudioTrackId < 0) audioChannelSelectionApplyAttempts = Math.Max(audioChannelSelectionApplyAttempts, 4);
                return channelOptions;
            }

            var tracks = Player.AudioTrackDescription
                .Where(track => track.Id >= 0)
                .ToArray();
            DisableBroadcastChannelSelection();
            return tracks.Select(track => new OneSegAudioTrackOption(track.Id, track.Name ?? "")).ToArray();
        }
        catch
        {
            DisableBroadcastChannelSelection();
            return [];
        }
    }

    public bool UsesAudioChannelSelection
    {
        get { lock (lifecycleGate) return usesAudioChannelSelection; }
    }
    public int DefaultAudioTrackId
    {
        get { lock (lifecycleGate) return DefaultBroadcastChannelOption(audioComponentType); }
    }

    internal static int DefaultBroadcastChannelOption(byte? componentType) => componentType switch
    {
        0x02 => MainAudioChannelOptionId,
        0x01 => MonoAudioChannelOptionId,
        _ => MainAndSecondaryAudioChannelOptionId
    };

    public int ResolveAudioTrackSelection(int requestedTrackId)
    {
        lock (lifecycleGate)
            return usesAudioChannelSelection
                ? ResolveBroadcastChannelSelection(audioComponentType, requestedTrackId)
                : requestedTrackId is >= 0 and < 1000 ? requestedTrackId : -1;
    }

    internal static int ResolveBroadcastChannelSelection(byte? componentType, int requestedTrackId) =>
        IsValidBroadcastChannelOption(componentType, requestedTrackId)
            ? requestedTrackId : DefaultBroadcastChannelOption(componentType);

    public bool SetAudioComponentType(byte? componentType)
    {
        lock (lifecycleGate)
        {
            if (audioComponentType == componentType) return false;
            bool decoderRestartRequired = audioComponentType is not null && componentType is not null;
            audioComponentType = componentType;
            appliedAudioTrackId = -1;
            audioChannelSelectionApplyAttempts = componentType is not null ? 4 : 0;
            if (componentType is null) DisableBroadcastChannelSelection();
            return decoderRestartRequired;
        }
    }

    public void RestartPlayback()
    {
        lock (lifecycleGate)
        {
            ProducerConsumerStream? replacementStream;
            LibVLCSharp.Shared.Media? replacementMedia;
            ProducerConsumerStream? previousStream;
            LibVLCSharp.Shared.Media? previousMedia;
            lock (streamGate)
            {
                if (stream is null) return;
                previousStream = stream;
                previousMedia = media;
                replacementStream = new ProducerConsumerStream();
                replacementMedia = CreateMedia(replacementStream);
                patInjector.Reset();
                stream = replacementStream;
                media = replacementMedia;
            }

            // Keep accepting live TS packets while LibVLC tears down the old
            // playback pipeline.
            int volume = Player.Volume;
            previousStream.Complete();
            Player.Stop();
            previousMedia?.Dispose();
            previousStream.Dispose();
            appliedAudioTrackId = -1;
            audioChannelSelectionApplyAttempts = 4;
            try { Player.SetChannel(AudioOutputChannel.Stereo); } catch { }
            Player.Volume = volume;
            _ = Player.Play(replacementMedia);
        }
    }

    private void DisableBroadcastChannelSelection()
    {
        if (usesAudioChannelSelection)
        {
            try { Player.SetChannel(AudioOutputChannel.Stereo); } catch { }
        }
        usesAudioChannelSelection = false;
        appliedAudioTrackId = -1;
        audioChannelSelectionApplyAttempts = 0;
    }

    internal static IReadOnlyList<OneSegAudioTrackOption> CreateBroadcastChannelOptions(
        byte? componentType) => componentType switch
        {
            0x01 => [new(MonoAudioChannelOptionId, "モノラル")],
            0x02 =>
            [
                new(MainAudioChannelOptionId, "主音声"),
                new(SecondaryAudioChannelOptionId, "副音声"),
                new(MainAndSecondaryAudioChannelOptionId, "主＋副")
            ],
            0x03 =>
            [
                new(MainAndSecondaryAudioChannelOptionId, "ステレオ"),
                new(MonoAudioChannelOptionId, "モノラル")
            ],
            _ => []
        };

    internal static bool IsValidBroadcastChannelOption(byte? componentType, int trackId) =>
        componentType switch
        {
            0x01 => trackId == MonoAudioChannelOptionId,
            0x02 => trackId is MainAudioChannelOptionId or SecondaryAudioChannelOptionId or
                MainAndSecondaryAudioChannelOptionId,
            0x03 => trackId is MainAndSecondaryAudioChannelOptionId or MonoAudioChannelOptionId,
            _ => false
        };

    public bool SelectAudioTrack(int trackId)
    {
        lock (lifecycleGate)
        {
            try
            {
                if (usesAudioChannelSelection)
                {
                    int resolvedTrackId = ResolveBroadcastChannelSelection(audioComponentType, trackId);
                    AudioOutputChannel channel = resolvedTrackId switch
                    {
                        MainAudioChannelOptionId => AudioOutputChannel.Left,
                        SecondaryAudioChannelOptionId => AudioOutputChannel.Right,
                        MainAndSecondaryAudioChannelOptionId => AudioOutputChannel.Stereo,
                        MonoAudioChannelOptionId => MonoAudioOutputChannel,
                        _ => AudioOutputChannel.Stereo
                    };
                    bool selected = Player.SetChannel(channel);
                    if (selected)
                    {
                        appliedAudioTrackId = resolvedTrackId;
                        if (audioChannelSelectionApplyAttempts > 0) audioChannelSelectionApplyAttempts--;
                    }
                    return selected;
                }

                return trackId < 0 || Player.SetAudioTrack(trackId);
            }
            catch { return false; }
        }
    }
    public int CurrentAudioTrack
    {
        get
        {
            lock (lifecycleGate)
            {
                try
                {
                    return usesAudioChannelSelection ? appliedAudioTrackId : Player.AudioTrack;
                }
                catch { return -1; }
            }
        }
    }

    public bool AudioTrackSelectionNeedsApply
    {
        get { lock (lifecycleGate) return audioChannelSelectionApplyAttempts > 0; }
    }

    public void Stop()
    {
        lock (lifecycleGate) StopCore();
    }

    private void StartCore(int volume)
    {
        var nextStream = new ProducerConsumerStream();
        LibVLCSharp.Shared.Media nextMedia = CreateMedia(nextStream);
        lock (streamGate)
        {
            patInjector.Reset();
            stream = nextStream;
            media = nextMedia;
        }
        try { Player.SetChannel(AudioOutputChannel.Stereo); } catch { }
        appliedAudioTrackId = -1;
        audioChannelSelectionApplyAttempts = Math.Max(audioChannelSelectionApplyAttempts, 4);
        Player.Volume = volume;
        _ = Player.Play(nextMedia);
    }

    private LibVLCSharp.Shared.Media CreateMedia(ProducerConsumerStream input)
    {
        var value = new LibVLCSharp.Shared.Media(libVlc, new StreamMediaInput(input));
        value.AddOption(":demux=ts");
        value.AddOption(":live-caching=1000");
        return value;
    }

    private void StopCore()
    {
        ProducerConsumerStream? previousStream;
        LibVLCSharp.Shared.Media? previousMedia;
        lock (streamGate)
        {
            previousStream = stream;
            previousMedia = media;
            stream = null;
            media = null;
        }
        previousStream?.Complete();
        Player.Stop();
        previousMedia?.Dispose();
        previousStream?.Dispose();
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            StopCore();
            Player.Dispose();
            libVlc.Dispose();
        }
    }
}
