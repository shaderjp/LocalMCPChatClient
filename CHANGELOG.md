# Changelog

このプロジェクトの主な変更をバージョンごとに記録します。

## [0.1.2] - 2026-08-03

### Added

- 起動後のバックグラウンドモデル準備とGPUウォームアップ
- ハードウェアと速度診断結果に基づくAutoバックエンド選択
- CUDA/Vulkanを比較できるキャンセル可能な速度診断
- 初回応答時間、token使用量、生成速度、使用バックエンドの計測と表示

### Changed

- `llama-server`のGPUオフロード、スレッド、batch、KV cache、prompt cache設定を実行環境に合わせて最適化
- ストリーミング表示を50ms単位で集約し、生成完了時にだけMarkdownを構築するよう変更
- Gemmaの非表示思考が出力上限を消費しないよう、最終回答生成を明示

## [0.1.1] - 2026-08-03

### Added

- 推論、モデル登録、MCP、承認ルール、Credential Manager項目をまとめて初期化する設定リセット
- 選択したチャット履歴をTool Call監査情報付きMarkdownとして保存する機能
- スクリーンショットとD3D12LookDevPTWinUI3を使ったMCP連携チュートリアル
- 設定画面からアプリ本体と第三者ソフトウェアのライセンスを表示する機能

### Fixed

- `0.1.0`のPortable ZIPに通知ファイルがなく、単体EXEにも表示手段がなかった問題を修正
- Portable ZIPへ.NET/WPF、MCP C# SDK、SQLite関連のライセンスと通知を同梱し、Releaseにも通知ファイルを添付

## [0.1.0] - 2026-08-02

初回リリース。

> 配布物に第三者ライセンス通知の同梱漏れがあります。修正版の`0.1.1`以降を使用してください。

### Added

- Gemma 4 E2B/E4Bによるローカルテキストチャット
- CPU、NVIDIA CUDA、Vulkan対応の`llama-server`管理
- stdioおよびStreamable HTTPのMCPクライアント
- Tool Callの実行前承認と承認ルール保存
- SQLite会話履歴とMarkdown表示
- モデル・ランタイムのダウンロード、検証、インポート
- Windows Credential Managerを利用した秘密情報管理
- Portable ZIPとself-contained単体EXEのWindows x64配布

[0.1.0]: https://github.com/shaderjp/LocalMCPChatClient/releases/tag/v0.1.0
[0.1.1]: https://github.com/shaderjp/LocalMCPChatClient/releases/tag/v0.1.1
[0.1.2]: https://github.com/shaderjp/LocalMCPChatClient/releases/tag/v0.1.2
