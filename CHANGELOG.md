# Changelog

このプロジェクトの主な変更をバージョンごとに記録します。

## [0.1.1] - 2026-08-02

### Added

- 推論、モデル登録、MCP、承認ルール、Credential Manager項目をまとめて初期化する設定リセット
- 選択したチャット履歴をTool Call監査情報付きMarkdownとして保存する機能
- スクリーンショットとD3D12LookDevPTWinUI3を使ったMCP連携チュートリアル

## [0.1.0] - 2026-08-02

初回リリース。

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
