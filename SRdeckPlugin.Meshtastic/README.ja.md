# SRdeckPlugin.Meshtastic

安定プラグインIDは `meshtastic`。Meshtastic LoRaパケットの受信・復号を提供します。

このソースはソースレベルの開発用として`SRdeckPlugins`に公開します。MeshtasticのDLLは
SRdeckの実行ファイルパッケージに含めません。

## プロファイル

LongFast、LongModerate、LongSlow、MediumFast、MediumSlow、ShortFast、ShortSlowに加え、
250 kHz、125 kHz、混在動作向けの自動拡散率プリセットを提供します。

## 機能

LoRaチャネル抽出、チャープ復調、FEC/CRC検証、Meshtasticパケット解析を行います。WPFワークスペースで
プロファイル設定と周波数オーバーレイを利用できます。

## ビルド

互換するSRdeckプラットフォームパッケージを用意し、`SRdeckPlugins.sln`のRelease構成をビルドしてください。
