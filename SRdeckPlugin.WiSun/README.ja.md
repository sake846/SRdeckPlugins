# SRdeckPlugin.WiSun

安定プラグインIDは `wisun`。920 MHz帯Wi-SUN / IEEE 802.15.4g SUN FSK復調プラグインです。

## プロファイル

Wi-SUN FAN Mode 1b/2/3/4/5、HAN A/Bルート、カスタム設定に対応します。
チャネルプランとPHYプロファイルをライブ同調プロファイルとして提供します。

## 機能

SUN FSKの同期とPHY/MAC解析を行い、周波数オーバーレイ、パケットエクスポート、ヘッドレス処理を提供します。

## ビルド

互換するSRdeckプラットフォームパッケージを用意し、`SRdeckPlugins.sln`のRelease構成をビルドしてください。
