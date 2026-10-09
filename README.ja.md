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

- リリースバージョン: `1.0.7`
- 必要なSRdeckプラットフォームパッケージ: `1.0.7`

## ビルド

### 必要なもの

- Windows x64
- .NET 10 SDK（`dotnet --info`で10.x SDKが表示されること）
- Git（Gitでソースを取得する場合のみ。ZIPで取得する場合は不要）
- `1.0.7`と同じバージョンのSRdeckプラットフォームNuGetパッケージ4個

プラグインは`SRdeckPlugin.Contracts`、`SRdeckPlugin.Sdk`、`SRdeckPlugin.Wpf`、
`SRdeckCore.SignalProcessing`をNuGetパッケージとして参照します。ビルド・動作確認に使う
SRdeckホストとNuGetパッケージのバージョンを一致させてください。異なるSRdeckリリースの
パッケージを混在させないでください。

この公開スナップショットのプラグインのビルドは CMake を呼び出しません。
SRdeck 本体もソースからビルドする場合は、別途 MSVC x64/x86 ビルドツール、Windows 10 / 11 SDK、
CMake（VS 2022 は3.21以上、VS 2026 は4.2以上）が必要です。
インストール手順と CMake エラー別の対処は [開発環境](https://github.com/sake846/SRdeck/wiki/Development-Environment) を参照してください。

### 1. 対応するソーススナップショットを取得する

以下のコマンドはすべてWindowsの**コマンドプロンプト（cmd.exe）**用です。
スタートメニューで「コマンドプロンプト」を開き、最初にバージョンを設定してください。
以降も同じウィンドウで、各コマンドが成功したことを確認してから次へ進みます。

```cmd
set "RELEASE_VERSION=1.0.7"
set "PLATFORM_VERSION=1.0.7"
```

#### Gitを使わない場合（ZIPをダウンロード）

1. [対応するSRdeckPluginsのリリース](https://github.com/sake846/SRdeckPlugins/releases/tag/v1.0.7)
   をブラウザーで開き、Assetsの **Source code (zip)** をダウンロードします。
2. エクスプローラーの「すべて展開」でZIPを展開します。
3. 展開されたフォルダーのうち、`SRdeckPlugins.sln`が入っているフォルダーを
   `C:\SRdeckBuild\SRdeckPlugins`として配置します。ZIPの外側のフォルダーではなく、
   このパスの直下に`SRdeckPlugins.sln`がある状態にしてください。
4. コマンドプロンプトで、そのフォルダーへ移動します。

```cmd
cd /d "C:\SRdeckBuild\SRdeckPlugins"
```

#### Gitを使う場合

ZIPの代わりにGitで取得する場合は、次のコマンドを実行します。

```cmd
if not exist "C:\SRdeckBuild" mkdir "C:\SRdeckBuild"
cd /d "C:\SRdeckBuild"
git clone --branch "v%RELEASE_VERSION%" --depth 1 https://github.com/sake846/SRdeckPlugins.git
cd SRdeckPlugins
```

### 2. プラットフォームパッケージを用意する

対応する[SRdeckのリリース](https://github.com/sake846/SRdeck/releases)から、次の4ファイルを
ダウンロードしてください。

- `SRdeckCore.SignalProcessing.1.0.7.nupkg`
- `SRdeckPlugin.Contracts.1.0.7.nupkg`
- `SRdeckPlugin.Sdk.1.0.7.nupkg`
- `SRdeckPlugin.Wpf.1.0.7.nupkg`

ソースのフォルダーで次のコマンドを実行し、パッケージの配置先を作ります。

```cmd
if not exist "platform-packages" mkdir "platform-packages"
set "PACKAGE_DIRECTORY=%CD%\platform-packages"
```

ダウンロードした4個の`.nupkg`は展開せず、`platform-packages`へ直接置いてください。
ZIPから取得したソースとパッケージの配置例は次のとおりです（ほかのソースファイルは省略）。

```text
C:\SRdeckBuild\
└─ SRdeckPlugins\
   ├─ SRdeckPlugins.sln
   ├─ Directory.Build.props
   ├─ Directory.Build.targets
   ├─ SRdeckPlugin.Ais\
   │  └─ SRdeckPlugin.Ais.csproj
   └─ platform-packages\
      ├─ SRdeckCore.SignalProcessing.1.0.7.nupkg
      ├─ SRdeckPlugin.Contracts.1.0.7.nupkg
      ├─ SRdeckPlugin.Sdk.1.0.7.nupkg
      └─ SRdeckPlugin.Wpf.1.0.7.nupkg
```

GitHub CLIをインストール済みなら、ブラウザーでのダウンロードの代わりに次のコマンドを使えます。
ZIPとブラウザーで取得する手順では、GitもGitHub CLIも不要です。

```cmd
gh release download "v%PLATFORM_VERSION%" --repo sake846/SRdeck --pattern "*.nupkg" --dir "%PACKAGE_DIRECTORY%"
```

### 3. 全プラグインを復元・ビルドする

`C:\SRdeckBuild\SRdeckPlugins`で、次のコマンドを実行します。
`restore`は依存パッケージを取得するため、インターネット接続が必要です。

```cmd
dotnet restore .\SRdeckPlugins.sln ^
  --source "%PACKAGE_DIRECTORY%" ^
  --source https://api.nuget.org/v3/index.json ^
  -p:SRdeckPlatformVersion=%PLATFORM_VERSION%

dotnet build .\SRdeckPlugins.sln ^
  -c Release ^
  --no-restore ^
  -p:SRdeckPlatformVersion=%PLATFORM_VERSION%
```

`^`はコマンドプロンプトの行継続記号です。各`^`の後ろに空白を入れないでください。

復元後に特定のプラグインだけをビルドする場合は、プロジェクトファイルを指定します。
例としてAISプラグインをビルドするコマンドは次のとおりです。

```cmd
dotnet build .\SRdeckPlugin.Ais\SRdeckPlugin.Ais.csproj ^
  -c Release ^
  --no-restore ^
  -p:SRdeckPlatformVersion=%PLATFORM_VERSION%
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

次の例は`SRdeck.exe`が`C:\SRdeck`にある場合です。SRdeckを終了してから実行し、
`SRDECK_DIRECTORY`は実際の配置先に合わせて変更してください。

```cmd
set "PLUGIN_OUTPUT=%CD%\SRdeckPlugin.Ais\bin\x64\Release\net10.0-windows\win-x64"
set "SRDECK_DIRECTORY=C:\SRdeck"
copy /Y "%PLUGIN_OUTPUT%\*.dll" "%SRDECK_DIRECTORY%\"
```

DLLの検索先は`SRdeck.exe`と同じフォルダーです。`%LOCALAPPDATA%\SRdeck\plugins`は設定や
プラグインデータ用で、プラグインDLLの配置場所ではありません。DLLを差し替えた後は、
SRdeckを完全に終了してから再起動してください。

Meshtasticおよびワンセグはこのソーススナップショットからローカル検証用にビルドできますが、公式の
SRdeck実行ファイルパッケージにはDLLを含めません。

### トラブルシューティング

- CMake エラー: このソリューションは CMake を呼び出しません。実行したコマンドとエラーの
  `-S` のパスを確認し、ホストや開発用統合リポジトリをビルドしている場合は上記のネイティブ環境を用意してください。
- SRdeckパッケージの`NU1101`エラー: `platform-packages`に4個すべての`.nupkg`があり、
  バージョンが`%PLATFORM_VERSION%`と一致するか確認してください。
- 実行時の型不足・互換性エラー: ホストとプラットフォームパッケージを同じSRdeckリリースに
  そろえてください。
- DLLをコピーしてもプラグインが表示されない: `SRdeck.exe`と同じフォルダーにあるか確認し、
  SRdeckのプロセスをすべて終了してから再起動してください。

公開前に、リリースワークフローでも対応するSRdeckプラットフォームパッケージを使った同じ
復元とReleaseビルドを実行しています。
