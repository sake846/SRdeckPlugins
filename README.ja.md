# SRdeckPlugins

[![Wiki](https://img.shields.io/badge/docs-SRdeck_Wiki-blue.svg)](https://github.com/sake846/SRdeck/wiki)
[![Plugins Overview](https://img.shields.io/badge/plugins-公式プラグイン一覧-orange.svg)](https://github.com/sake846/SRdeck/wiki/Plugins-Overview)
[![Developer Guide](https://img.shields.io/badge/docs-開発者ガイド-purple.svg)](https://github.com/sake846/SRdeck/wiki/Developer-Overview)

SRdeckの公式プラグインをバージョン付きリリーススナップショットとして収録しています。

公開ソーススナップショットには、実行ファイルパッケージ向けの10プラグインと、
ソース公開のみのMeshtasticおよびワンセグを収録します。MeshtasticとワンセグのDLLはSRdeckの実行ファイル
パッケージに含めません。再配布前に`PATENT-NOTICE.md`を確認してください。

製品の変更をこのリポジトリへ直接コミットしないでください。通常の開発・レビュー手順を利用してください。

## 📖 ドキュメント・Wiki

各プラグインの詳しい仕様や受信設定、新規プラグイン開発の手順は [SRdeck 公式Wiki](https://github.com/sake846/SRdeck/wiki) をご覧ください。

- 🔌 **プラグイン利用**: [公式プラグイン一覧](https://github.com/sake846/SRdeck/wiki/Plugins-Overview) / [プラグイン管理](https://github.com/sake846/SRdeck/wiki/Plugin-Management)
- 💻 **プラグイン開発**: [開発概要](https://github.com/sake846/SRdeck/wiki/Developer-Overview) / [開発環境](https://github.com/sake846/SRdeck/wiki/Development-Environment) / [最初のプラグイン](https://github.com/sake846/SRdeck/wiki/Creating-First-Plugin) / [右ペインUI設計](https://github.com/sake846/SRdeck/wiki/Right-Pane-UI-Design)
- ❓ **トラブル・FAQ**: [よくある質問 (FAQ)](https://github.com/sake846/SRdeck/wiki/FAQ) / [トラブルシューティング](https://github.com/sake846/SRdeck/wiki/Troubleshooting)

## リリースメタデータ

- リリースバージョン: `1.0.5`
- 必要なSRdeckプラットフォームパッケージ: `1.0.5`

## ビルド

### 必要なもの

- Windows x64
- .NET 10 SDK（`dotnet --info`で10.x SDKが表示されること）
- Git
- `1.0.5`と同じバージョンのSRdeckプラットフォームNuGetパッケージ4個

プラグインは`SRdeckPlugin.Contracts`、`SRdeckPlugin.Sdk`、`SRdeckPlugin.Wpf`、
`SRdeckCore.SignalProcessing`をNuGetパッケージとして参照します。ビルド・動作確認に使う
SRdeckホストとNuGetパッケージのバージョンを一致させてください。異なるSRdeckリリースの
パッケージを混在させないでください。

### 1. 対応するソーススナップショットを取得する

```powershell
$releaseVersion = "1.0.5"
$platformVersion = "1.0.5"

git clone --branch "v$releaseVersion" --depth 1 https://github.com/sake846/SRdeckPlugins.git
Set-Location .\SRdeckPlugins
```

### 2. プラットフォームパッケージを用意する

対応する[SRdeckのリリース](https://github.com/sake846/SRdeck/releases)から、次の4ファイルを
ダウンロードしてください。

- `SRdeckCore.SignalProcessing.1.0.5.nupkg`
- `SRdeckPlugin.Contracts.1.0.5.nupkg`
- `SRdeckPlugin.Sdk.1.0.5.nupkg`
- `SRdeckPlugin.Wpf.1.0.5.nupkg`

GitHub CLIをインストール済みなら、次のコマンドで自動取得できます。

```powershell
$packageDirectory = Join-Path (Get-Location) "platform-packages"
New-Item -ItemType Directory -Force $packageDirectory | Out-Null
gh release download "v$platformVersion" `
  --repo sake846/SRdeck `
  --pattern "*.nupkg" `
  --dir $packageDirectory
```

GitHub CLIを使わない場合は、ブラウザーで同じ4個の`.nupkg`をダウンロードし、
`platform-packages`へ直接置いてください。

### 3. 全プラグインを復元・ビルドする

```powershell
dotnet restore .\SRdeckPlugins.sln `
  --source $packageDirectory `
  --source https://api.nuget.org/v3/index.json `
  -p:SRdeckPlatformVersion=$platformVersion

dotnet build .\SRdeckPlugins.sln `
  -c Release `
  --no-restore `
  -p:SRdeckPlatformVersion=$platformVersion
```

復元後に特定のプラグインだけをビルドする場合は、プロジェクトファイルを指定します。
例としてAISプラグインをビルドするコマンドは次のとおりです。

```powershell
dotnet build .\SRdeckPlugin.Ais\SRdeckPlugin.Ais.csproj `
  -c Release `
  --no-restore `
  -p:SRdeckPlatformVersion=$platformVersion
```

生成されるDLLは次の場所にあります。

```text
SRdeckPlugin.Ais\bin\x64\Release\net10.0-windows\win-x64\SRdeckPlugin.Ais.dll
```

`Ais`はビルドしたプラグイン名に置き換えてください。同じ出力フォルダーにある
プラグイン固有のDLLも実行時に必要です。

### 4. SRdeckでローカル動作確認する

同じSRdeckリリースのホスト、または対応するホストソーススナップショットからビルドした
`SRdeck.exe`を用意します。プラグインDLLとプラグイン固有の依存DLLを`SRdeck.exe`と同じ
フォルダーへコピーし、SRdeckを再起動してください。

```powershell
$pluginOutput = Join-Path (Get-Location) "SRdeckPlugin.Ais\bin\x64\Release\net10.0-windows\win-x64"
$srdeckDirectory = "C:\path\to\SRdeck"
Get-ChildItem $pluginOutput -Filter "*.dll" | Copy-Item -Destination $srdeckDirectory -Force
```

DLLの検索先は`SRdeck.exe`と同じフォルダーです。`%LOCALAPPDATA%\SRdeck\plugins`は設定や
プラグインデータ用で、プラグインDLLの配置場所ではありません。DLLを差し替えた後は、
SRdeckを完全に終了してから再起動してください。

Meshtasticおよびワンセグはこのソーススナップショットからローカル検証用にビルドできますが、公式の
SRdeck実行ファイルパッケージにはDLLを含めません。

### トラブルシューティング

- SRdeckパッケージの`NU1101`エラー: `platform-packages`に4個すべての`.nupkg`があり、
  バージョンが`$platformVersion`と一致するか確認してください。
- 実行時の型不足・互換性エラー: ホストとプラットフォームパッケージを同じSRdeckリリースに
  そろえてください。
- DLLをコピーしてもプラグインが表示されない: `SRdeck.exe`と同じフォルダーにあるか確認し、
  SRdeckのプロセスをすべて終了してから再起動してください。

公開前に、リリースワークフローでも対応するSRdeckプラットフォームパッケージを使った同じ
復元とReleaseビルドを実行しています。
