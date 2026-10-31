> 過去の実装・検証記録です。現在はDVStickによる音声化のみ対応し、ソフトウェア音声合成とDLLへのフォールバックは廃止しています。

# AMBE復号経路・外部DLLの試験レビュー

冒頭から「未充足と公開前の確認」までは、最初のDLL非同梱対応時の記録です。
秘話解析の現行動作は、末尾の「秘話解析の分離」を参照してください。

## 変更と実行対象

- 要求: ドングルを優先、ドングルなし・DLLありはソフトウェア復号、どちらも利用できなければ受信表示のみ。mbelib-neo DLLは利用者が外部で用意する。
- 確認日: 2026-10-11（Asia/Tokyo）。基点 `957b25233d331ba214d8fdf8ea74013f2e191580` と本変更の未コミット差分。
- 変更範囲: `AribT98Voice`、`AmbeDecoder`とその呼出元、管理コードのチャネルFEC、ネイティブDLLのリンク、出力へのDLL配置、配布検査。
- 呼出元確認: 音声再生・自動秘話探索・手動秘話解析・受信ベンチマークを検索し、DLLなしで実行できない解析を開始しないことを確認。
- 総合状態: 変更範囲の自動試験は成功。ドングル実機・公開配布物の受入確認は未完了。

## 検査観点の適否

| 回帰試験仕様の節 | 適否・根拠 |
|---|---|
| 4.1 登録・互換性 | 適用。DLLなしでの生成・有効化、標準API 2.0.0 DLLとの動的リンク |
| 4.2 ライフサイクル | 適用。開始・停止・再開始・設定適用・Dispose |
| 4.3 IQ配送、4.4 同調 | 適用。既存のIQ・同期・同調試験とDLLなしの合成IQ配送 |
| 4.5 設定・データ・キャプチャ | 適用。現在鍵・単一プリセットの維持、既存の設定・IQ保存試験 |
| 4.6 音声・診断 | 適用。PCMゼロ、音声提出0件、復号経路・表示のみの状態 |
| 4.7 UI | 適用。受信UC・履歴の表示、DLLなしの秘話解析コマンド無効化、既存のWPFビュー試験 |
| 4.8 方式固有処理 | 適用。独立したSTD-T98ベクトル、Golay FEC、秘話、PSK経路 |
| 4.9 ホスト | 音声sink契約は適用。ホスト実装は変更せず、録音・SDR入力の全受入確認は今回の自動試験対象外 |
| 4.10 ランナー・配布 | 登録完全性・DLL省略配置・配布検査の構文を確認。公開ZIP全体の検査は未実行 |
| 5 性能 | 既存のSTD-T98受信・FIR性能試験を実行。ドングルでの実時間処理は未確認 |
| 6 受入 | ドングルのみ／DLLと併用／通信中抜去の実機確認が未完了 |

## 要求と試験の対応

新規ケースの入力・アサートは `SRdeck.Tests/RegressionTests.AribT98.CodecAvailability.cs` を正本とする。
合成IQ・独立ベクトルの出典は既存 `RegressionTests.AribT98.cs` の定数・コメントを参照。

| 登録名 | 区分・入力・期待値 | 結果 |
|---|---|---|
| ARIB STD-T98 managed voice FEC | U。通常・無音・鍵129の独立49 bitベクトル、4スロット、各72 bitの単一誤りとC0の3 bit誤り。管理／既存ネイティブFECのパラメータと誤り数が一致。非2値入力を拒否 | Passed |
| ARIB STD-T98 codec unavailable | I。P/InvokeのDLLロードにDllNotFoundExceptionを注入。音声出力なし、受信表示継続、既知の鍵・単一プリセット維持、ネイティブ探索なし、ロード試行1回 | Passed |
| ARIB STD-T98 codec incompatible | I。同じ経路にBadImageFormatExceptionを注入。利用できないDLLでも受信表示を継続 | Passed |
| ARIB STD-T98 external codec file absent | I。接続用DLLを残し、外部mbe-neo.dllだけを省いた実際のファイル配置で別プロセスを起動。通常・秘話の受信表示、音声提出0件、停止・再開始を確認 | Passed |
| ARIB STD-T98 dongle fallback to DLL | C。接続不能のCOM2147483647を指定。接続失敗を表示し、DLLで非ゼロのPCMを生成 | Passed |
| ARIB STD-T98 AMBE reference vectors / voice gating / plugin lifecycle | U/I。外部DLLを使い、既存の1031 Hz音声・消音条件・80 ms mono提出を確認 | Passed |
| その他のSTD-T98登録ケース | U/C/I/P。受信、秘話、PSK、設定、ビュー、キャプチャ、性能。予定集合は実行時のcases.txt | Passed |
| Regression catalog completeness | C。公開Testメソッドの登録漏れとID重複を確認 | Passed |

## 実行と証拠

- Windows x64、.NET SDK 10.0.401、Release、MSVC 19.51。`SRDECK_TEST_*` の環境変数は未指定。
- プラグインのReleaseビルド: 警告0、エラー0。
- 起動中のSRdeckによる共有DLLロックのため、ホストを含む通常ビルドは失敗。変更したプラグインをビルド後、ホスト実装を変更していないため、試験プロジェクトは `-p:BuildProjectReferences=false` でビルド（警告0、エラー0）。
- `dotnet test` はtesthost接続タイムアウトで中止。実行成功として数えない。
- 代替実行: 同じカタログから `ARIB STD-T98` の45件と登録完全性の1件を選び、既存の `SRdeck.Tests.exe --run-test "登録名"` を各ケースで別プロセス起動。
- 結果: 46 Passed、0 Failed、0 Skipped。リポジトリ全件の成功を示す結果ではない。
- ケース一覧・終了コード・所要時間・出力: `temp/arib-t98-codec-tests/cases.txt`、`direct-results.json`、`direct-results.jsonl`。
- 単独実行: `ARIB STD-T98 external codec file absent`、終了コード0。
- ソフトウェアDLLは上流ソースを変更せず単独ビルドした `temp/mbelib-neo-standalone/Release/mbe-neo.dll` を試験フォルダーへ配置。全46件で使用したDLLのSHA-256は `930CD6DB7B2A54F769DB7CDE4C403C94BFA3C2E9E9C93DD3C02A3595B688DD43`。
- 接続用DLLのimportをdumpbinで確認: `mbe-neo.dll` を参照。ソフトウェアCODECの静的リンクを除去。
- 外部DLLのバージョンを状態構造体の確保・初期化より前に確認。対応外のバージョンは利用不可とする。
- 新規の通常出力 `temp/arib-t98-display-only` に `mbe-neo.dll` が含まれないことを確認。
- `Native/Test-Build-Native.ps1`: Passed。ビルド失敗時のstamp、空DLL、Debug/Release/DebugPublicを検査。
- 修正前のDLLなし経路は、常時 `new AmbeDecoder()` を行うため起動不能。修正前のバイナリの再実行は行わず、修正後のDLL省略配置を検証。

## 未充足と公開前の確認

| 未充足 | 影響・実施条件 |
|---|---|
| 正規AMBEドングルだけでの復号、DLL併用時の優先、混在スロット・指定鍵、通信中抜去と回復 | 実機と既知音声IQを用意して確認。パケット・FECの自動試験から実機の受信音を推定しない |
| 公開ZIP、クリーンなWindows環境、依存ランタイム | 現在は公開対象外。公開許可リストを変更する時に、CODEC非同梱と必要DLL・利用条件を確認 |
| 公開国の特許・GPL等の条件 | DLLを別途用意することだけでは確認済みにならない。第三者ライセンス文書と上流の特許注意書きを確認 |

新しい公開API契約・変調方式・設定スキーマは追加していない。公開許可リストの変更と実際の公開は行っていない。

## 秘話解析の分離

確認日: 2026-10-11（Asia/Tokyo）。上記対応後、音声復号用DLLへの解析の依存を分離した。

- `arib_t98_analysis_native.dll` はパラメータ分類と秘話候補の採点だけを行う。音声PCMを生成せず、`mbe-neo.dll` をリンクしない。
- 既存のネイティブ採点・管理コードの並列処理をそのまま移動。分離前後の実装本文が同一であることを比較確認。
- 自動探索と内部の手動解析処理は、この同梱解析モジュールの利用可否で判定する。音声復号用DLLがない場合も、現在鍵の確認・プリセット比較・全32767コード探索と手動解析処理を実行できる。手動解析はViewModelのコマンドとして検証し、現行UIには操作項目を設けていない。
- 音声復号用DLLがない場合のチャネルFECは既存の管理コードによる硬判定。誤り訂正で保護されないビットの信頼度も確認し、信頼度不足の区間を解析から除外する。
- ドングルと音声復号用DLLがともに利用できなければ、解析結果が得られても音声PCMを出力しない。
- 解析モジュールだけが利用できない場合は、既知の現在鍵・単一プリセットを維持し、音声復号は継続できる。
- プラグインの通常出力に解析モジュールを追加し、公開配布検査にも混入禁止ファイルとして追加。公開許可リストは変更していない。

検証:

- Windows x64 / Release。プラグイン単独と試験プロジェクトのビルドは警告0、エラー0。新規出力先 `temp/arib-t98-independent-analysis` への依存プロジェクトを含めたビルドも成功し、解析モジュールを含み、`mbe-neo.dll` を含まないことを確認。
- `dumpbin /dependents` で解析モジュールの依存を確認。WindowsとVCランタイムのみ。公開関数は `arib_t98_classify_parameters` と `arib_t98_score_privacy` の2件で、音声合成関数は含まない。
- DLL実ファイルを省いた別プロセスと、ロード失敗・不適合の注入で、合成通常音声の鍵05931について自動全コード探索・手動解析が成功。音声提出は0件。信頼度を落とした未保護ビット100区間は解析から除外した。
- 追加ケース `ARIB STD-T98 analysis unavailable` は、解析モジュールの欠落によって音声復号が止まらず、既知の鍵を維持し、手動解析だけを無効にすることを確認。
- `Native/Test-Build-Native.ps1` 成功。解析モジュールの空DLL拒否とDebugPublicへのコピーも確認。
- 対象ケースは `temp/arib-t98-analysis-tests/cases.txt`。初回、新規出力先の生成物、最新コードの各回で、関連47件がすべて成功。最新結果は同ディレクトリの `latest-results.json` / `latest-results.jsonl`、生成物のSHA-256とコミットIDは `latest-artifacts.json` に記録した。試験対象と最新プラグイン出力が一致し、試験前後で生成物のSHA-256とコミットIDが変わらないことも確認した。

ドングル実機での音声、弱い信号での硬判定FECと秘話推定の性能、公開ZIPの受入確認は未実施。
