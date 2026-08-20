using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

using SRdeckPlugin.Contracts;
using SRdeckPlugin.Wpf;
using SRdeckPlugin.WiSun.Dsp;
using SRdeckPlugin.WiSun.Models;

namespace SRdeckPlugin.WiSun.ViewModels;

public sealed partial class WiSunViewModel
{
    private void UpdateDiagnosticWindow(DateTimeOffset now)
    {
        if (_diagnosticWindow.TryPeek(out DiagnosticWindowSample first) &&
            (TotalRfBursts < first.RfBursts || TotalPreambleMatches < first.Preambles ||
             TotalSfdMatches < first.SfdMatches || TotalCrcOk < first.CrcOk || TotalCrcNg < first.CrcNg))
        {
            _diagnosticWindow.Clear();
            _lastCrcOkAt = null;
        }
        DiagnosticWindowSample? previous = _diagnosticWindow.Count == 0 ? null : _diagnosticWindow.Last();
        if ((previous is DiagnosticWindowSample last && TotalCrcOk > last.CrcOk) ||
            (previous is null && TotalCrcOk > 0))
            _lastCrcOkAt = now;
        _diagnosticWindow.Enqueue(new(now, TotalRfBursts, TotalPreambleMatches,
            TotalSfdMatches, TotalCrcOk, TotalCrcNg));
        while (_diagnosticWindow.TryPeek(out first) && now - first.MeasuredAt > TimeSpan.FromSeconds(60))
            _diagnosticWindow.Dequeue();
    }

    private DiagnosticEvaluation EvaluateDiagnostic()
    {
        PluginRuntimeDiagnosticsSnapshot runtime;
        try { runtime = RuntimeDiagnostics.GetSnapshot(); }
        catch { runtime = default; }
        DateTimeOffset now = DateTimeOffset.Now;
        DiagnosticWindowSample oldest = _diagnosticWindow.TryPeek(out DiagnosticWindowSample value)
            ? value : default;
        long bursts = Math.Max(0, TotalRfBursts - oldest.RfBursts);
        long preambles = Math.Max(0, TotalPreambleMatches - oldest.Preambles);
        long sfds = Math.Max(0, TotalSfdMatches - oldest.SfdMatches);
        long crcOk = Math.Max(0, TotalCrcOk - oldest.CrcOk);
        long crcNg = Math.Max(0, TotalCrcNg - oldest.CrcNg);

        if (!IsReceiverEnabled)
            return new(OverallStatusKind.Idle, "停止中", "入力", "Wi-SUN復調器は停止しています", "確認: 受信を有効にしてください");
        if (!string.IsNullOrWhiteSpace(runtime.LastError))
            return new(OverallStatusKind.Error, "エラー", "リアルタイム処理", "ホストのIQ配送処理でエラーが発生しました", "確認: リアルタイム処理の最終エラーを確認してください");
        if (_module.DiagnosticLastMeasuredAt is not DateTimeOffset && runtime.ProcessedBlocks == 0)
            return new(OverallStatusKind.Idle, "入力待ち", "入力", "IQ入力を待機しています", "確認: SDR接続と受信開始状態を確認してください");
        if (_module.DiagnosticLastMeasuredAt is DateTimeOffset lastMeasured && now - lastMeasured > TimeSpan.FromSeconds(3))
            return new(OverallStatusKind.Warning, "要確認", "更新停止", "Wi-SUN診断スナップショットの更新が停止しています", "確認: IQ配送とプラグインのライフサイクル状態を確認してください");
        if (crcNg > crcOk && crcNg >= 3)
            return new(OverallStatusKind.Warning, "要確認", "検証・復号", "直近60秒はCRC不一致が正常フレームを上回っています", "確認: 信号レベル、選局チャネル、同期品質を確認してください");
        if (crcOk > 0 || (_lastCrcOkAt is DateTimeOffset lastOk && now - lastOk <= TimeSpan.FromSeconds(60)))
            return new(OverallStatusKind.Success, "正常", "検証・復号", "直近60秒にWi-SUNフレームを正常に同期・検証しています", "確認: 受信処理は正常に動作しています");
        if (sfds > 0)
            return new(OverallStatusKind.Warning, "要確認", "検証・復号", "SFD同期後のPHRまたはCRC検証が成立していません", "確認: PHR長、FCS種別、CRC不一致数を確認してください");
        if (preambles > 0)
            return new(OverallStatusKind.Warning, "要確認", "SFD同期", "プリアンブル一致後にSFD同期が成立していません", "確認: clock誤差、SFD設定、信号品質を確認してください");
        if (bursts > 0)
            return new(OverallStatusKind.Running, "監視中", "検出", "RFバーストを検出し、プリアンブルを探索しています", "確認: 選択PHYとsquelch閾値を確認してください");
        return new(OverallStatusKind.Running, "監視中", "信号待機", "IQ入力は正常でWi-SUN信号を監視しています", "確認: 対象チャネルと周辺トラフィックを確認してください");
    }

    private readonly record struct DiagnosticWindowSample(DateTimeOffset MeasuredAt,
        long RfBursts, long Preambles, long SfdMatches, long CrcOk, long CrcNg);
    private readonly record struct DiagnosticEvaluation(OverallStatusKind Kind, string Status,
        string Phase, string Summary, string Recommendation);

    private void QueueDiagnosticRefresh()
    {
        if (Interlocked.Exchange(ref _diagnosticRefreshQueued, 1) != 0) return;
        DispatchToOwner(() =>
        {
            Interlocked.Exchange(ref _diagnosticRefreshQueued, 0);
            RefreshDiagnosticProperties();
        });
    }


    public double FrequencyMhz
    {
        get => _frequencyMhz;
        set
        {
            if (Math.Abs(_frequencyMhz - value) > 1e-6)
            {
                _frequencyMhz = Math.Clamp(value, 920.0, 928.0);
                OnPropertyChanged();
                OnPropertyChanged(nameof(FrequencyHzText));
                ApplySettings();
            }
        }
    }

    public double FrequencyStepMhz
    {
        get => _frequencyStepMhz;
        set
        {
            if (Math.Abs(_frequencyStepMhz - value) > 1e-6)
            {
                _frequencyStepMhz = Math.Clamp(value, 0.001, 2.0);
                OnPropertyChanged();
                ApplySettings();
            }
        }
    }

    public float SquelchThresholdDbm
    {
        get => _squelchThresholdDbm;
        set
        {
            if (Math.Abs(_squelchThresholdDbm - value) > 0.1f)
            {
                _squelchThresholdDbm = Math.Clamp(value, -160.0f, 0.0f);
                OnPropertyChanged();
                OnPropertyChanged(nameof(SquelchThresholdDbfs));
                ApplySettings();
            }
        }
    }

    public float SquelchThresholdDbfs
    {
        get => SquelchThresholdDbm;
        set => SquelchThresholdDbm = value;
    }

    partial void OnIsReceiverEnabledChanged(bool value)
    {
        StatusText = value ? "Wi-SUN 復調動作中" : "受信停止中";
        ApplySettings();
    }

    public OverallStatusKind ReceiverStatusKind =>
        IsReceiverEnabled ? OverallStatusKind.Running : OverallStatusKind.Idle;

    public int PacketCount => Packets.Count;
}
