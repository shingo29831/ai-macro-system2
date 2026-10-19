# ADR 0004: 実行およびアクション状態機械と監査イベント

## ステータス
承認済 (Accepted)

## コンテキスト
ワークフローの安全性と復旧性を保証するため、ワークフロー実行単位およびアクション実行単位のライフサイクルを明示的な状態機械（State Machine）として分離定義する必要がある。
また、並行実行競合やクラッシュ時の不整合を防ぐため、期待状態を条件にしたCAS（Compare-And-Swap）更新と追記型監査イベントログの同一トランザクションでの確定が求められる。

## 決定事項

### 1. ワークフロー実行状態機械 (Execution State Machine)
状態一覧: `Pending`, `Validating`, `AwaitingApproval`, `Ready`, `Running`, `Paused`, `Succeeded`, `Failed`, `Cancelled`, `Unknown`

| 遷移元 (From) | 遷移先 (To) | トリガー (Trigger) | 監査イベント (Audit Event) | ガード条件・説明 |
|---|---|---|---|---|
| `Pending` | `Validating` | `StartValidation` | `ExecutionValidationStarted` | 構文・スキーマ・リスク検証開始 |
| `Pending` | `Cancelled` | `Cancel` | `ExecutionCancelled` | 実行前キャンセル要求 |
| `Validating` | `Ready` | `ValidationPassedAutoApprove` | `ExecutionValidationPassed` | 検証合格かつ承認不要または既承認 |
| `Validating` | `AwaitingApproval` | `ValidationPassedRequireApproval` | `ExecutionApprovalRequested` | 検証合格かつ高リスク等のため承認待ち |
| `Validating` | `Failed` | `ValidationFailed` | `ExecutionValidationFailed` | 構文・参照・型検証エラー検知 |
| `Validating` | `Cancelled` | `Cancel` | `ExecutionCancelled` | 検証中キャンセル要求 |
| `AwaitingApproval` | `Ready` | `Approve` | `ExecutionApproved` | 明示的承認の受領 |
| `AwaitingApproval` | `Failed` | `RejectApproval` | `ExecutionApprovalRejected` | 承認拒絶または却下 |
| `AwaitingApproval` | `Cancelled` | `Cancel` | `ExecutionCancelled` | 承認待ち中キャンセル要求 |
| `Ready` | `Running` | `StartExecution` | `ExecutionStarted` | 単一実行排他ロック取得完了と実行開始 |
| `Ready` | `Cancelled` | `Cancel` | `ExecutionCancelled` | 実行直前キャンセル要求 |
| `Running` | `Paused` | `Pause` | `ExecutionPaused` | 一時停止要求の受領 |
| `Running` | `Succeeded` | `CompleteSuccess` | `ExecutionSucceeded` | 全ノード正常終了 |
| `Running` | `Failed` | `FailExecution` | `ExecutionFailed` | 致命的障害または回復不能エラー |
| `Running` | `Unknown` | `FaultUnknown` | `ExecutionFaultedUnknown` | 副作用成否不明またはプロセス切断 |
| `Running` | `Cancelled` | `Cancel` | `ExecutionCancelled` | 実行中中断要求 |
| `Paused` | `Running` | `Resume` | `ExecutionResumed` | 一時停止からの再開 |
| `Paused` | `Cancelled` | `Cancel` | `ExecutionCancelled` | 一時停止中キャンセル要求 |

- 終端状態 (`Succeeded`, `Failed`, `Cancelled`, `Unknown`) からの遷移はすべて拒絶される。

### 2. アクション状態機械 (Action State Machine)
状態一覧: `Pending`, `Running`, `Retrying`, `Succeeded`, `Failed`, `Skipped`, `Cancelled`, `Unknown`

| 遷移元 (From) | 遷移先 (To) | トリガー (Trigger) | 監査イベント (Audit Event) | ガード条件・説明 |
|---|---|---|---|---|
| `Pending` | `Running` | `Start` | `ActionStarted` | アクション試行の開始 |
| `Pending` | `Skipped` | `Skip` | `ActionSkipped` | 条件分岐等によるスキップ判定 |
| `Pending` | `Cancelled` | `Cancel` | `ActionCancelled` | 親ワークフロー中止等によるキャンセル |
| `Running` | `Succeeded` | `CompleteSuccess` | `ActionSucceeded` | アクション試行の正常完了 |
| `Running` | `Retrying` | `ScheduleRetry` | `ActionRetryScheduled` | 失敗検知かつ副作用安全・再試行上限未満 |
| `Running` | `Failed` | `Fail` | `ActionFailed` | 失敗確定（上限到達または非再試行エラー） |
| `Running` | `Unknown` | `FaultUnknown` | `ActionFaultedUnknown` | 外部副作用成否不明状態の確定 |
| `Running` | `Cancelled` | `Cancel` | `ActionCancelled` | アクション実行中の中断要求 |
| `Retrying` | `Running` | `StartRetry` | `ActionRetrying` | 待機インターバル完了後の再試行開始 |
| `Retrying` | `Failed` | `Fail` | `ActionFailed` | 再試行待機中の致命的障害または期限到達 |
| `Retrying` | `Cancelled` | `Cancel` | `ActionCancelled` | 再試行待機中のキャンセル |

- 終端状態 (`Succeeded`, `Failed`, `Skipped`, `Cancelled`, `Unknown`) からの遷移はすべて拒絶される。

### 3. 機密情報保護とログ監査
- `AuditPayloadSanitizer` により、パスワード、APIキー、トークン等の機密フィールドを再帰的に `***REDACTED***` に置換して記録する。
- エラー分類は `ActionErrorCategory`（ValidationError, Timeout, TargetNotFound, NetworkOrIO, SecurityViolation, SystemOrInternal, Unknown）として型付き記録する。

### 4. 永続化とアトミック性
- `TransitionExecutionStatusAsync` および `TransitionActionExecutionStatusAsync` は、`WHERE ... AND Status = @ExpectedStatus` によるCAS更新を実施する。
- 更新件数が0件の場合は `ConcurrencyConflictException` をスローし、ロールバックによりイベントログのみが追記される不整合を物理的に防止する。
