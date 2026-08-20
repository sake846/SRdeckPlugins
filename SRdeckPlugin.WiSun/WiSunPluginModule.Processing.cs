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
    public async ValueTask<PluginExportResult> ExportAsync(
        PluginExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        WiSunPacketFrame[] frames;
        lock (_processingGate) frames = _packetHistory.ToArray();

        var filtered = frames.Where(f =>
            (request.From is null || f.Timestamp >= request.From) &&
            (request.To is null || f.Timestamp <= request.To)).ToArray();

        try
        {
            if (request.FormatId == "json")
            {
                string json = JsonSerializer.Serialize(filtered, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(request.DestinationPath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            else if (request.FormatId == "csv")
            {
                var sb = new StringBuilder("Timestamp,FrequencyHz,FrameType,Seq,PanId,SrcAddr,DstAddr,DurationMs,PeakDbfs,SnrDb,RawHex,Ascii\r\n");
                foreach (var f in filtered)
                {
                    sb.Append(f.Timestamp.ToString("O")).Append(',')
                      .Append(f.FrequencyHz).Append(',')
                      .Append(f.FrameType).Append(',')
                      .Append(f.SequenceNumber?.ToString() ?? "").Append(',')
                      .Append(f.PanId.HasValue ? f.PanId.Value.ToString("X4") : "").Append(',')
                      .Append(f.SrcAddress ?? "").Append(',')
                      .Append(f.DstAddress ?? "").Append(',')
                      .Append(f.DurationMs.ToString("F1")).Append(',')
                      .Append(f.PeakDbfs.ToString("F1")).Append(',')
                      .Append(f.SnrDb.ToString("F1")).Append(',')
                      .Append('"').Append(f.RawHexString).Append("\",")
                      .Append('"').Append(f.AsciiString.Replace("\"", "\"\"")).Append("\"\r\n");
                }
                await File.WriteAllTextAsync(request.DestinationPath, sb.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return new PluginExportResult(false, 0, $"未対応のエクスポート形式: '{request.FormatId}'");
            }

            return new PluginExportResult(true, filtered.Length, $"{filtered.Length} 件の復調パケットを保存しました: {request.DestinationPath}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PluginExportResult(false, 0, ex.Message);
        }
    }

    public async ValueTask UpdateSettingsAsync(
        WiSunSettings newSettings,
        CancellationToken cancellationToken = default)
    {
        await _settingsUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WiSunSettings normalized = newSettings.Normalize();
            bool channelChanged = normalized.PhyProfile != _settings.PhyProfile ||
                !normalized.FanChannels.SequenceEqual(_settings.FanChannels) ||
                !normalized.HanChannels.SequenceEqual(_settings.HanChannels) ||
                normalized.CustomFrequencyHz != _settings.CustomFrequencyHz ||
                normalized.CustomBitRateBps != _settings.CustomBitRateBps ||
                normalized.CustomSfdHex != _settings.CustomSfdHex;

            if (channelChanged &&
                State is PluginLifecycleState.Active or PluginLifecycleState.Streaming)
            {
                IPluginHostContext context = _hostContext ??
                    throw new InvalidOperationException("The Wi-SUN plugin is not initialized.");
                await RequestAndValidateTuningAsync(
                    context, TuningProfileId(normalized), normalized, cancellationToken)
                    .ConfigureAwait(false);
            }

            lock (_consumptionGate)
                lock (_processingGate)
                {
                    _settings = normalized;
                    string tuningProfileId = TuningProfileId(normalized);
                    _selectedProfileId = tuningProfileId == "custom"
                        ? null
                        : tuningProfileId;
                    if (channelChanged) RebuildDemodulators();
                    else
                    {
                        ushort? customSfd = ushort.TryParse(normalized.CustomSfdHex, System.Globalization.NumberStyles.HexNumber, null, out ushort sfdVal)
                            ? sfdVal
                            : null;
                        foreach (WiSunDemodulator demodulator in _demodulators.Values)
                        {
                            demodulator.CustomSfd = customSfd;
                            demodulator.EnableRawBurstLog = normalized.EnableRawBurstLog;
                        }
                    }
                }
            PersistSettings();
            if (channelChanged)
                SetStatus($"{(State == PluginLifecycleState.Streaming ? "受信中" : "待機中")} / " +
                    SelectedChannelSummary(normalized));
        }
        finally
        {
            _settingsUpdateGate.Release();
        }
    }


    private void HandlePacketDecoded(WiSunPacketFrame frame)
    {
        float peakDbm = _hostContext?.ReceiverTelemetry?.DbfsToDbm(frame.PeakDbfs)
            ?? (frame.PeakDbfs - 80f);
        WiSunPacketFrame enrichedFrame = frame with { PeakDbm = peakDbm };
        lock (_processingGate)
        {
            _packetHistory.Add(enrichedFrame);
            if (_packetHistory.Count > 10_000) _packetHistory.RemoveRange(0, _packetHistory.Count - 10_000);
        }
        PacketDecoded?.Invoke(enrichedFrame);
        _viewModel?.AddPacketFrame(enrichedFrame);
    }

    protected override ValueTask OnDisposeAsync(IPluginHostContext? hostContext)
    {
        lock (_consumptionGate)
            lock (_processingGate)
            {
                foreach (WiSunDemodulator demodulator in _demodulators.Values)
                    demodulator.ReleaseBuffers();
                _demodulators.Clear();
            }
        if (_hostContext is not null)
        {
            _hostContext.Tuning.AppliedConfigurationChanged -= OnTuningChanged;
            _hostContext = null;
        }
        return ValueTask.CompletedTask;
    }
}
