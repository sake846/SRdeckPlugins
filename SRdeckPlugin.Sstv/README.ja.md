# SRdeckPlugin.Sstv

SRdeck用SSTV受信プラグインです。USB／FM入力、VIS自動判別、カラー画像の逐次表示、PNG保存に対応します。

対応方式: Martin M1/M2、Scottie S1/S2、Robot 36、PD120。

```powershell
dotnet build SRdeckPlugin.Sstv/SRdeckPlugin.Sstv.csproj -c Release
```

生成した`SRdeckPlugin.Sstv.dll`をSRdeck.exeと同じフォルダーへ置き、再起動してください。リポジトリからホストをビルドした場合は自動で配置します。

周波数にはUSBのダイヤル周波数、またはFMの搬送波周波数を入力します。VISヘッダーの途中から受信した場合は、次の送信を待ってください。手動開始・音声モニター・画像履歴・自動保存はありません。

操作説明は[公式Wiki](https://github.com/sake846/SRdeck/wiki/Plugins-Sstv)を参照してください。
