# SRdeckPlugin.Vdl

安定プラグインIDは `vdl`。VHF Data Link Mode 2（D8PSK/AVLC）受信プラグインです。

## 受信経路

- 136.725 MHz～136.975 MHzの6つのVDL2チャネルプロファイルを提供します。
- 105 kS/s標準チャネルIQを消費します。
- D8PSKプリアンブル、タイミング、搬送波を同期します。
- ヘッダーFEC、Reed-Solomon、フレームチェックシーケンスを検証します。

## 機能

AVLCフレームとACARS上位層の内容を解析し、受信履歴、音声モニター、結果通知、周波数オーバーレイ、
CSV/JSONエクスポートを提供します。

## ビルド

互換するSRdeckプラットフォームパッケージを用意し、`SRdeckPlugins.sln`のRelease構成をビルドしてください。
