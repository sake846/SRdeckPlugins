using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Ft8.Dsp;
using SRdeckPlugin.Ft8.Models;
using SRdeckPlugin.Ft8.ViewModels;
using SRdeckPlugin.Ft8.Views;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;

namespace SRdeckPlugin.Ft8;

public sealed partial class Ft8PluginModule
{
    private void SelectBandFromView(string profileId)
    {
        if (profileId == selectedBandId ||
            Interlocked.Exchange(ref profileChangeInProgress, 1) != 0) return;
        Ft8Band previous = SelectedBand();
        Ft8Band requested = Bands.FirstOrDefault(item => item.Id == profileId) ?? previous;
        viewModel.SetBandChangeInProgress(true);
        viewModel.Status = $"選局中 / {requested.DisplayName}";
        _ = SelectBandFromViewAsync(profileId, previous);
    }

    private async Task SelectBandFromViewAsync(string profileId, Ft8Band previous)
    {
        try
        {
            // A profile selection updates SDR tuning and may need to wait for a
            // driver-owned processing lock. Never let that work run inside the
            // ComboBox change notification on WPF's UI thread.
            await Task.Run(async () =>
                await SelectProfileAsync(profileId, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            host?.Logger.Log(PluginLogLevel.Warning, "ft8.band.rejected",
                $"FT8 band change to '{profileId}' was rejected.", exception);
            host?.Dispatcher.Post(() =>
            {
                viewModel.RollbackBand(previous);
                viewModel.SetBandChangeInProgress(false);
            });
            SetStatus($"選局できません / {previous.DisplayName}");
        }
        finally
        {
            host?.Dispatcher.Post(() => viewModel.SetBandChangeInProgress(false));
            Volatile.Write(ref profileChangeInProgress, 0);
        }
    }

    private PluginTuningRequest CreateTuningRequest(Ft8Band band,
        IReadOnlyList<Ft8Band>? additionalBands = null)
    {
        Ft8Band[] requestedBands = new[] { band }
            .Concat(additionalBands ?? [])
            .Where(item => item.Mode == band.Mode)
            .DistinctBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        long halfBandwidth = Ft8Receiver.OccupiedPassbandHz / 2;
        long lower = requestedBands.Min(item => item.ChannelCenterFrequencyHz - halfBandwidth);
        long upper = requestedBands.Max(item => item.ChannelCenterFrequencyHz + halfBandwidth);
        long requiredSpan = upper - lower;
        int minimumSampleRate = (int)Math.Min(int.MaxValue,
            Math.Max(48_000L, (long)Math.Ceiling(requiredSpan / 0.95)));
        long preferredCenter = lower + requiredSpan / 2;
        return new PluginTuningRequest(
            band.Id,
            $"{band.Mode} {band.BandDisplayName}",
            requestedBands.Select(item =>
                new TuningTarget(item.ChannelCenterFrequencyHz, Ft8Receiver.OccupiedPassbandHz)).ToArray(),
            preferredCenter,
            minimumSampleRate,
            1,
            true,
            false,
            PluginGainPreference.Automatic);
    }

    private static PluginChannelRequest CreateChannelRequest(Ft8Band band) => new(
        CreateChannelRequestId(band), band.ChannelCenterFrequencyHz,
        Ft8Receiver.OccupiedPassbandHz, Ft8Receiver.OutputSampleRateHz,
        102_400, 51_200, 65, 3, 4, false,
        102_400, 409_600, 16);

    private static void ValidateTuningResult(PluginTuningResult result)
    {
        if (result.Outcome == PluginTuningOutcome.Rejected)
            throw new InvalidOperationException($"Weak-signal tuning was rejected: {result.Message}");
        if (result.Outcome == PluginTuningOutcome.Deferred)
            throw new InvalidOperationException($"Weak-signal tuning was deferred: {result.Message}");
        if (result.SampleRateHz < 12_800)
            throw new InvalidOperationException("FT8 requires at least 12.8 kS/s.");
    }

    private Ft8Band SelectedBand() =>
        Bands.FirstOrDefault(item => item.Id == selectedBandId) ??
        Bands.First(item => item.Id == DefaultBandId);

    private static string CreateChannelRequestId(Ft8Band band) =>
        band.Id.StartsWith("ft8-", StringComparison.Ordinal)
            ? band.Id
            : $"ft8-{band.Id}";

    private static TimeSpan TransmissionDuration(WeakSignalMode mode) => mode switch
    {
        WeakSignalMode.FT4 => TimeSpan.FromSeconds(103 * 0.048),
        WeakSignalMode.JT65 => TimeSpan.FromSeconds(126 * 4096.0 / 11025.0),
        _ => TimeSpan.FromSeconds(79 * 0.160)
    };

    private static int OccupiedSignalBandwidth(WeakSignalMode mode) => mode switch
    {
        WeakSignalMode.FT4 => 90,
        WeakSignalMode.JT65 => 178,
        _ => Ft8OccupiedBandwidthHz
    };

    private void OnTuningChanged(object? sender, PluginTuningResult result)
    {
        RefreshNearbyBandOptions(result, true);
        if (State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
            SetStatus($"{(State == PluginLifecycleState.Streaming ? "受信中" : "待機中")} / {SelectedBand().DisplayName}");
    }

    private Ft8Band[] ActiveBands()
    {
        Ft8Band selected = SelectedBand();
        return new[] { selected }.Concat(ConfiguredAdditionalBands(selected)).ToArray();
    }

    private Ft8Band[] ConfiguredAdditionalBands(Ft8Band selected)
    {
        string[] ids = Volatile.Read(ref enabledAdditionalBandIds);
        return ids.Select(id => Bands.FirstOrDefault(item => item.Id == id))
            .Where(item => item is not null && item.Mode == selected.Mode && item.Id != selected.Id)
            .Cast<Ft8Band>()
            .ToArray();
    }

    private void ApplyNearbyBandSelection(IReadOnlyList<string> selectedIds)
    {
        IPluginHostContext? context = host;
        if (context is null) return;
        Ft8Band selected = SelectedBand();
        HashSet<string> allowedIds = Ft8NearbyBandPolicy
            .FindCandidates(selected, context.Tuning.Current, Bands)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        string[] normalizedIds = selectedIds.Where(allowedIds.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] previousIds = Volatile.Read(ref enabledAdditionalBandIds);
        lock (processingGate)
        {
            Volatile.Write(ref enabledAdditionalBandIds, normalizedIds);
            settings = settings with { AdditionalBandIds = normalizedIds };
            foreach (string removedId in previousIds.Except(normalizedIds, StringComparer.Ordinal))
                if (additionalReceivers.TryGetValue(removedId, out Ft8Receiver? removedReceiver))
                    removedReceiver.Reset();
        }
        PersistSettings();
        FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshNearbyBandOptions(PluginTuningResult tuning, bool sanitizeSelections)
    {
        Ft8Band selected = SelectedBand();
        IReadOnlyList<Ft8Band> candidates = Ft8NearbyBandPolicy.FindCandidates(selected, tuning, Bands);
        HashSet<string> candidateIds = candidates.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        string[] selectedIds = Volatile.Read(ref enabledAdditionalBandIds);
        string[] validIds = selectedIds.Where(candidateIds.Contains).ToArray();
        if (sanitizeSelections && !selectedIds.SequenceEqual(validIds, StringComparer.Ordinal))
        {
            lock (processingGate)
            {
                Volatile.Write(ref enabledAdditionalBandIds, validIds);
                settings = settings with { AdditionalBandIds = validIds };
                foreach (string removedId in selectedIds.Except(validIds, StringComparer.Ordinal))
                    if (additionalReceivers.TryGetValue(removedId, out Ft8Receiver? removedReceiver))
                        removedReceiver.Reset();
            }
            PersistSettings();
            FrequencyOverlaysChanged?.Invoke(this, EventArgs.Empty);
        }
        host?.Dispatcher.Post(() => viewModel.UpdateNearbyBands(candidates, validIds));
    }

    private Ft8Receiver GetAdditionalReceiver(Ft8Band band)
    {
        if (additionalReceivers.TryGetValue(band.Id, out Ft8Receiver? existing)) return existing;
        var created = new Ft8Receiver();
        created.MessagesDecoded += OnMessagesDecoded;
        additionalReceivers.Add(band.Id, created);
        return created;
    }

    private void ResetAllReceivers()
    {
        receiver.Reset();
        foreach (Ft8Receiver additionalReceiver in additionalReceivers.Values)
            additionalReceiver.Reset();
    }

    private async Task DrainAllReceiversAsync(CancellationToken cancellationToken)
    {
        Ft8Receiver[] receivers;
        lock (processingGate)
            receivers = new[] { receiver }.Concat(additionalReceivers.Values).ToArray();
        await Task.WhenAll(receivers.Select(item => item.DrainAsync()))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
