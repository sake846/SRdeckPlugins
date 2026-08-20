# SRdeckPlugin.Ft8

安定プラグインIDは `ft8`。FT8、FT4、JT65Aのマルチシグナル弱信号受信・復号を提供します。

## 受信経路

- `Bands.json`からバンドとモードを読み込み、配布時は`SRdeckPlugin.Ft8.bands.json`として同梱します。
- FT8、FT4、JT65AのUTCスロットに合わせて復号します。
- 選択した標準チャネルを使用し、広帯域生IQへ暗黙にフォールバックしません。
- 候補探索、同期評価、LDPC/RS復号を行います。

## 機能

バンドプロファイル、受信履歴、音声モニター、ウォーターフォール注釈、結果通知、CSV/JSONエクスポートを提供します。
プロトコル出典と第三者帰属は`docs/protocol-provenance.md`と`THIRD-PARTY-NOTICES.md`に記載しています。

## ビルド

互換するSRdeckプラットフォームパッケージを用意し、`SRdeckPlugins.sln`のRelease構成をビルドしてください。
