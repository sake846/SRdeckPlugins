# SRdeckPlugin.Acars

安定プラグインIDは `acars`。VHF ACARSを受信し、ARINC 618メッセージを解析します。

## 受信経路

- SRdeckの同調サービスを通じて、選択した25 kHz VHFチャネルを要求します。
- 同期した48 kS/s標準チャネルブロックを消費します。
- 1200/2400 Hz NRZI符号化AM-MSKを2400 bit/sで復調します。
- 同期文字、奇数パリティ、ブロックチェックシーケンスを検証します。

## 機能

機体登録、ラベル、ブロックID、本文を受信履歴、結果通知、CSV/JSONエクスポートへ提供します。

## ビルド

互換するSRdeckプラットフォームパッケージを用意し、`SRdeckPlugins.sln`のRelease構成をビルドしてください。
