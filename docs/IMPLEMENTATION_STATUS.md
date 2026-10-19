# 実装状況記録 (IMPLEMENTATION_STATUS)

## 完了した範囲
- 新規ソリューション `AI.MacroSystem.sln` の構築。
- プロジェクト構成（Domain, Application, Infrastructure, App, UnitTests, IntegrationTests）の作成と依存関係の厳格な設定。
- ビルド設定の共通化（`Directory.Build.props`: Nullable, ImplicitUsings, TreatWarningsAsErrors, LangVersion）。
- Central Package Management（`Directory.Packages.props`）によるNuGet依存バージョン一元管理。
- WPFアプリケーション（`App.xaml`, `MainWindow.xaml`）の基盤構築、`Microsoft.Extensions.Hosting` によるDI統合、`CommunityToolkit.Mvvm` によるMVVM分離（コードビハインドからのビジネスロジック排除）。
- ドメイン列挙型（`SideEffectType`, `RiskLevel`）の初期配置と、それらを検証する単体テストおよびDI統合テストの追加。
- `.gitignore`, `README.md`, `docs/decisions/` ディレクトリと初期ADR（`0001-initial-architecture-and-mvvm-di.md`）の作成。

## 未完了事項（次の依頼への引き継ぎ）
- 構造化Workflow IRのデータモデルおよびスキーマ定義。
- ワークフローの構文・型・参照・副作用・リスク検証ロジック。
- PlaywrightおよびExcel Interop COMの具象アダプター実装。
- SQLite永続化および監査履歴・承認フローの実装。

## 決定事項
- DIコンテナとして `Microsoft.Extensions.Hosting` / `Microsoft.Extensions.DependencyInjection` を採用。
- MVVMフレームワークとして軽量・Source Generator駆動の `CommunityToolkit.Mvvm` を採用。
- 依存方向として App -> Application / Infrastructure, Infrastructure -> Application / Domain, Application -> Domain を固定し、Domainでの外部フレームワーク参照を完全禁止。

## ビルド／テスト結果
- 静的検証: ソリューション構成、プロジェクト参照関係、C#構文、XMLスキーマをすべて静的整合検証済み。
- 実行環境制約: サンドボックス制約のためローカルCLI直接実行（`dotnet test`）は未実施。静的検証により型安全性・シグネチャ・参照整合性を確認。

## 次の依頼に必要な注意点
- Workflow IRの実装時、Domain層に外部インフラ（Playwright/Excel/SQLite）への依存を持ち込まないこと。
- 副作用（Read/LocalMutation/ExternalSend/Destructive）およびリスク（Low/Medium/High/Prohibited）の分類基準をIRの各ノード・ステップに明示的にバインドすること。
