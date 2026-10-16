using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Meshtastic.Services;

internal readonly record struct MeshtasticDiagnosticInput(
    MeshtasticPerformanceState PerformanceState,
    bool IsTargetInPassband,
    string? WorkerError,
    string PerformanceDetails,
    DateTimeOffset? LastSuccess,
    DateTimeOffset? LastFailure,
    long PayloadFailureCount,
    long HeaderFailureCount,
    long HeaderCount,
    long SynchronizedCount,
    long PreambleCount,
    string LastFailureSummary);

internal enum MeshtasticDiagnosticState
{
    WorkerStopped,
    Overloaded,
    PerformanceWarning,
    OutOfPassband,
    WaitingForInput,
    RecentFailure,
    RecentSuccess,
    AwaitingPayload,
    AwaitingHeader,
    AcquiringSynchronization,
    WaitingForSignal
}

internal readonly record struct MeshtasticDiagnosticEvaluation(
    OverallStatusKind Kind, string Status, string Phase, string Summary, string Recommendation);

/// <summary>Pure diagnostic policy. The caller supplies one snapshot and the evaluation time.</summary>
internal static class MeshtasticDiagnosticEvaluator
{
    private static readonly TimeSpan RecentReceptionWindow = TimeSpan.FromSeconds(60);

    public static MeshtasticDiagnosticState EvaluateState(MeshtasticDiagnosticInput input, DateTimeOffset now)
    {
        if (input.WorkerError is not null) return MeshtasticDiagnosticState.WorkerStopped;
        if (input.PerformanceState == MeshtasticPerformanceState.Overloaded) return MeshtasticDiagnosticState.Overloaded;
        if (input.PerformanceState == MeshtasticPerformanceState.Warning) return MeshtasticDiagnosticState.PerformanceWarning;
        if (!input.IsTargetInPassband) return MeshtasticDiagnosticState.OutOfPassband;
        if (input.PerformanceState is MeshtasticPerformanceState.Idle or MeshtasticPerformanceState.WarmingUp)
            return MeshtasticDiagnosticState.WaitingForInput;
        if (input.LastFailure is DateTimeOffset failedAt && now - failedAt <= RecentReceptionWindow &&
            (input.LastSuccess is null || failedAt > input.LastSuccess.Value))
            return MeshtasticDiagnosticState.RecentFailure;
        if (input.LastSuccess is DateTimeOffset succeededAt && now - succeededAt <= RecentReceptionWindow)
            return MeshtasticDiagnosticState.RecentSuccess;
        if (input.HeaderCount > 0) return MeshtasticDiagnosticState.AwaitingPayload;
        if (input.SynchronizedCount > 0) return MeshtasticDiagnosticState.AwaitingHeader;
        if (input.PreambleCount > 0) return MeshtasticDiagnosticState.AcquiringSynchronization;
        return MeshtasticDiagnosticState.WaitingForSignal;
    }

    public static MeshtasticDiagnosticEvaluation Evaluate(MeshtasticDiagnosticInput input, DateTimeOffset now) =>
        Describe(EvaluateState(input, now), input);

    private static MeshtasticDiagnosticEvaluation Describe(MeshtasticDiagnosticState state, MeshtasticDiagnosticInput input) => state switch
    {
        MeshtasticDiagnosticState.WorkerStopped => new(OverallStatusKind.Error, "受信停止", "受信ワーカー",
            input.WorkerError!, "確認: エラー記録を確認し、受信を停止して再開してください"),
        MeshtasticDiagnosticState.Overloaded => new(OverallStatusKind.Critical, "過負荷", "リアルタイム処理",
            input.PerformanceDetails, "確認: 探索プリセット数、スロット数、または入力サンプルレートを減らしてください"),
        MeshtasticDiagnosticState.PerformanceWarning => new(OverallStatusKind.Warning, "注意", "リアルタイム処理",
            input.PerformanceDetails, "確認: 遅延IQの保持残り時間と入力サンプルレートを確認してください"),
        MeshtasticDiagnosticState.OutOfPassband => new(OverallStatusKind.Warning, "要確認", "入力・選局",
            "Meshtastic対象周波数が受信帯域外です", "確認: 地域、slot、中心周波数、入力帯域を確認してください"),
        MeshtasticDiagnosticState.WaitingForInput => new(OverallStatusKind.Idle, "入力待ち", "入力",
            "IQ入力またはリアルタイム測定の開始を待っています", "確認: SDR接続と受信開始状態を確認してください"),
        MeshtasticDiagnosticState.RecentFailure => new(OverallStatusKind.Warning, "要確認",
            input.PayloadFailureCount > 0 ? "検証・復号" : input.HeaderFailureCount > 0 ? "ヘッダー" : "同期",
            input.LastFailureSummary, "確認: 信号margin、slot/PHY設定、失敗段階の件数を確認してください"),
        MeshtasticDiagnosticState.RecentSuccess => new(OverallStatusKind.Success, "正常", "検証・復号",
            "直近60秒にMeshtastic payloadを正常に検証・復号しています", "確認: 受信処理は正常に動作しています"),
        MeshtasticDiagnosticState.AwaitingPayload => new(OverallStatusKind.Running, "監視中", "payload",
            "LoRa headerを取得し、payload完了を待っています", "確認: payload失敗とCRC内訳を確認しながら継続監視してください"),
        MeshtasticDiagnosticState.AwaitingHeader => new(OverallStatusKind.Running, "監視中", "header",
            "LoRa同期が成立し、headerを探索しています", "確認: header失敗と選択PHYを確認してください"),
        MeshtasticDiagnosticState.AcquiringSynchronization => new(OverallStatusKind.Running, "監視中", "同期",
            "LoRa preambleを検出し、同期を継続しています", "確認: 信号marginとslot設定を確認してください"),
        MeshtasticDiagnosticState.WaitingForSignal => new(OverallStatusKind.Running, "監視中", "信号待機",
            "IQ入力は正常でMeshtastic信号を監視しています", "確認: 対象slotと周辺トラフィックを確認してください"),
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}
