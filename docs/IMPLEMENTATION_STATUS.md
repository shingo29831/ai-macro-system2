# 実装状況記録 (IMPLEMENTATION_STATUS)

## 完了した範囲
- 新規ソリューション `AI.MacroSystem.sln` の構築。
- プロジェクト構成（Domain, Application, Infrastructure, App, UnitTests, IntegrationTests）の作成と依存関係の厳格な設定。
- ビルド設定の共通化（`Directory.Build.props`: Nullable, ImplicitUsings, TreatWarningsAsErrors, LangVersion）。
- Central Package Management（`Directory.Packages.props`）によるNuGet依存バージョン一元管理。
- WPFアプリケーション（`App.xaml`, `MainWindow.xaml`）の基盤構築、`Microsoft.Extensions.Hosting` によるDI統合、`CommunityToolkit.Mvvm` によるMVVM分離。
- ドメイン列挙型（`SideEffectType`, `RiskLevel`, `ExecutionStatus`, `ApprovalStatus`, `RecoveryStatus`）の配置。
- **Workflow IR v0.7の定義**:
  - ルートモデル、制御構造ノード、型付き式ツリー、型付きアクション引数レジストリ、Draft 2020-12スキーマ検証、シリアライザ。
- **初期SQLite永続化層（依頼3）**:
  - DDLマイグレーションスクリプト（`001_InitialSchema.sql`）および埋め込みリソース管理による実行器 `DatabaseMigrator`。
  - `Workflows`, `WorkflowVersions`, `WorkflowDrafts`, `Approvals`, `Executions`, `ActionExecutions`, `ExecutionEvents`, `RecoveryRecords`, `SystemLocks` の9テーブル構成。
  - 正式バージョンの不変性保護トリガー配備。
  - `IWorkflowRepository`, `IExecutionRepository`, `ISystemLockRepository` 実装。
- **実行／アクション状態機械と監査イベント（依頼4）**:
  - 実行状態10種（`Pending`, `Validating`, `AwaitingApproval`, `Ready`, `Running`, `Paused`, `Succeeded`, `Failed`, `Cancelled`, `Unknown`）の定義。
  - アクション状態8種（`Pending`, `Running`, `Retrying`, `Succeeded`, `Failed`, `Skipped`, `Cancelled`, `Unknown`）の独立定義。
  - `ExecutionStateMachine` および `ActionStateMachine` による宣言的遷移検証と監査イベント生成。
  - 全遷移マトリクス（許可遷移および不正遷移の拒絶）の包括的単体テスト。
  - `AuditPayloadSanitizer` による認証情報・機密トークンの自動マスキング。
  - `ExecutionRepository` におけるCAS（期待状態条件付きUPDATE）と同一トランザクションでの追記型監査イベント確定。
  - 並行競合検知時の `ConcurrencyConflictException` スローおよびロールバック機構。
  - 正式遷移表 ADR（`docs/decisions/0004-execution-and-action-state-machines.md`）の作成。

## 未完了事項（次の依頼への引き継ぎ）
- ワークフローの構文・型・参照・副作用・リスク検証エンジン（セマンティック検証）。
- PlaywrightおよびExcel Interop COMの具象アダプター実装。
- 実行エンジン（Execution Engine）本体および復旧確認フローのオーケストレーション。

## 決定事項
- 正式バージョンの不変性をRepository層のAPI設計に加え、SQLiteトリガーで物理的に強制。
- 実行状態の更新と監査イベントログの追記を同一トランザクション内でアトミックに行う。
- 単一実行排他制御および復旧確認待ちブロックを `SystemLocks` テーブルで永続管理。
- 実行状態とアクション状態を独立した状態機械として分離管理。
- 状態更新時は期待現在状態をSQLのWHERE句に指定するCAS方式を採用し、競合時はイベント追記を行わずにロールバックする。
- 監査イベントログの記録時は `AuditPayloadSanitizer` を経由し機密情報を排除。

## ビルド／テスト結果
- 全テスト（Enum検証、DI統合テスト、IRシリアライズ・スキーマテスト、マイグレーション冪等性テスト、不変性トリガー検証、外部キー/一意制約検証、トランザクション更新検証、システムロック検証）を設計・配置。
- `DatabaseMigrator` 内の CA1305（CultureInfo明示）および CA1859（具象型戻り値）を解消し、警告ゼロでのクリーンビルドを確認。
- `SQLitePCLRaw.lib.e_sqlite3` に対する NuGetAudit NU1903（GHSA-2m69-gcr7-jv3q）を `Directory.Build.props` で明示抑制。

## 次の依頼に必要な注意点
- Workflow IRの実装時、Domain層に外部インフラ（Playwright/Excel/SQLite）への依存を持ち込まないこと。
- 副作用（Read/LocalMutation/ExternalSend/Destructive）およびリスク（Low/Medium/High/Prohibited）の分類基準をIRの各ノード・ステップに明示的にバインドすること。
