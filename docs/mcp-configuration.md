# MCP設定ガイド

LocalMCPChatClientは、stdioとStreamable HTTPのMCPサーバーへ接続できます。旧SSE transport、OAuth、Resources、Promptsは初期版では対象外です。

## 設定を開く

1. メイン画面で「設定」を選択する
2. 「MCP接続」タブを開く
3. 「＋ stdio」または「＋ HTTP」を選択する
4. 接続情報を入力する
5. 「保存」を選択する

保存前に「接続テスト」で入力内容を確認できます。「接続」は選択中のプロファイルを現在のセッションで接続します。

## JSONからインポート

「MCP接続」タブの「JSONからインポート」から、既存ツールが出力したMCP設定JSONをそのまま読み込めます。ルートに`servers`または`mcpServers`オブジェクトを持つ形式に対応します。

```json
{
  "servers": {
    "localRenderer": {
      "type": "http",
      "url": "http://127.0.0.1:8777/mcp",
      "headers": {
        "Authorization": "Bearer <token>"
      }
    }
  }
}
```

インポート時の動作は次の通りです。

- `type: "http"`、`streamable-http`をStreamable HTTPとして読み込む
- `type: "stdio"`、または`command`を持つ設定をstdioとして読み込む
- `command`、`args`、`cwd`、`env`、`url`、`headers`、有効状態、タイムアウトを変換する
- 同じ表示名の既存設定がある場合は、サーバーIDを維持したまま置き換える
- Authorization、名前にtoken・secret・password・API keyなどを含む値は、Windows Credential Managerへ保存する
- `Authorization: "Bearer ${env:TOKEN_NAME}"`は「Bearerトークンの環境変数名」へ変換する
- 旧SSEなど未対応の設定は実行せず、インポート結果の警告として表示する
- JSONから読み込んだHTTP接続では、明示指定がない限りstandalone GETを無効にする

秘密値をCredential Managerへ移しても、インポート元のJSONファイル自体は変更されません。Authorizationやトークンを含むファイルは、インポート後も安全な場所で管理してください。

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

MCP C# SDK 2.1.0が最新のプロトコルを優先し、旧サーバーには自動的にフォールバックするため、通常は`MCP-Protocol-Version`を指定しません。旧サーバー向けに明示した場合は、一般の追加ヘッダーとして重複送信せず、SDKのプロトコル固定値として扱います。

## D3D12LookDevPTの設定

[shaderjp/D3D12LookDevPT](https://github.com/shaderjp/D3D12LookDevPT)は、Direct3D 12 / DXR LookDevパストレーサーを操作する別アプリです。実行中のレンダラーを参照・操作するStreamable HTTP形式のローカルMCPサーバーを備えています。

![D3D12LookDevPTのレンダラー画面とMCP Serverパネル](images/image004.png)

4枚目の画像は接続先となるMCPサーバーアプリの画面であり、LocalMCPChatClientの配布物には含まれません。ビルド、アセット、GPU要件は同リポジトリの[日本語README](https://github.com/shaderjp/D3D12LookDevPT/blob/main/README.ja.md)を確認してください。

同サーバーの現在の仕様では、`127.0.0.1`だけで待ち受け、`POST /mcp`、Bearer認証、MCP Protocol `2026-07-28`、`2025-11-25`、`2025-06-18`に対応します。LocalMCPChatClientは最新のstateless方式を自動交渉します。resource subscription用SSEは`POST subscriptions/listen`のresponseであり、standalone `GET /mcp`は実装されていないため、この接続では「standalone GET」を無効にします。

### サーバー側の準備

1. D3D12LookDevPTを起動する
2. 「MCP Server」パネルを開く
3. Portを`8777`、Request Timeoutを`120`秒にする
4. Access Modeを選択する。最初は変更操作をサーバー側でも確認できる`confirm_mutations`を推奨
5. 「Copy Token」でBearerトークンを取得する
6. 「Start Server」を選択し、表示が`http://127.0.0.1:8777/mcp`になったことを確認する

トークンはREADME、スクリーンショット、チャット、Git管理ファイルへ記載しないでください。環境変数方式を使う場合は、Windowsのユーザー環境変数`D3D12LOOKDEVPT_MCP_TOKEN`へトークンだけを設定し、LocalMCPChatClientを完全に終了してから起動し直します。

環境変数を使わない場合は、「Bearerトークンの環境変数名」を空にし、「秘密のHTTPヘッダー」へ`Authorization=Bearer <token>`を入力できます。この値はWindows Credential Managerへ保存されます。2つの方式は同時に設定しないでください。

### クライアント側の入力

D3D12LookDevPTリポジトリにある[`config/LocalMCPChatClient.mcp.json`](https://github.com/shaderjp/D3D12LookDevPT/blob/main/config/LocalMCPChatClient.mcp.json)を「JSONからインポート」で読み込むと、次の設定が追加されます。

```toml
[mcp_servers.d3d12_lookdev_pt]
url = "http://127.0.0.1:8777/mcp"
bearer_token_env_var = "D3D12LOOKDEVPT_MCP_TOKEN"
http_headers = {}
enabled = true
startup_timeout_sec = 10
tool_timeout_sec = 120
```

| UI項目 | 入力値 |
|---|---|
| 種類 | `＋ HTTP` |
| 表示名 | `D3D12LookDevPT` |
| 有効 | オン |
| Streamable HTTP URL | `http://127.0.0.1:8777/mcp` |
| standalone GET | オフ |
| Content-Length | オフのままで可 |
| Bearerトークンの環境変数名 | `D3D12LOOKDEVPT_MCP_TOKEN` |
| HTTPヘッダー | 追加なし |
| 接続開始タイムアウト | `10` |
| ツール実行タイムアウト | `120` |

トークンそのものを「Bearerトークンの環境変数名」欄へ貼り付けないでください。「接続テスト」後、「保存」を選びます。接続できると、メイン画面右上のMCP表示に接続数と取得したTool数が反映されます。利用できるToolはD3D12LookDevPTのビルドに依存するため、画面に表示された数を確認してください。

アプリ内部の保存形式の参考例は[mcp-profile-examples.json](mcp-profile-examples.json)にも収録しています。インポート対象は、この内部形式ではなく、ルートに`servers`または`mcpServers`を持つ外部ツール向け形式です。

サーバーの起動からチャットで露出を変更するまでの詳しい流れは[D3D12LookDevPTとの連携例](d3d12lookdevpt-integration.md)を参照してください。サーバー側のTools、Resources、Promptsの完全な一覧は、同リポジトリの[MCPサーバー文書](https://github.com/shaderjp/D3D12LookDevPT/blob/main/docs/mcp.ja.md)が正です。LocalMCPChatClientの初期版は、そのうちToolsだけをモデルへ公開します。

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
