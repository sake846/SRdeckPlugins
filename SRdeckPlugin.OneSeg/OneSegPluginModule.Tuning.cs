using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.OneSeg.Dsp;
using SRdeckPlugin.OneSeg.Media;
using SRdeckPlugin.OneSeg.Models;
using SRdeckPlugin.OneSeg.Transport;
using SRdeckPlugin.OneSeg.ViewModels;
using SRdeckPlugin.OneSeg.Views;
using SRdeckPlugin.Sdk;
using SRdeckPlugin.Wpf;


namespace SRdeckPlugin.OneSeg;

public sealed partial class OneSegPluginModule
{
    private OneSegPrefecture SelectedPrefecture() => OneSegPrefecture.Find(settings.PrefectureId);
    private OneSegChannel SelectedChannel() => OneSegChannel.JapaneseUhfChannels
        .FirstOrDefault(item => item.Number == settings.Channel) ?? SelectedPrefecture().CandidateChannels[0];
    private IReadOnlyList<OneSegChannel> ProfileChannels()
    {
        OneSegChannel selected = SelectedChannel();
        return SelectedPrefecture().CandidateChannels.Append(selected)
            .DistinctBy(channel => channel.Number).ToArray();
    }
    private static PluginTuningRequest CreateTuningRequest(OneSegChannel channel) => new(channel.Id,
        $"ワンセグ {channel.DisplayName}", [new TuningTarget(channel.CenterFrequencyHz, 500_000)],
        channel.CenterFrequencyHz, 1_015_873, 1, true, false, PluginGainPreference.Automatic);
    private static void ValidateTuning(PluginTuningResult result)
    {
        if (result.Outcome is PluginTuningOutcome.Rejected or PluginTuningOutcome.Deferred)
            throw new InvalidOperationException($"OneSeg tuning failed: {result.Message}");
        if (result.SampleRateHz < 1_015_873) throw new InvalidOperationException("OneSeg reception requires at least 1.016 MS/s.");
    }

    private async Task ChangeChannelFromViewAsync(OneSegChannel channel)
    {
        try { await Task.Run(async () => await SelectProfileAsync(channel.Id, CancellationToken.None)); }
        catch (Exception exception)
        {
            host?.Logger.Log(PluginLogLevel.Warning, "oneseg.channel.change", "OneSeg channel selection failed.", exception);
            Dispatch(() =>
            {
                viewModel.Configure(settings, SelectedChannel());
                viewModel.Status = $"選局失敗: {exception.Message}";
            });
        }
    }

    private async Task ChangePrefectureFromViewAsync(OneSegPrefecture prefecture, OneSegChannel channel)
    {
        try { await Task.Run(async () => await ApplySelectionAsync(prefecture, channel, CancellationToken.None)); }
        catch (Exception exception)
        {
            host?.Logger.Log(PluginLogLevel.Warning, "oneseg.prefecture.change", "OneSeg prefecture selection failed.", exception);
            Dispatch(() =>
            {
                viewModel.Configure(settings, SelectedChannel());
                viewModel.Status = $"地域変更失敗: {exception.Message}";
            });
        }
    }

    private async Task ChangeStationFromViewAsync(OneSegStation station)
    {
        try
        {
            OneSegPrefecture prefecture = SelectedPrefecture();
            OneSegChannel channel = OneSegChannel.JapaneseUhfChannels.FirstOrDefault(item =>
                item.Number == station.ChannelNumber) ?? throw new InvalidOperationException(
                $"{station.ServiceName} の物理チャンネルは現在の地域候補にありません。");
            settings = settings with { LastServiceId = station.ServiceId };
            await Task.Run(async () => await ApplySelectionAsync(
                prefecture, channel, CancellationToken.None, forceRestart: true));
        }
        catch (Exception exception)
        {
            host?.Logger.Log(PluginLogLevel.Warning, "oneseg.station.change",
                "OneSeg station selection failed.", exception);
            Dispatch(() => viewModel.Status = $"放送局選択失敗: {exception.Message}");
        }
    }

    private async Task ScanChannelsFromViewAsync(bool isAppend = false)
    {
        if (State != PluginLifecycleState.Streaming || host is null)
        {
            Dispatch(() => viewModel.ScanStatus = "チャンネルサーチは受信中に実行してください");
            return;
        }
        IPluginHostContext context = host;
        if (Interlocked.CompareExchange(ref channelScanInProgress, 1, 0) != 0) return;

        var cancellation = new CancellationTokenSource();
        channelScanCancellation = cancellation;
        OneSegPrefecture prefecture = SelectedPrefecture();
        OneSegChannel original = SelectedChannel();
        IReadOnlyList<OneSegChannel> channels = OneSegChannel.JapaneseUhfChannels;
        var stations = new List<OneSegStation>();
        bool restored = false;
        try
        {
            Dispatch(() => viewModel.BeginScan(channels.Count, isAppend));
            lock (processingGate)
            {
                mediaPlayer?.Stop();
                receiver.Reset();
                analyzer.Reset();
                iqCaptureBuffer.Reset();
            }

            for (int index = 0; index < channels.Count; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                OneSegChannel channel = channels[index];
                int progress = index + 1;
                ScanChannelFrequencyHz = channel.CenterFrequencyHz;
                Dispatch(() => viewModel.ReportScanProgress(progress, channels.Count, channel));
                try
                {
                    ValidateTuning(await context.Tuning.RequestAsync(
                        CreateTuningRequest(channel), cancellation.Token).ConfigureAwait(false));
                    await Task.Delay(300, cancellation.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    context.Logger.Log(PluginLogLevel.Warning, "oneseg.channel-scan.tune",
                        $"Could not tune {channel.DisplayName} while scanning.", exception);
                    continue;
                }

                lock (processingGate)
                {
                    receiver.Reset();
                    analyzer.Reset();
                    iqCaptureBuffer.Reset();
                }
                OneSegStation? station = await WaitForScannedStationAsync(
                    channel, progress, channels.Count, cancellation.Token).ConfigureAwait(false);
                if (station is null) continue;
                stations.Add(station);
                Dispatch(() => viewModel.AddScannedStation(station));
            }

            restored = true;
        }
        catch (OperationCanceledException)
        {
            restored = false;
        }
        catch (Exception exception)
        {
            context.Logger.Log(PluginLogLevel.Warning, "oneseg.channel-scan.failed",
                "OneSeg channel scan failed.", exception);
            Dispatch(() => viewModel.Status = $"チャンネルサーチ失敗: {exception.Message}");
        }
        finally
        {
            try
            {
                if (!IsDisposed)
                {
                    ScanChannelFrequencyHz = 0;
                    ValidateTuning(await context.Tuning.RequestAsync(
                        CreateTuningRequest(original), CancellationToken.None).ConfigureAwait(false));
                    lock (processingGate)
                    {
                        receiver.Reset();
                        analyzer.Reset();
                        iqCaptureBuffer.Reset();
                        Dispatch(() =>
                        {
                            viewModel.ResetServiceData();
                            viewModel.Configure(settings, original);
                        });
                        Interlocked.Exchange(ref channelScanInProgress, 0);
                        if (State == PluginLifecycleState.Streaming) mediaPlayer?.Start(settings.Volume);
                    }
                    SetStatus($"{(State == PluginLifecycleState.Streaming ? "受信中" : "待機中")} / " +
                        $"{prefecture.DisplayName} / {original.DisplayName}");
                }
            }
            catch (Exception exception)
            {
                restored = false;
                context.Logger.Log(PluginLogLevel.Warning, "oneseg.channel-scan.restore",
                    "Could not restore the channel after scanning.", exception);
            }
            ScanChannelFrequencyHz = 0;
            channelScanCancellation?.Dispose();
            channelScanCancellation = null;
            Interlocked.Exchange(ref channelScanInProgress, 0);
            bool completed = restored;
            if (completed)
            {
                IReadOnlyList<OneSegStation> finalStations = isAppend
                    ? MergeStations(settings.ScannedStations ?? [], stations)
                    : stations.ToArray();
                settings = settings with { ScannedStations = finalStations };
                PersistSettings();
            }
            Dispatch(() => viewModel.EndScan(settings.ScannedStations ?? [], original.Number, completed, settings.LastServiceId));
        }
    }

    private static IReadOnlyList<OneSegStation> MergeStations(
        IEnumerable<OneSegStation> existingStations,
        IEnumerable<OneSegStation> newStations)
    {
        var map = existingStations.ToDictionary(s => (s.ServiceId, s.ChannelNumber));
        foreach (OneSegStation station in newStations)
        {
            var key = (station.ServiceId, station.ChannelNumber);
            if (map.TryGetValue(key, out OneSegStation? existing))
            {
                map[key] = existing.UpdateInfo(
                    serviceName: station.ServiceName,
                    providerName: station.ProviderName,
                    remoteControlKeyId: station.RemoteControlKeyId,
                    merDb: station.MerDb,
                    transportPackets: station.TransportPackets,
                    reedSolomonPackets: station.ReedSolomonPackets,
                    reedSolomonRejected: station.ReedSolomonRejected,
                    scannedAt: station.ScannedAt ?? DateTimeOffset.Now);
            }
            else
            {
                map[key] = station;
            }
        }
        return map.Values.OrderBy(item => item.RemoteControlKeyId ?? byte.MaxValue)
            .ThenBy(item => item.ChannelNumber)
            .ThenBy(item => item.ServiceId)
            .ThenBy(item => item.ServiceName)
            .ToArray();
    }

    private void UpdateStationInfoFromNetwork(OneSegNetworkInfo network)
    {
        bool updatedAny = false;
        var currentSettingsStations = (settings.ScannedStations ?? []).ToList();

        foreach (OneSegTransportInfo transport in network.Transports)
        {
            if (!transport.RemoteControlKeyId.HasValue) continue;
            byte keyId = transport.RemoteControlKeyId.Value;

            foreach (OneSegNetworkService service in transport.Services)
            {
                for (int i = 0; i < viewModel.Stations.Count; i++)
                {
                    OneSegStation station = viewModel.Stations[i];
                    if (station.ServiceId == service.ServiceId && station.ChannelNumber == settings.Channel)
                    {
                        if (station.RemoteControlKeyId != keyId)
                        {
                            OneSegStation updated = station.UpdateInfo(remoteControlKeyId: keyId);
                            viewModel.Stations[i] = updated;
                            if (viewModel.SelectedStation?.ServiceId == updated.ServiceId && viewModel.SelectedStation?.ChannelNumber == updated.ChannelNumber)
                            {
                                viewModel.UpdateSelectedStationSilently(updated);
                            }
                            updatedAny = true;
                        }
                    }
                }

                for (int i = 0; i < currentSettingsStations.Count; i++)
                {
                    OneSegStation station = currentSettingsStations[i];
                    if (station.ServiceId == service.ServiceId && station.ChannelNumber == settings.Channel)
                    {
                        if (station.RemoteControlKeyId != keyId)
                        {
                            currentSettingsStations[i] = station.UpdateInfo(remoteControlKeyId: keyId);
                            updatedAny = true;
                        }
                    }
                }
            }
        }

        if (updatedAny)
        {
            settings = settings with { ScannedStations = currentSettingsStations.ToArray() };
            PersistSettings();
        }
    }

    private async Task<OneSegStation?> WaitForScannedStationAsync(
        OneSegChannel channel, int progress, int total, CancellationToken cancellationToken)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        OneSegProgramInfo? fallback = null;
        bool sawOfdmLock = false;
        bool sawTmccLock = false;
        double merTotal = 0;
        int merSamples = 0;
        OneSegDiagnostics latestDiagnostics = OneSegDiagnostics.Empty;
        int polls = 0;
        while (DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            OneSegDiagnostics diagnostics;
            OneSegProgramInfo? program;
            long packets;
            lock (processingGate)
            {
                diagnostics = receiver.Diagnostics;
                latestDiagnostics = diagnostics;
                sawOfdmLock |= diagnostics.OfdmLocked;
                sawTmccLock |= diagnostics.TmccLocked;
                if (diagnostics.OfdmLocked && double.IsFinite(diagnostics.MerDb))
                {
                    merTotal += diagnostics.MerDb;
                    merSamples++;
                }
                packets = analyzer.PacketCount;
                fallback = analyzer.Programs.FirstOrDefault(item => item.PmtPid == 0x1fc8 &&
                    item.Streams.Count > 0) ?? fallback;
                program = fallback is not null &&
                    !string.IsNullOrWhiteSpace(fallback.ServiceName) &&
                    !fallback.ServiceName.StartsWith("Service ", StringComparison.Ordinal)
                    ? fallback : null;
            }
            if (program is not null)
                return CreateScannedStation(program, channel, latestDiagnostics,
                    merSamples > 0 ? merTotal / merSamples : null, packets);

            TimeSpan elapsed = DateTimeOffset.UtcNow - started;
            if (++polls % 5 == 0)
            {
                string reception = $"OFDM {(diagnostics.OfdmLocked ? "LOCK" : "--")} / " +
                    $"TMCC {(diagnostics.TmccLocked ? "LOCK" : "--")} / TS {packets:N0}";
                Dispatch(() => viewModel.ReportScanProgress(progress, total, channel, reception));
            }
            if (elapsed >= TimeSpan.FromSeconds(2) && !sawOfdmLock) return null;
            if (elapsed >= TimeSpan.FromSeconds(5) && !sawTmccLock && fallback is null) return null;
        }
        if (fallback is null) return null;
        OneSegProgramInfo result = fallback.ServiceName.StartsWith("Service ", StringComparison.Ordinal)
            ? fallback with { ServiceName = $"受信局 (Service {fallback.ServiceId})" } : fallback;
        return CreateScannedStation(result, channel, latestDiagnostics,
            merSamples > 0 ? merTotal / merSamples : null, analyzer.PacketCount);
    }

    private OneSegStation CreateScannedStation(OneSegProgramInfo program, OneSegChannel channel,
        OneSegDiagnostics diagnostics, double? averageMerDb, long packets)
    {
        OneSegStation? previous = (settings.ScannedStations ?? []).FirstOrDefault(item =>
            item.ServiceId == program.ServiceId && item.ChannelNumber == channel.Number);
        byte? remoteControlKeyId = analyzer.NetworkInfo?.Transports
            .Where(transport => transport.Services.Any(service => service.ServiceId == program.ServiceId))
            .Select(transport => transport.RemoteControlKeyId)
            .FirstOrDefault(value => value.HasValue) ?? previous?.RemoteControlKeyId;
        return new OneSegStation(program.ServiceId, program.ServiceName, program.ProviderName, channel.Number,
            averageMerDb, packets, diagnostics.ReedSolomonPackets,
            diagnostics.ReedSolomonRejected, DateTimeOffset.Now)
        {
            RemoteControlKeyId = remoteControlKeyId
        };
    }

    private void PersistSettings()
    {
        RefreshRecordingSnapshot();
        IPluginHostContext? context = host;
        if (context is null) return;
        OneSegSettings snapshot = settings.Normalize();
        _ = Task.Run(async () =>
        {
            try { await context.Settings.SaveAsync(new(2, JsonSerializer.Serialize(snapshot))).ConfigureAwait(false); }
            catch (Exception exception) { context.Logger.Log(PluginLogLevel.Warning, "oneseg.settings.save", "OneSeg settings could not be saved.", exception); }
        });
    }

    private void SetStatus(string value) => Dispatch(() => viewModel.Status = value);
    private void Dispatch(Action action)
    {
        IPluginHostContext? context = host;
        if (context is null || context.Dispatcher.CheckAccess()) action(); else context.Dispatcher.Post(action);
    }
}
