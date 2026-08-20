# リリース手順

LocalMCPChatClientは、Gitタグとアプリのバージョンが一致した場合にGitHub ActionsからReleaseを公開します。

## バージョン情報

バージョンの情報源はリポジトリ直下の`Directory.Build.props`です。

```xml
<Version>0.2.0-beta.1</Version>
<VersionPrefix>0.2.0</VersionPrefix>
<VersionSuffix>beta.1</VersionSuffix>
<AssemblyVersion>0.2.0.0</AssemblyVersion>
<FileVersion>0.2.0.0</FileVersion>
<InformationalVersion>0.2.0-beta.1</InformationalVersion>
```

リリースタグは`v<Version>`形式にします。例えば公開ベータのタグは`v0.2.0-beta.1`です。`-`を含むversionはGitHub Prerelease、それ以外は正式Releaseとして公開します。タグと`Directory.Build.props`の値が一致しない場合、CIはReleaseを作成せず失敗します。

## Release成果物

| ファイル | 内容 |
|---|---|
| `LocalMCPChatClient-<version>-win-x64.zip` | self-contained PortableフォルダーのZIP |
| `LocalMCPChatClient-<version>-win-x64.zip.sha256` | ZIPのSHA-256 |
| `LocalMCPChatClient-<version>-win-x64.exe` | self-contained単体EXE |
| `LocalMCPChatClient-<version>-win-x64.exe.sha256` | EXEのSHA-256 |
| `LocalMCPChatClient-<version>-LICENSE.txt` | LocalMCPChatClient本体のMIT License |
| `LocalMCPChatClient-<version>-THIRD-PARTY-NOTICES.txt` | 同梱ライブラリの第三者通知一覧 |

Portable ZIPには上記に加えて`licenses`フォルダーを含めます。単体EXEにはライセンス文書を埋め込み、「設定」から表示できるようにします。モデル、`llama-server`、DB、ログはどの成果物にも含めません。

## リリース前チェック

1. `CHANGELOG.md`へ対象バージョンの変更内容を追加する
2. `Directory.Build.props`のバージョンを更新する
3. READMEと資料中の固定バージョン例を確認する
4. Releaseビルドとテストを実行する
5. Portable ZIPと単体EXEをローカル発行する
6. EXEのFileVersionとProductVersionを確認する
7. クリーンなWindows環境で初回セットアップを手動確認する
8. Portable出力に`LICENSE.txt`、`THIRD-PARTY-NOTICES.txt`、`licenses`フォルダーがあることを確認する
9. 単体EXEの「設定」からすべてのライセンス文書を表示できることを確認する

```powershell
./scripts/check-large-files.ps1
./scripts/bootstrap.ps1 -Configuration Release

dotnet publish src/LocalMCPChatClient.App/LocalMCPChatClient.App.csproj `
  -c Release -r win-x64 --self-contained true -o artifacts/portable

dotnet publish src/LocalMCPChatClient.App/LocalMCPChatClient.App.csproj `
  -c Release -p:PublishProfile=win-x64-single-file -o artifacts/single-file

(Get-Item artifacts/single-file/LocalMCPChatClient.exe).VersionInfo |
  Select-Object FileVersion, ProductVersion
```

## タグを公開する

検証済みコミットでタグを作成してpushします。

```powershell
git tag -a v0.2.0-beta.1 -m "LocalMCPChatClient 0.2.0-beta.1"
git push origin v0.2.0-beta.1
```

タグのpushでCIが実行されます。build、test、2形式のpublish、バージョン検証、ハッシュ作成が成功すると、Releaseが自動公開されます。

## 公開後チェック

- GitHub ReleaseがDraftやPrereleaseになっていないこと
- ZIP、EXE、それぞれのSHA-256、アプリライセンス、第三者通知の6成果物が添付されていること
- `.sha256`のファイル名とハッシュが対応していること
- 単体EXEのプロパティに意図した製品・ファイルバージョンが表示されること
- Portable ZIPと単体EXEの双方で初回画面が起動すること
- Portable ZIP内とアプリ内のライセンス文書が欠けていないこと
