# MCP設定ガイド

LocalMCPChatClientは、stdioとStreamable HTTPのMCPサーバーへ接続できます。旧SSE transport、OAuth、Resources、Promptsは初期版では対象外です。

## 設定を開く

1. メイン画面で「設定」を選択する
2. 「MCP」タブを開く
3. 「＋ stdio」または「＋ HTTP」を選択する
4. 接続情報を入力する
5. 「保存して再接続」を選択する

保存前に「接続テスト」で入力内容を確認できます。「接続」は選択中のプロファイルを現在のセッションで接続します。

## 共通項目

| 項目 | 説明 |
|---|---|
| 有効 | アプリ起動時に自動接続するか |
| 表示名 | UIに表示する任意の名前 |
| 接続開始タイムアウト | 初期化とTools一覧取得の待機秒数 |
| ツール実行タイムアウト | 1回のツール呼び出しの上限秒数 |

サーバーIDは内部で一意に管理されます。モデルへ渡すツール名は`<serverId>__<toolName>`形式になり、異なるサーバーに同名ツールがあっても衝突しません。

## stdioサーバー

| 項目 | 入力例 |
|---|---|
| stdio command | `npx`、`python`、または実行ファイルの完全パス |
| 引数 | 1行に1引数 |
| 作業ディレクトリ | サーバーを起動するディレクトリ |
| 環境変数 | `NAME=VALUE`を1行ずつ |
| 秘密の環境変数 | 保存後にWindows Credential Managerへ移す値 |

コマンドと引数はシェル文字列として連結されません。例えば次のコマンドは、各要素を別々に入力します。

```text
npx -y @modelcontextprotocol/server-filesystem D:\Work
```

```text
stdio command:
npx

引数:
-y
@modelcontextprotocol/server-filesystem
D:\Work
```

stdioサーバーへ渡す環境変数は制限された既定セットと、プロファイルへ明示した値から構成されます。

## Streamable HTTPサーバー

| 項目 | 説明 |
|---|---|
| URL | MCPエンドポイント。リモートはHTTPS必須、HTTPはループバックだけ許可 |
| standalone GET | サーバー通知用のGETストリームを使う場合に有効化 |
| Content-Length | chunked requestを受け付けないサーバーで有効化 |
| Bearerトークンの環境変数名 | 値ではなく、トークンを保持する環境変数の名前 |
| HTTPヘッダー | `NAME=VALUE`を1行ずつ入力 |
| 秘密のHTTPヘッダー | Windows Credential Managerへ保存するヘッダー |

「Bearerトークンの環境変数名」を使う場合、接続時にその環境変数を読み取り、値へ`Bearer `が付いていなければ自動付与して`Authorization`ヘッダーを作ります。環境変数を追加・変更した後は、LocalMCPChatClientを再起動してください。

## D3D12 Lookdev PTの設定

次のMCPサーバー設定に対応するUI入力値です。

```toml
[mcp_servers.d3d12_lookdev_pt]
url = "http://127.0.0.1:8777/mcp"
bearer_token_env_var = "D3D12LOOKDEVPT_MCP_TOKEN"
http_headers = { "MCP-Protocol-Version" = "2025-11-25" }
enabled = true
startup_timeout_sec = 10
tool_timeout_sec = 120
```

| UI項目 | 入力値 |
|---|---|
| 種類 | `＋ HTTP` |
| 表示名 | `D3D12 Lookdev PT` |
| 有効 | オン |
| Streamable HTTP URL | `http://127.0.0.1:8777/mcp` |
| Bearerトークンの環境変数名 | `D3D12LOOKDEVPT_MCP_TOKEN` |
| HTTPヘッダー | `MCP-Protocol-Version=2025-11-25` |
| 接続開始タイムアウト | `10` |
| ツール実行タイムアウト | `120` |

トークンはD3D12 Lookdev PTサーバーの手順に従って環境変数`D3D12LOOKDEVPT_MCP_TOKEN`へ設定します。トークンそのものを「Bearerトークンの環境変数名」欄へ貼り付けないでください。

保存形式の参考例は[mcp-profile-examples.json](mcp-profile-examples.json)にも収録しています。このJSONファイルは資料用であり、現在のUIにはJSONインポート機能はありません。

## 秘密情報の扱い

- 通常の「環境変数」「HTTPヘッダー」に入力した値は`settings.json`へ平文保存されます。
- 「秘密の環境変数」「秘密のHTTPヘッダー」に入力した値はWindows Credential Managerへ保存されます。
- `settings.json`には秘密値ではなく`secretRef`だけが残ります。
- Bearerトークン環境変数方式では、トークン値はプロファイルへ保存されません。

秘密情報をプロンプト、ツール引数、作業ディレクトリ名へ含めないでください。

## ツール実行時の安全境界

1. 接続済みサーバーからToolsを取得する
2. モデルがツール名とJSON引数を生成する
3. 引数をツールのJSON Schemaで検証する
4. 保存済み承認ルールを評価し、必要なら確認画面を表示する
5. 許可された呼び出しだけを実行する
6. 結果をモデルと画面へ返す

1ターンの推論・ツール反復は最大8回です。ツール結果は最大256 KiBで切り詰められます。複数のTool Callは順番に承認・実行されます。
