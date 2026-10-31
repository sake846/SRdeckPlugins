# 活動ヒートマップ・共有スペクトラムの検証

2026-10-31 / Windows x64 / .NET SDK 10.0.403 / Release。

ヒートマップ向けに受信機テレメトリへ追加していた取得メソッドを削除し、汎用の `IPluginSpectrumService` へ分離しました。共有契約は範囲・点数・集約・座標・時刻・校正差を扱い、活動率や履歴はプラグインが所有します。ヒートマップにはズーム・パン後のスペクトラム表示範囲への追従を追加しています。

## 要求と試験

| 観点 | 登録名 | 入力と期待値 |
|---|---|---|
| 汎用サービスの範囲・集約 | `Shared spectrum service ranges and aggregation` | ヒートマップを使わずサービス単体を検査。4,000 S/s、中心1 MHzの4ビンから全帯域・2 kHz表示帯域・任意Hz範囲を取得。ピークと線形電力平均の違い、dBm校正差5 dB、座標、入力条件、元ビン数、公開時刻を確認。 |
| 共有データ・検証・互換性 | 同上 | 同じ問い合わせは不変スナップショットを再利用。パン・表示拡大で取得範囲外はNaN。保持済みデータは次のFFTでも変わらない。不正な点数・列挙値・範囲を拒否。未対応サービスは既定のNullサービスで未観測。 |
| 表示範囲追従 | `Activity heatmap follows spectrum zoom and pan` | 6 MS/s、入力中心922 MHz、表示幅5.25 MHzで919.375〜924.625 MHz。パンと1 MHz→250 kHzズームを同じFrameIdのまま追従し、旧周波数列の履歴を消去。初回FFTが遅れても古い座標・公開時計を残さない。 |
| 全入力・帯域外 | 同上 | 全入力へ切り替えると919〜925 MHzになり、その後の画面パンで履歴を消さない。8 MHz表示では918〜926 MHzの座標を維持し、範囲外セルのみNaN、観測回数0。捕捉した中間セルは通常集計。 |
| 実FFTとdBmの一致 | `Activity heatmap spectrum calibration parity` | ホストの実CPU FFTで6 MS/sの固定seed雑音を4096点・32768点で計算。雑音ピーク−150 dBm・しきい値−130 dBmで全セル0%。強い実トーンは該当セル100%、離れたセル0%。異なる入力条件・未初期化FFTは除外。 |
| 活動率・履歴・欠測 | `Activity heatmap frequency occupancy and bounded history` | 20フレーム中10回の信号を50%とし、しきい値一致を活動とする。1,444分から最後の24時間を保持。集約11/21、途中2分の欠測、セル単位の無効値、最新が上を確認。 |
| ライフサイクル・描画 | `Activity heatmap lifecycle continuity and views` | 重複FFT除外、二重校正の禁止、期間変更・停止中の操作・再開・欠落・遅延UI更新・キャンセル・未対応サービスを検査。700×600で描画し、最新の活動色が上、未観測が下であることとセル詳細の単位を確認。 |
| 非同期FFT・既存契約 | 関連Main FFT、テレメトリ、プラグイン集合・カタログ・参照構造・ウォームアップ試験 | 完了したFFTの入力レート、中心周波数、公開時刻、バッファ寿命を確認。既存の受信レベル通知やプラグイン探索の互換性も確認。 |

新しい試験は既存 `RegressionTests` 部分クラスとカタログへ登録しています。サービス単体の試験は `RegressionTests.Core.SpectrumService.cs`、表示追従は `RegressionTests.ActivityHeatmap.DisplayRange.cs` です。汎用契約は [共有スペクトラムサービス](../docs/contracts/spectrum-service-contract.md) を参照してください。

## 実行証拠

- プラグイン・Contracts・SDK・Wpf: `temp/spectrum-service-plugin-build.log`
- 本体: `temp/spectrum-service-host-build.log`
- テストのビルド: `temp/spectrum-service-tests-build.log`
- テスト実行: `temp/spectrum-service-tests.log`、`SRdeck.Tests/TestResults/spectrum-service.trx`
- 700×600の画面確認: `temp/activity-heatmap-preview-6h.png`、`-24h.png`。範囲セレクター・狭い周波数範囲の横軸・日付・凡例・説明の重なりがないことを確認。

試験の時間・GPU関連環境変数は未指定。VSTestのローカル接続制約を避けてサンドボックス外で実行します。変更対象の依存を先にビルドし、本体・テストでは既存参照を再利用します。

最終結果: 18件すべてPassed、Failed/Skippedは0件。プラグイン・本体・テストのReleaseビルドは警告・エラー0件です。汎用サービスを独立した試験で検査し、表示範囲と全入力の切り替え、FFT公開待ちからの復帰、帯域外のセル単位の欠測も確認しました。

```powershell
dotnet build SRdeckPlugin.ActivityHeatmap\SRdeckPlugin.ActivityHeatmap.csproj -c Release --no-restore -m:1
dotnet build SRdeck\SRdeck.csproj -c Release --no-restore -m:1 -p:BuildProjectReferences=false
dotnet build SRdeck.Tests\SRdeck.Tests.csproj -c Release --no-restore -m:1 -p:BuildProjectReferences=false
dotnet test SRdeck.Tests\SRdeck.Tests.csproj -c Release --no-build --no-restore -m:1 --filter "DisplayName~Activity heatmap|DisplayName~Shared spectrum service|DisplayName~Main FFT|DisplayName=Plugin receiver telemetry uses band power|DisplayName=Built-in plugin set|DisplayName=Regression catalog completeness|DisplayName=Phase 4 project reference architecture|DisplayName=Built-in plugin processing warm-up" --logger "trx;LogFileName=spectrum-service.trx"
```

## 運用上の範囲

RSP1B実機での操作、24時間の実受信、校正精度の実測は未実施です。CPU FFTの校正一致と共有バッファ上の範囲操作を自動検査しています。絶対dBm精度は受信機校正に依存し、FFT更新間隔より短い信号を見逃す場合があります。
