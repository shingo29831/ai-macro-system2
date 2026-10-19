# AI.MacroSystem

Windows 11 x64 向けの検証ドリブン型・高信頼マクロオートメーションデスクトップアプリケーション。

## アーキテクチャと依存方向

クリーンアーキテクチャの原則に従い、外側から内側への単方向依存を徹底しています。

```
AI.MacroSystem.App (net10.0-windows)
  ├──> AI.MacroSystem.Application (net10.0)
  │      └──> AI.MacroSystem.Domain (net10.0)
  └──> AI.MacroSystem.Infrastructure (net10.0)
         ├──> AI.MacroSystem.Application
         └──> AI.MacroSystem.Domain

AI.MacroSystem.UnitTests (net10.0)
  ├──> AI.MacroSystem.Domain
  └──> AI.MacroSystem.Application

AI.MacroSystem.IntegrationTests (net10.0-windows)
  ├──> AI.MacroSystem.Domain
  ├──> AI.MacroSystem.Application
  ├──> AI.MacroSystem.Infrastructure
  └──> AI.MacroSystem.App
```

## プロジェクト責務

| プロジェクト | ターゲット | 責務 |
| :--- | :--- | :--- |
| `AI.MacroSystem.Domain` | `net10.0` | ビジネスエンティティ、値オブジェクト、ドメイン列挙型（副作用・リスク分類等）、不変条件。外部ライブラリ非依存。 |
| `AI.MacroSystem.Application` | `net10.0` | ユースケース調整、ワークフロー検証・実行の抽象契約、DI拡張。 |
| `AI.MacroSystem.Infrastructure` | `net10.0` | 外部サービス具象実装（SQLite、Playwright、Excel Interop COM等）。 |
| `AI.MacroSystem.App` | `net10.0-windows` | WPFデスクトップUI、MVVM、ホスト初期化、DI構成、ウィンドウ管理。 |
| `AI.MacroSystem.UnitTests` | `net10.0` | ドメインロジックおよびアプリケーション単体テスト。 |
| `AI.MacroSystem.IntegrationTests` | `net10.0-windows` | DIコンテナ解決および複数プロジェクト間連携テスト。 |

## ビルドおよびテスト手順

```bash
# クリーンビルド
dotnet build AI.MacroSystem.sln -c Debug

# 単体テストおよび統合テストの実行
dotnet test AI.MacroSystem.sln --no-build
```
