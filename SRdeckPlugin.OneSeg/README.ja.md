# SRdeckPlugin.OneSeg

安定プラグインIDは `oneseg`。ISDB-T 1セグメント（ワンセグ）テレビ・音声・字幕・番組表受信機です。

このソースはソースレベルの開発用として`SRdeckPlugins`に公開します。ワンセグのDLLは
SRdeckの実行ファイルパッケージに含めません。再配布前に`PATENT-NOTICE.md`および
`THIRD-PARTY-NOTICES.md`を確認してください。

## プロファイル

日本の地上デジタルテレビ放送UHF 13〜52ch（470〜710 MHz）に対応し、都道府県別の代表送信所プリセット
および全帯域スキャン機能を提供します。

## 機能

- Mode 3（8k FFT）、ガードインターバル1/8、DQPSK/QPSK/16QAMデインターリーブによるOFDM復調
- ビタビ畳み込み復号およびリード・ソロモン誤り訂正
- MPEG-2 TS分離、PSI/SIテーブル解析、番組表（EPG）集約、ARIB STD-B24字幕表示
- LibVLCSharp / LibVLCによる映像・AAC音声再生
- Microsoft WebView2によるBMLデータ放送表示
- TS録画分割保存および直前IQプリトリガー録音

## ビルド

ワンセグは公開`SRdeckPlugins.sln`とBATの初期状態のビルド対象から外しています。
有効化する前に、公開スナップショットのREADME、`PATENT-NOTICE.md`、`THIRD-PARTY-NOTICES.md`を読み、
用途と利用国・地域の特許・ライセンス条件を確認してください。ソース公開だけで第三者の特許権が
すべて許諾されたり、特許非侵害が保証されたりするわけではありません。有効化できると自身で判断した場合にだけ、
`build.bat`のOneSegの`call :BuildPlugin`行から先頭の`rem `を削除し、互換する
SRdeckプラットフォームパッケージを用意してBATを実行してください。
