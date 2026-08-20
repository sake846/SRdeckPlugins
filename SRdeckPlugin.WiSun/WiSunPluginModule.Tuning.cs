using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using SRdeckPlugin.Contracts;
using SRdeckPlugin.Wpf;
using SRdeckPlugin.WiSun.Dsp;
using SRdeckPlugin.WiSun.Models;
using SRdeckPlugin.WiSun.ViewModels;
using SRdeckPlugin.WiSun.Views;

namespace SRdeckPlugin.WiSun;

public sealed partial class WiSunPluginModule
{
    private static string? ProfileIdForFrequency(long frequencyHz, WiSunPhyProfile phyProfile) =>
        ProfileDefinitions.FirstOrDefault(profile =>
            profile.FrequencyHz == frequencyHz && profile.PhyProfile == phyProfile)?.Id;

    private static string TuningProfileId(WiSunSettings settings)
    {
        if (settings.PhyProfile == WiSunPhyProfile.Custom) return "custom";
        int selectedCount = settings.PhyProfile == WiSunPhyProfile.HanBRoute
            ? settings.HanChannels.Length
            : settings.FanChannels.Length;
        return selectedCount == 1
            ? ProfileIdForFrequency(settings.FrequencyHz, settings.PhyProfile) ?? "custom"
            : "custom";
    }

    private static ValueTask<PluginTuningResult> RequestTuningAsync(
        IPluginHostContext context,
        string profileId,
        WiSunSettings settings,
        CancellationToken cancellationToken) => context.Tuning.RequestAsync(new PluginTuningRequest(
            profileId,
            profileId == "custom" ? $"Wi-SUN {SelectedChannelSummary(settings)}" :
                (SupportedProfiles.FirstOrDefault(profile => profile.Id == profileId)?.DisplayName ?? $"Wi-SUN {SelectedChannelSummary(settings)}"),
            CreateChannelRequests(settings)
                .Select(request => new TuningTarget(
                    request.CenterFrequencyHz,
                    TuningBandwidthHz(settings.PhyProfile, settings.CustomBitRateBps)))
                .ToArray(),
            PreferredCenterFrequencyHz(settings),
            RequiredSourceSampleRateHz(settings),
            checked((int)settings.FrequencyStepHz),
            true,
            false,
            PluginGainPreference.Automatic), cancellationToken);

    private static void ValidateTuningResult(PluginTuningResult result, WiSunSettings settings)
    {
        if (result.Outcome == PluginTuningOutcome.Rejected)
            throw new InvalidOperationException($"Wi-SUN tuning was rejected: {result.Message}");
        int minimumSampleRateHz = RequiredSourceSampleRateHz(settings);
        if (result.SampleRateHz < minimumSampleRateHz)
            throw new InvalidOperationException(
                $"Wi-SUN requires at least {minimumSampleRateHz:N0} samples/s.");
    }

    private async ValueTask<PluginTuningResult> RequestAndValidateTuningAsync(
        IPluginHostContext context,
        string profileId,
        WiSunSettings settings,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _tuningUpdateInProgress);
        try
        {
            PluginTuningResult result = await RequestTuningAsync(
                context, profileId, settings, cancellationToken).ConfigureAwait(false);
            ValidateTuningResult(result, settings);
            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _tuningUpdateInProgress);
        }
    }

    private static string ChannelRequestId(WiSunPhyProfile profile, int channel) => profile switch
    {
        WiSunPhyProfile.FanMode1b => $"wisun-fan-mode1b-{channel:D2}",
        WiSunPhyProfile.FanMode2 => $"wisun-fan-mode2-{channel:D2}",
        WiSunPhyProfile.FanMode3 => $"wisun-fan-mode3-{channel:D2}",
        WiSunPhyProfile.FanMode4 => $"wisun-fan-mode4-{channel:D2}",
        WiSunPhyProfile.FanMode5 => $"wisun-fan-mode5-{channel:D2}",
        WiSunPhyProfile.HanBRoute => $"wisun-han-broute-{channel:D2}",
        WiSunPhyProfile.Custom => $"wisun-custom-{channel:D2}",
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };

    private static IReadOnlyList<PluginChannelRequest> CreateChannelRequests(
        WiSunSettings settings)
    {
        WiSunPhyProfile profile = settings.PhyProfile;
        if (profile == WiSunPhyProfile.Custom)
        {
            long frequencyHz = settings.CustomFrequencyHz;
            int bw = WiSunDemodulator.ChannelBandwidthHz(profile, settings.CustomBitRateBps);
            int rate = WiSunDemodulator.WorkingSampleRateHz(profile, settings.CustomBitRateBps);
            return [
                new PluginChannelRequest(
                    "wisun-custom-01", frequencyHz,
                    bw, rate, 800_000, 400_000,
                    64, 3, 16, false, 1_000_000, 2_000_000, 8)
            ];
        }

        int[] channels = profile == WiSunPhyProfile.HanBRoute
            ? settings.HanChannels
            : settings.FanChannels;
        return channels.Select(channel =>
        {
            long frequencyHz = ChannelFrequencyHz(profile, channel);
            return new PluginChannelRequest(
                ChannelRequestId(profile, channel), frequencyHz,
                WiSunDemodulator.ChannelBandwidthHz(profile, settings.CustomBitRateBps),
                WiSunDemodulator.WorkingSampleRateHz(profile, settings.CustomBitRateBps), 800_000, 400_000,
                64, 3, 16, false, 1_000_000, 2_000_000, 8);
        }).ToArray();
    }

    private void RebuildDemodulators()
    {
        bool rejectInvalidFcs = _demodulators.Count == 0 ||
            _demodulator.RejectInvalidFcs;
        _demodulators.Clear();
        ushort? customSfd = ushort.TryParse(_settings.CustomSfdHex, System.Globalization.NumberStyles.HexNumber, null, out ushort sfdVal)
            ? sfdVal
            : null;

        foreach (PluginChannelRequest request in CreateChannelRequests(_settings))
        {
            var demodulator = new WiSunDemodulator
            {
                RejectInvalidFcs = rejectInvalidFcs,
                CustomSfd = customSfd,
                EnableRawBurstLog = _settings.EnableRawBurstLog
            };
            long frequencyHz = request.CenterFrequencyHz;
            demodulator.OnPacketDecoded += frame =>
            {
                lock (_processingGate) { _lastDemodulator = demodulator; }
                HandlePacketDecoded(frame);
            };
            demodulator.OnDiagnosticLog += message =>
            {
                lock (_processingGate) { _lastDemodulator = demodulator; }
                OnDiagnosticLog?.Invoke(
                    $"[{frequencyHz / 1_000_000.0:F3} MHz] {message}");
            };
            demodulator.OnDiagnosticCountersChanged += () =>
                OnDiagnosticCountersChanged?.Invoke();
            _demodulators.Add(request.Id, demodulator);
        }
        _demodulator = _demodulators.Values.First();
        _lastDemodulator = _demodulator;
    }

    private WiSunSettings SettingsForSingleProfile(WiSunProfileDefinition profile)
    {
        int channel = ChannelNumber(profile);
        return (_settings with
        {
            PhyProfile = profile.PhyProfile,
            FanChannels = profile.PhyProfile != WiSunPhyProfile.HanBRoute
                ? [channel]
                : _settings.FanChannels,
            HanChannels = profile.PhyProfile == WiSunPhyProfile.HanBRoute
                ? [channel]
                : _settings.HanChannels
        }).Normalize();
    }

    private static int ChannelNumber(WiSunProfileDefinition profile) => profile.PhyProfile switch
    {
        WiSunPhyProfile.HanBRoute => 4 + (int)((profile.FrequencyHz - 922_500_000L) / 400_000L),
        WiSunPhyProfile.Custom => 1,
        _ => (int)((profile.FrequencyHz - 920_600_000L) / 200_000L)
    };

    private static long ChannelFrequencyHz(WiSunPhyProfile profile, int channel) => profile switch
    {
        WiSunPhyProfile.HanBRoute => 922_500_000L + (channel - 4) * 400_000L,
        WiSunPhyProfile.Custom => 922_400_000L,
        _ => 920_600_000L + channel * 200_000L
    };

    private static long PreferredCenterFrequencyHz(WiSunSettings settings)
    {
        PluginChannelRequest[] requests = CreateChannelRequests(settings).ToArray();
        return (requests[0].CenterFrequencyHz +
            requests[^1].CenterFrequencyHz) / 2;
    }

    private static int RequiredSourceSampleRateHz(WiSunSettings settings)
    {
        PluginChannelRequest[] requests = CreateChannelRequests(settings).ToArray();
        int tuningBandwidthHz = TuningBandwidthHz(settings.PhyProfile);
        long lowerEdge = requests.Min(value =>
            value.CenterFrequencyHz - tuningBandwidthHz / 2L);
        long upperEdge = requests.Max(value =>
            value.CenterFrequencyHz + tuningBandwidthHz / 2L);
        long occupiedSpan = upperEdge - lowerEdge;
        int workingRate = WiSunDemodulator.WorkingSampleRateHz(settings.PhyProfile, settings.CustomBitRateBps);
        return checked((int)Math.Max(workingRate,
            ((occupiedSpan + 99_999) / 100_000) * 100_000));
    }

    private static bool TryReduceChannelsToFitHostSampleRate(
        WiSunSettings settings,
        int hostSampleRateHz,
        out WiSunSettings reduced)
    {
        reduced = settings;
        if (hostSampleRateHz <= 0 || settings.PhyProfile == WiSunPhyProfile.Custom) return false;

        int[] selected = settings.PhyProfile == WiSunPhyProfile.HanBRoute
            ? settings.HanChannels
            : settings.FanChannels;
        for (int count = selected.Length - 1; count >= 1; count--)
        {
            int[] subset = selected.Take(count).ToArray();
            WiSunSettings candidate = (settings.PhyProfile == WiSunPhyProfile.HanBRoute
                ? settings with { HanChannels = subset }
                : settings with { FanChannels = subset }).Normalize();
            if (RequiredSourceSampleRateHz(candidate) > hostSampleRateHz) continue;

            PluginChannelRequest[] requests = CreateChannelRequests(candidate).ToArray();
            int bandwidthHz = TuningBandwidthHz(candidate.PhyProfile, candidate.CustomBitRateBps);
            long lowerEdgeHz = requests.Min(request => request.CenterFrequencyHz - bandwidthHz / 2L);
            long upperEdgeHz = requests.Max(request => request.CenterFrequencyHz + bandwidthHz / 2L);
            if (upperEdgeHz - lowerEdgeHz > hostSampleRateHz * 0.95) continue;

            reduced = candidate;
            return true;
        }
        return false;
    }

    private static string SelectedChannelSummary(WiSunSettings settings)
    {
        if (settings.PhyProfile == WiSunPhyProfile.Custom)
            return $"カスタム {settings.CustomBitRateBps / 1_000}k {settings.CustomFrequencyHz / 1_000_000.0:F3}MHz (SFD:{settings.CustomSfdHex})";

        string phy = settings.PhyProfile switch
        {
            WiSunPhyProfile.HanBRoute => "HAN A,B",
            WiSunPhyProfile.FanMode2 => "FAN 100k",
            WiSunPhyProfile.FanMode3 => "FAN 150k",
            WiSunPhyProfile.FanMode4 => "FAN 200k",
            WiSunPhyProfile.FanMode5 => "FAN 300k",
            _ => "FAN"
        };
        int[] channels = settings.PhyProfile == WiSunPhyProfile.HanBRoute
            ? settings.HanChannels
            : settings.FanChannels;
        return $"{phy} Ch {string.Join(',', channels)}";
    }

    internal static int TuningBandwidthHz(WiSunPhyProfile profile, int customBitRateBps = 50_000) => profile switch
    {
        WiSunPhyProfile.FanMode1b => 200_000,
        WiSunPhyProfile.FanMode5 => 800_000,
        WiSunPhyProfile.Custom => (int)WiSunSettings.StepHzForBitRate(customBitRateBps),
        _ => 400_000
    };

    private static IReadOnlyList<WiSunProfileDefinition> CreateProfileDefinitions()
    {
        var profiles = new List<WiSunProfileDefinition>(100);
        for (int channel = 9; channel <= 37; channel++)
        {
            long frequencyHz = 920_600_000L + channel * 200_000L;
            profiles.Add(new WiSunProfileDefinition(
                $"fan-21-{channel:D2}",
                $"FAN Mode #1b Ch {channel:D2} — {frequencyHz / 1_000_000.0:F1} MHz",
                $"Wi-SUN FAN JP Channel Plan 21 / Mode #1b / 50 kbps / channel {channel}",
                frequencyHz, WiSunPhyProfile.FanMode1b, 200_000, channel == 9));

            if ((channel - 9) % 2 == 0)
            {
                profiles.Add(new WiSunProfileDefinition(
                    $"fan-mode2-{channel:D2}",
                    $"FAN Mode #2 Ch {channel:D2} — {frequencyHz / 1_000_000.0:F1} MHz",
                    $"Wi-SUN FAN JP / Mode #2 / 100 kbps / channel {channel}",
                    frequencyHz, WiSunPhyProfile.FanMode2, 400_000));
                profiles.Add(new WiSunProfileDefinition(
                    $"fan-mode3-{channel:D2}",
                    $"FAN Mode #3 Ch {channel:D2} — {frequencyHz / 1_000_000.0:F1} MHz",
                    $"Wi-SUN FAN JP / Mode #3 / 150 kbps / channel {channel}",
                    frequencyHz, WiSunPhyProfile.FanMode3, 400_000));
                profiles.Add(new WiSunProfileDefinition(
                    $"fan-mode4-{channel:D2}",
                    $"FAN Mode #4 Ch {channel:D2} — {frequencyHz / 1_000_000.0:F1} MHz",
                    $"Wi-SUN FAN JP / Mode #4 / 200 kbps / channel {channel}",
                    frequencyHz, WiSunPhyProfile.FanMode4, 400_000));
            }

            if ((channel - 9) % 4 == 0)
            {
                profiles.Add(new WiSunProfileDefinition(
                    $"fan-mode5-{channel:D2}",
                    $"FAN Mode #5 Ch {channel:D2} — {frequencyHz / 1_000_000.0:F1} MHz",
                    $"Wi-SUN FAN JP / Mode #5 / 300 kbps / channel {channel}",
                    frequencyHz, WiSunPhyProfile.FanMode5, 800_000));
            }
        }
        for (int channel = 4; channel <= 17; channel++)
        {
            long frequencyHz = 922_500_000L + (channel - 4) * 400_000L;
            int aribOddChannel = 33 + (channel - 4) * 2;
            profiles.Add(new WiSunProfileDefinition(
                $"han-broute-{channel:D2}",
                $"HAN A,Bルート Ch {channel:D2} — {frequencyHz / 1_000_000.0:F1} MHz",
                $"Wi-SUN HAN A,Bルート / 100 kbps / ARIB channels {aribOddChannel}+{aribOddChannel + 1}",
                frequencyHz, WiSunPhyProfile.HanBRoute, 400_000));
        }
        return profiles;
    }

    private void PersistSettings()
    {
        IPluginHostContext? context = _hostContext;
        if (context is null) return;
        var document = new PluginSettingsDocument(1, JsonSerializer.Serialize(_settings));
        lock (_settingsSaveGate)
        {
            _settingsSaveTail = SaveSettingsInOrderAsync(context, document, _settingsSaveTail);
        }
    }

    private static async Task SaveSettingsInOrderAsync(
        IPluginHostContext context,
        PluginSettingsDocument document,
        Task predecessor)
    {
        try
        {
            try { await predecessor.ConfigureAwait(false); }
            catch { /* A prior write was already logged; keep later settings writable. */ }
            await context.Settings.SaveAsync(document).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            context.Logger.Log(PluginLogLevel.Warning, "wisun.settings.save-failed",
                "Wi-SUN settings could not be saved.", exception);
        }
    }

    private void SetStatus(string value)
    {
        EnsureViewModel();
        IPluginHostContext? context = _hostContext;
        if (context is null || context.Dispatcher.CheckAccess()) _viewModel!.StatusText = value;
        else context.Dispatcher.Post(() => _viewModel!.StatusText = value);
    }

    private void OnTuningChanged(object? sender, PluginTuningResult result)
    {
        if (Volatile.Read(ref _tuningUpdateInProgress) != 0) return;
        // During SDR start/stop the host publishes intermediate radio-control
        // values. Re-requesting a profile before the first IQ block arrives can
        // race the device lifecycle and, in particular, HAN's wider channel.
        if (State != PluginLifecycleState.Streaming ||
            Volatile.Read(ref _hasReceivedStreamData) == 0) return;
        if (result.Outcome != PluginTuningOutcome.Rejected &&
            result.CenterFrequencyHz > 0)
        {
            if (_settings.PhyProfile == WiSunPhyProfile.Custom)
            {
                long stepHz = Math.Max(1L, _settings.FrequencyStepHz);
                long roundedFreqHz = ((result.CenterFrequencyHz + stepHz / 2) / stepHz) * stepHz;
                if (roundedFreqHz != _settings.CustomFrequencyHz)
                {
                    WiSunSettings updated = (_settings with
                    {
                        CustomFrequencyHz = roundedFreqHz,
                        FrequencyHz = roundedFreqHz
                    }).Normalize();
                    lock (_consumptionGate)
                        lock (_processingGate)
                        {
                            _settings = updated;
                            RebuildDemodulators();
                        }
                    PersistSettings();
                    _viewModel?.SynchronizeFrequency(roundedFreqHz);
                    SetStatus($"{(State == PluginLifecycleState.Streaming ? "受信中" : "待機中")} / {roundedFreqHz / 1_000_000.0:F3} MHz");
                }
            }
            else
            {
                // A spectrum pan is a host-side tuning change.  Do not immediately
                // request the selected Wi-SUN plan again here: doing so moves the
                // center frequency back under the pointer and makes horizontal
                // scrolling appear to be ignored.  Selecting a profile or changing
                // channels still explicitly requests the required tuning above.
            }
        }
    }
}
