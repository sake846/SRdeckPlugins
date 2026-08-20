# SRdeckPlugin.Hfdl

安定プラグインIDは `hfdl`。HF Data Linkを受信し、ARINC 635を解析します。

## 受信経路

- 同調サービスを通じて、選択した3 kHz地上局チャネルを要求します。
- 14.4 kS/s IQを受け、1800 symbol/sのBPSK、QPSK、8PSKを処理します。
- prekey、プリアンブル、M1/M2、位相デスクランブル、デインターリーブ、K=7 Viterbi復号、CRC検証を行います。

## 機能

SPDU/LPDUエンベロープ、復号結果、生ペイロード、受信履歴、周波数オーバーレイ、CSV/JSONエクスポートを提供します。

## ビルド

互換するSRdeckプラットフォームパッケージを用意し、`SRdeckPlugins.sln`のRelease構成をビルドしてください。
