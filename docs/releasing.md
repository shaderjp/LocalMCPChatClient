# リリース手順

LocalMCPChatClientは、Gitタグとアプリのバージョンが一致した場合にGitHub ActionsからReleaseを公開します。

## バージョン情報

バージョンの情報源はリポジトリ直下の`Directory.Build.props`です。

```xml
<Version>0.1.0</Version>
<VersionPrefix>0.1.0</VersionPrefix>
<AssemblyVersion>0.1.0.0</AssemblyVersion>
<FileVersion>0.1.0.0</FileVersion>
<InformationalVersion>0.1.0</InformationalVersion>
```

リリースタグは`v<Version>`形式にします。例えばバージョン`0.1.0`のタグは`v0.1.0`です。タグと`Directory.Build.props`の値が一致しない場合、CIはReleaseを作成せず失敗します。

## Release成果物

| ファイル | 内容 |
|---|---|
| `LocalMCPChatClient-<version>-win-x64.zip` | self-contained PortableフォルダーのZIP |
| `LocalMCPChatClient-<version>-win-x64.zip.sha256` | ZIPのSHA-256 |
| `LocalMCPChatClient-<version>-win-x64.exe` | self-contained単体EXE |
| `LocalMCPChatClient-<version>-win-x64.exe.sha256` | EXEのSHA-256 |

モデル、`llama-server`、DB、ログはどの成果物にも含めません。

## リリース前チェック

1. `CHANGELOG.md`へ対象バージョンの変更内容を追加する
2. `Directory.Build.props`のバージョンを更新する
3. READMEと資料中の固定バージョン例を確認する
4. Releaseビルドとテストを実行する
5. Portable ZIPと単体EXEをローカル発行する
6. EXEのFileVersionとProductVersionを確認する
7. クリーンなWindows環境で初回セットアップを手動確認する

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
git tag -a v0.1.0 -m "LocalMCPChatClient 0.1.0"
git push origin v0.1.0
```

タグのpushでCIが実行されます。build、test、2形式のpublish、バージョン検証、ハッシュ作成が成功すると、Releaseが自動公開されます。

## 公開後チェック

- GitHub ReleaseがDraftやPrereleaseになっていないこと
- 4つの成果物が添付されていること
- `.sha256`のファイル名とハッシュが対応していること
- 単体EXEのプロパティに意図した製品・ファイルバージョンが表示されること
- Portable ZIPと単体EXEの双方で初回画面が起動すること
