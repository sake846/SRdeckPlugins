# SRdeckPlugin.Meshtastic

安定プラグインIDは `meshtastic`。Meshtastic LoRaパケットの受信・復号を提供します。

このソースはソースレベルの開発用として`SRdeckPlugins`に公開します。MeshtasticのDLLは
SRdeckの実行ファイルパッケージに含めません。

## プロファイル

LongFast、LongModerate、LongSlow、MediumFast、MediumSlow、ShortFast、ShortSlowに加え、
250 kHz、125 kHz、混在動作向けの自動拡散率プリセットを提供します。

## 機能

LoRaチャネル抽出、チャープ復調、FEC/CRC検証、Meshtasticパケット解析を行います。条件指定モードでは、
選択した先頭スロットを48 kHzモノラルPCMへFM復調して音声モニターできます。探索モードではFMモニターを
処理しません。WPFワークスペースでプロファイル設定と周波数オーバーレイを利用できます。

## ビルド

互換するSRdeckプラットフォームパッケージを用意し、`SRdeckPlugins.sln`のRelease構成をビルドしてください。
