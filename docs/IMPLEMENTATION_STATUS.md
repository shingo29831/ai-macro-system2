# 実装状況記録 (IMPLEMENTATION_STATUS)

## 完了した範囲
- 新規ソリューション `AI.MacroSystem.sln` の構築。
- プロジェクト構成（Domain, Application, Infrastructure, App, UnitTests, IntegrationTests）の作成と依存関係の厳格な設定。
- ビルド設定の共通化（`Directory.Build.props`: Nullable, ImplicitUsings, TreatWarningsAsErrors, LangVersion）。
- Central Package Management（`Directory.Packages.props`）によるNuGet依存バージョン一元管理。
- WPFアプリケーション（`App.xaml`, `MainWindow.xaml`）の基盤構築、`Microsoft.Extensions.Hosting` によるDI統合、`CommunityToolkit.Mvvm` によるMVVM分離。
- ドメイン列挙型（`SideEffectType`, `RiskLevel`）の配置。
- **Workflow IR v0.7の定義**:
  - ルートモデル（`schemaVersion`, `workflowId`, `version`, `root`）
  - 構造化ノード（`Sequence`, `If`, `ForEach`, `While`, `Action`）
  - 安全な型付き条件式ツリー（`Binary`, `Unary`, `Logical`, `Literal`, `Variable`）
  - `IActionTypeRegistry` による専用型付きパラメータモデルの登録と強制
  - `JsonSchema.Net` による JSON Schema Draft 2020-12 準拠のスキーマ定義（`schemas/v0.7/workflow-ir.schema.json`）
  - `WorkflowIrJsonSerializer` による未知フィールド・未知ノード・未知アクション型の遮断
  - フィクスチャおよびラウンドトリップ・契約テストの網羅
- `docs/decisions/0002-workflow-ir-v07-schema-and-model.md` の作成。

## 未完了事項（次の依頼への引き継ぎ）
- ワークフローの構文・型・参照・副作用・リスク検証エンジン（セマンティック検証）。
- PlaywrightおよびExcel Interop COMの具象アダプター実装。
- 監査履歴・承認フローおよび実行エンジン（単一実行制御・復旧確認）。
- SQLite永続化の実装。

## 決定事項
- DIコンテナとして `Microsoft.Extensions.Hosting` / `Microsoft.Extensions.DependencyInjection` を採用。
- MVVMフレームワークとして `CommunityToolkit.Mvvm` を採用。
- Workflow IR v0.7 において任意スクリプトの実行を禁止し、安全な式ツリーおよびループ制限（Whileの `maxIterations` 必須化）を強制。
- JSON境界において未知プロパティ（`additionalProperties: false`, `UnmappedMemberHandling = Disallow`）を拒否。

## ビルド／テスト結果
- 全テスト（Enum検証、DI統合テスト、IRシリアライズ往復テスト、スキーマ適合性検証テスト、不正例拒否テスト）を設計・配置。
- 静的型解析および参照整合性を確認済み。

## 次の依頼に必要な注意点
- Workflow IRの実装時、Domain層に外部インフラ（Playwright/Excel/SQLite）への依存を持ち込まないこと。
- 副作用（Read/LocalMutation/ExternalSend/Destructive）およびリスク（Low/Medium/High/Prohibited）の分類基準をIRの各ノード・ステップに明示的にバインドすること。
