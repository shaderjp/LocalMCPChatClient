# 開発ガイド

## 開発環境

現在の構成は次の環境で検証しています。

- Windows 10/11 x64
- Visual Studio Community 2026（18.8.2で検証）
- Visual Studioワークロード「.NETデスクトップ開発」
- .NET 10 SDK 10.0.300以降
- PowerShell 7またはWindows PowerShell

`global.json`は.NET 10 SDKを選択します。一方、アプリとテストプロジェクトは`net9.0-windows`または`net9.0`をターゲットにしています。SDKとターゲットフレームワークは異なるため、プロジェクトを.NET 10へ再ターゲットする必要はありません。

## リポジトリを準備する

```powershell
git clone https://github.com/shaderjp/LocalMCPChatClient.git
cd LocalMCPChatClient
./scripts/bootstrap.ps1
```

`bootstrap.ps1`はSDKバージョンを確認し、restore、build、testを順に実行します。

オプション:

```powershell
# Release構成で検証
./scripts/bootstrap.ps1 -Configuration Release

# テストを省略
./scripts/bootstrap.ps1 -SkipTests
```

## Visual Studioで開く

1. `LocalMCPChatClient.sln`を開く
2. ソリューション構成を`Debug`、プラットフォームを`Any CPU`にする
3. `LocalMCPChatClient.App`をスタートアッププロジェクトにする
4. `F5`でデバッグ、`Ctrl+F5`でデバッグなし実行を開始する

NuGet復元で問題がある場合は、ソリューションを閉じずに「ツール」→「NuGetパッケージマネージャー」→「パッケージマネージャー設定」で`nuget.org`が有効か確認してください。

## CLIコマンド

```powershell
dotnet restore LocalMCPChatClient.sln
dotnet build LocalMCPChatClient.sln -c Release --no-restore
dotnet test LocalMCPChatClient.sln -c Release --no-build
dotnet run --project src/LocalMCPChatClient.App/LocalMCPChatClient.App.csproj
```

特定のテストプロジェクトだけを実行する場合:

```powershell
dotnet test tests/LocalMCPChatClient.Tests/LocalMCPChatClient.Tests.csproj -c Release
```

モデルやGPUを必要としない偽のllama-server APIとテスト用MCPサーバーを使用するため、通常の自動テストはローカルモデルなしで実行できます。CPU / CUDA / Vulkanと実モデルの確認は手動E2Eテストです。

## プロジェクト構成

```text
LocalMCPChatClient.sln
├─ src/
│  ├─ LocalMCPChatClient.App/            WPF、MVVM、DI、画面
│  ├─ LocalMCPChatClient.Core/           ドメインモデル、インターフェース
│  └─ LocalMCPChatClient.Infrastructure/ llama.cpp、MCP、SQLite、設定、取得処理
├─ tests/
│  ├─ LocalMCPChatClient.Tests/          単体・統合テスト
│  └─ LocalMCPChatClient.TestServer/     テスト用HTTP MCPサーバー
├─ catalog/                              固定モデル・ランタイムカタログ
├─ docs/                                 利用者・開発者向け資料
└─ scripts/                              セットアップ、Git対象サイズ検査
```

依存方向は`App → Infrastructure → Core`を基本とし、`Core`はWPF、SQLite、MCP SDKへ依存しません。詳しくは[アーキテクチャ](architecture.md)を参照してください。

## 設定と開発データ

デバッグ実行でも既定では次の実ユーザーデータを使用します。

```text
%LocalAppData%\LocalMCPChatClient
```

テストコードでは`AppPaths`へ一時ディレクトリを渡し、本番データから分離してください。デバッグ中に設定を初期化したい場合は、アプリを終了してから`settings.json`を別名へ退避します。履歴を保持したい場合は`history.db`を削除・移動しないでください。

## モデルとランタイムのカタログ

- `catalog/models.json`: Hugging Faceリポジトリ、revision、ファイル名、サイズ、SHA-256、ライセンスURL
- `catalog/runtimes.json`: llama.cppリリース、CPU / CUDA / Vulkanアセット、サイズ、SHA-256

カタログを変更する場合は、ダウンロード元の固定revisionまたはreleaseとSHA-256を確認し、`BuiltInArtifacts`および既定設定との整合も更新してください。モデル、ZIP、DLL、GGUFをリポジトリやGit LFSへ追加しないでください。

## Portable版を発行する

```powershell
dotnet publish src/LocalMCPChatClient.App/LocalMCPChatClient.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o artifacts/portable

Compress-Archive `
  -Path artifacts/portable/* `
  -DestinationPath artifacts/LocalMCPChatClient-win-x64.zip `
  -Force

Get-FileHash artifacts/LocalMCPChatClient-win-x64.zip -Algorithm SHA256
```

`artifacts`は生成物でありGit管理しません。GitHub Actionsは同等の発行を行い、ZIPとSHA-256を成果物にします。`v*`タグではGitHub Releaseへ添付します。

## 変更前後の確認

最低限、次を実行してください。

```powershell
./scripts/check-large-files.ps1
dotnet build LocalMCPChatClient.sln -c Release
dotnet test LocalMCPChatClient.sln -c Release --no-build
```

UI変更では、ダークテーマ上の本文・見出し・入力欄・無効状態のコントラストと、ウィンドウ最小サイズでのスクロールも確認してください。
