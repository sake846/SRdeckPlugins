# 検証記録

2026-10-29 / Windows x64 / .NET SDK 10.0.403。SignalWorkbenchの初回実装を対象に検証。

要求は選択帯域のコンスタレーション、周波数偏移分布、シンボルレート候補の表示。新規プラグイン、ホストのビルド・コピー定義、ソリューション、公開カタログ、共通試験のプラグイン集合、Wikiを更新した。ホストAPIの変更はない。

## 自動試験

`SRdeck.Tests/RegressionTests.SignalWorkbench.cs`に入力条件と数値アサートを記載。登録は`RegressionTestCatalog.cs`。

| 登録名 | 入力と期待値 | 結果 |
|---|---|---|
| Signal workbench measurements | 100 ksample/s、振幅0.5、±2,500 Hzの複素搬送波。平均周波数誤差0.01 Hz未満、電力誤差0.01 dB未満、正規化RMS誤差0.001未満。無信号・不完全窓・レート変更・NaN/Infinityからの回復・設定正規化も検査 | Passed |
| Signal workbench symbol rate candidates | seed 1234、矩形QPSK 10 ksym/s、2FSK 5 ksym/s、偏移±2,500 Hz、中心誤差800 Hz。2 FFTビン以内に既知レート候補。seed 5678の雑音は候補0件 | Passed |
| Signal workbench channel isolation | 1 Msample/s、入力中心100 MHz、選択中心100.2 MHz、帯域25 kHz。中心から+1 kHzの目的信号と+80 kHzの強い妨害波。標準チャネル化後の周波数誤差30 Hz未満、分布幅50 Hz未満、電力誤差1 dB未満 | Passed |
| Signal workbench continuity and lifecycle | 開始→部分窓→IQ欠落→回復→表示保持→選局→帯域変更→拒否時の巻戻し→キャンセル→二重Stop→再Start→二重Deactivate→二重Dispose | Passed |
| Signal workbench view layout | 実WPFウィンドウ・ホスト共通テーマ、700/560 DIP幅。グラフの収まり、候補列の可読幅、帯域選択のBinding。100/125/150%相当のPNGを生成して目視確認 | Passed |
| Signal workbench capability contract | API範囲、能力と実装インターフェースの一致 | Passed |
| Signal workbench malformed settings | 不正JSONから初期化、停止・非活性化・破棄の反復 | Passed |
| Signal workbench view construction | STAでメインビューとDataContextを生成 | Passed |
| Built-in plugin set / Regression catalog completeness / Phase 4 deployment catalog | 新規プラグインを含む集合、試験登録、公開バイナリとホストのコピー定義の整合 | Passed（3件） |

合計11件成功。実行コマンド:

```powershell
dotnet build SRdeck.Tests\SRdeck.Tests.csproj -c Release
dotnet test SRdeck.Tests\SRdeck.Tests.csproj -c Release --no-build --filter "DisplayName~Signal workbench|DisplayName=Regression catalog completeness|DisplayName=Built-in plugin set|DisplayName=Phase 4 deployment catalog" --logger "trx;LogFileName=signal-workbench.trx"
dotnet build SRdeckPlugin.SignalWorkbench\SRdeckPlugin.SignalWorkbench.csproj -c Release -p:Platform=x64
```

通常構成・x64構成でReleaseビルド成功。ホストの`CopyBuiltInPluginAssemblies`ターゲットをx64構成で実行し、配備DLLとx64出力のSHA-256一致を確認した。プラグイン固有のネイティブ依存はない。

証拠は開発リポジトリ内の`SRdeck.Tests/TestResults/signal-workbench.trx`、`artifacts/signal-workbench-build.log`、`artifacts/signal-workbench/preview-*.png`（Git対象外）。

上記の回帰試験コマンドと証拠パスは開発リポジトリ専用です。公開ソースからのビルドには
[READMEの手順](README.ja.md#ビルド検証)を使用してください。

## FSK解析の追加検証（2026-10-29）

FSKトーン推定、周波数判別波形、トーン遷移の周期解析、各レート候補の変調指数を追加。
新たな外部依存やホストAPIの変更はない。

| 登録名 | 入力と期待値 | 結果 |
|---|---|---|
| Signal workbench FSK tone levels and modulation index | seed 2026、100 ksample/s、5 ksym/s、振幅0.5、中心ずれ+800 Hzの位相連続2/4/8FSK。隣接間隔5,000 / 2,500 / 1,200 Hz。無雑音と各I/Qへ一様雑音±0.004を加えた条件でトーン数一致、中心・間隔誤差30 Hz未満、レート誤差2 FFTビン以内、h誤差0.01未満。全候補のhと間隔/候補レートの一致、判別波形1,024点も検査。中心ずれ+8 kHz・間隔500 Hzの2FSK、および係数0.3の一次平滑化で遷移をなめらかにした4FSKも検査 | Passed |
| Signal workbench FSK rejection and display reset | 搬送波・無信号・3トーン・不等間隔4トーン・seed 90210の雑音・1 kHz正弦波で偏移±3 kHzのアナログFMでFSK候補なし。表示保持、設定リセット、次窓で未検出になった際の古いFSK表示の消去 | Passed |
| Signal workbench symbol rate candidates | 既存のQPSK/2FSKレート検査に加え、QPSKの位相ジャンプをFSKトーンと誤判定しないことを確認 | Passed |
| Signal workbench view layout | ホスト共通テーマ、700/560 DIP幅。3グラフ、FSK表とレート表のBinding・列幅。上下のスクロール位置で100/125/150%相当のPNGを生成し、波形・トーン表・hの表示を目視確認 | Passed |

上記を含むSignal workbench 10件と、Regression catalog completeness、Phase 4 project reference architectureの計12件が成功（失敗・スキップ0件）。プラグインとテストのReleaseビルドは警告・エラー0件。
初回実装の全体試験とは別の、今回の変更に対する対象限定の検証。

開発リポジトリでは、通常のテストビルド後に次のフィルターで再現できる。

```powershell
dotnet test SRdeck.Tests\SRdeck.Tests.csproj -c Release --no-build --filter "DisplayName~Signal workbench|DisplayName=Regression catalog completeness|DisplayName=Phase 4 project reference architecture"
```

今回の実行は既存出力への干渉を避けて`artifacts/signal-workbench-fsk-tests`にテストをビルドし、
その`SRdeck.Tests.exe`の`-filterVSTest`に同じフィルターを指定した。
結果は`artifacts/signal-workbench-fsk.trx`、ビルド記録は`artifacts/signal-workbench-fsk-build.log`、
画面証拠は`artifacts/signal-workbench/preview-*.png`（いずれもGit対象外）。
一次平滑化の試験はGFSK/GMSKの規格波形による検証を意味しない。

## 制約・未検証

実SDRでの連続運用・未知方式の同定精度、ホストでの手動MODE切替は未検証。音声・結果公開・Export・録音は能力を提供しないため対象外。RFレベルは未校正のdBFS、コンスタレーションは未同期IQ、レート候補は確率や方式の確定値ではない。
