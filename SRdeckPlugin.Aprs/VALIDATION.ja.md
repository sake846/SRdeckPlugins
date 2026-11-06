# APRS数値解析・グラフの検証記録

対象: `b2643eea`を基点とする2026-10-29の作業差分。要求はMic-E位置・気象・テレメトリの解析と時系列グラフ表示。
方式の根拠は[APRS101 1.0.1](https://www.aprs.org/doc/APRS101.PDF)の10・12・13章。
作成・コード照合: Codex。実機・対話的GUIの受入は未実行。

## 対象と観点

`rg`でAprsParser／AprsPacketの利用側、画面、保存・結果発行、テスト登録を確認。
既存のContracts／SDK／WPFと有界履歴を使用し、新規依存・能力・設定・プロファイルは追加しない。

| 回帰試験仕様 | 適用と理由 |
|---|---|
| 4.1 登録・互換性 | 適用。既存パケットへの任意フィールド追加、新規テストの登録確認。 |
| 4.2 ライフサイクル | 適用。再起動時に旧本文を再解析し、停止・再開の既存試験も実行。 |
| 4.3 IQ配送・4.4 同調 | 実装変更なし。既存の合成IQ、境界、拒否・保留・再開試験で保護。 |
| 4.5 保存・Export | 適用。JSONの構造、CSVの単位付き列、旧JSONL復元、TNC2の本文保持。 |
| 4.6 結果 | 適用。合成IQで受信した気象数値がDetailsJsonへ届くことを確認。音声・通知は追加なし。 |
| 4.7 UI・地図 | 適用。項目選択・表示・値表・描画・保持上限と、既存の地図投影を確認。 |
| 4.8 方式固有処理 | 適用。既知例、単位、欠測、不正値、切断、ランダム入力。 |
| 4.9 ホスト | 方式固有分岐の追加なし。結果・標準チャネル・共通地図の既存契約を利用。 |
| 4.10 ランナー・配布 | テストカタログとソースリンクを更新。配布・インストールは変更なし。 |
| 5 性能 | DSPの既存定常割り当て試験、グラフの履歴上限を確認。最大10,000件のUI応答時間は未測定。 |
| 6 受入 | 実波・手動操作・DPI別の実機確認は未実行。 |

## 入力と期待値

正本は`tests/Aprs/AprsExtendedProtocolTests.cs`と`SRdeck.Tests/RegressionTests.Aprs.cs`のアサート。
新規テストは既存の所有クラスへ追加し、`RegressionTestCatalog.cs`から登録する。

| 登録名 | 区分 | 固定入力と主な期待値 |
|---|---|---|
| APRS Mic-E positions and channel values | U | APRS101例の112°7.74′W、20 kn、251°。南北・東西、経度0/9/10/99/100/109/110/179°、曖昧度1～4桁、2種類の速度符号化。 |
| APRS weather fields and units | U | 77°F→25°C、-1°F、1/100 inch→0.254 mm、風速のmph／kn変換、h00→100%、b10132→1013.2 hPa、欠測と真の0の区別。 |
| APRS telemetry values and malformed reports | U | 連番005、アナログ199/0/255/73/123、bit順01101001。範囲外256、切断、不正bitを拒否。seed101の4,000入力とJSON往復。 |
| APRS numeric graphs and selection | C/I | UnixEpoch基準の順不同受信、0と25°Cの時刻順表示、局・所有者分離、WPF選択保持、100件上限と消失、空／1点／同時刻／2日幅の描画。 |
| APRS numeric history and exports | C/I | 旧形式の生本文2件をJSONLから復元し、気象・テレメトリを再解析。JSON構造とCSVの単位・空欄・bit順を検査。 |
| APRS host lifecycle and history | I | 位置付き気象の合成IQから履歴・DetailsJsonへ数値を伝播。停止・不連続・再起動・出力を既存手順で検査。 |

## 実行記録

- Windows x64／.NET SDK 10.0.403／Release。GPU・実SDRは使用しない。
- `dotnet build SRdeckPlugin.Aprs/SRdeckPlugin.Aprs.csproj -c Release --no-restore`: 成功、警告0・エラー0。
- `dotnet run --project tests/Aprs/Aprs.Tests.csproj -c Release -p:RuntimeIdentifier=win-x64`: 8グループPassed（既存54条件の合成IQを含む）。
- `dotnet test SRdeck.Tests/SRdeck.Tests.csproj -c Release --no-build --filter "DisplayName~APRS|DisplayName=Regression catalog completeness" --logger "trx;LogFileName=aprs-extension.trx"`: 14件Passed、Failed／Skippedとも0。
  証拠はGit対象外の`SRdeck.Tests/TestResults/aprs-extension.trx`。予定したAPRS全13件と登録完全性1件が実行された。
- sandbox内のVSTestはSocketException 10013でプロセス間通信が失敗したため、上記xUnit検査は制限外で実行した。
- グラフ選択保持の診断時、UIの選択クリア通知により項目がリセットされる失敗を確認。局と項目を復元する修正後、同じWPFバインディング試験がPassed。
- 性能環境変数`SRDECK_TEST_TIMING_ASSERTIONS`、`SRDECK_TEST_REALTIME_BUDGET_MS`、`SRDECK_TEST_REQUIRE_GPU`は未指定。
  既存の10秒アイドルIQ測定は6.7 ms／0 bytes。新しいUIの最大履歴条件の性能測定ではない。
- 描画確認画像: Git対象外の`temp/aprs/numeric-graph.png`。

全リポジトリのRelease全件、実波、対話的GUI、最大履歴数での性能は未検証。
テレメトリの校正式・単位定義、ベンダー固有形式等の対応範囲はREADMEに記載する。

## GMSK 9600追加の検証（2026-10-31）

G3RUHの`1 + x^12 + x^17`デスクランブルをNRZI復号の前に追加。
方式選択、日本144.640 MHzプリセット、帯域（9600は20 kHz）、保存設定・復元を更新した。
旧設定はAFSK 1200として読み込む。方式の根拠はREADMEのG3RUH原典を参照。

- Windows x64／.NET SDK 10.0.403／Release。
- APRSプラグインのビルド成功。共通回帰プロジェクトもビルド成功（依存先の鉄道プラグインのネイティブリンク警告4件、エラー0）。
- `dotnet run --project tests/Aprs/Aprs.Tests.csproj -c Release -p:RuntimeIdentifier=win-x64`: 9グループPassed。
  既存AFSK IQの54条件とGMSK IQの216条件を実行。
  GMSKはCFO −1000／0／1000 Hz、−150／0／150 ppm、IQ反転、1／731／4096サンプル境界、
  BT 0.3／0.5、端数サンプル遅延、短い位置報告と最大256バイト・ビット詰めの多い情報部を使用。
  参照送信器は過去の送信ビットを添字で参照し、本番のシフトレジスタ方式とは別にスクランブルする。
- 両方式でCRC破損、不連続、停止・再開、非有限IQ、ノイズ、無音、定常割り当てを確認。
  10秒の無音IQはAFSK 7.2 ms／0 bytes、GMSK 5.1 ms／0 bytes。
- 共通回帰のAPRS全14件と`Regression catalog completeness`の計15件Passed。
  `SRdeck.Tests.exe --run-test "<登録名>"`で各登録を個別実行。
  ホスト試験は両方式で結果・JSON／CSV／TNC2・履歴・再起動後の受信を検証し、
  同調拒否／保留で方式を維持、受信中の方式切り替え、9600で旧15 kHzチャネルを棄却することも確認。
  WPF試験で方式選択と「適用」のバインディング、既存の数値グラフを確認。
- VSTestはtesthostへの接続が90秒でタイムアウトし中止したため、上記は既存の個別ワーカーを直接実行した結果。
  Git対象外の実行記録は`temp/aprs/gmsk-regression.log`、描画確認は`temp/aprs/numeric-graph.png`。

実波、対話的GUI、全リポジトリの回帰全件は未実行。合成IQの成功は実波での感度・復号率を保証しない。

### スペクトラム表示の調整

青い受信帯域のラベルを空にし、表示幅を受信帯域の10倍（AFSK 150 kHz／GMSK 200 kHz）に変更。
方式変更時は既存の表示要求変更イベントでホストへ通知する。
APRSプラグインと共通回帰プロジェクトのReleaseビルドは警告0・エラー0。
`APRS spectrum display and bandwidth switching`でMainViewModelの初期150 kHzと
9600／1200切替時の200／150 kHzを検証した。
同試験、ホスト・履歴、共通能力・設定、登録整合性の4件を個別ワーカーで実行し、すべてPassed。
実行記録はGit対象外の`temp/aprs/spectrum-regression.log`。実機画面での手動確認は未実行。
