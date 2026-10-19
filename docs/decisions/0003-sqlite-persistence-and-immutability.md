# 3. SQLite初期DDL設計・不変性トリガー・排他制御ロック方式

## ステータス
承認済み (Accepted)

## コンテキスト
マクロシステムの監査証跡、正式バージョンの再現性、異常終了時の安全停止を担保するため、SQLiteによる高信頼な永続化層を構築する必要がある。
特に「正式ワークフローバージョンは不変」「初期版では同時に1ワークフローのみ実行」「異常終了後は復旧確認が終わるまで新規実行を禁止する」という要件をDB層でも確実に強制しなければならない。

## 決定事項
1. **テーブル構成**:
   - `Workflows`, `WorkflowVersions`, `WorkflowDrafts`, `Approvals`, `Executions`, `ActionExecutions`, `ExecutionEvents`, `RecoveryRecords` の8テーブルと、排他制御用の `SystemLocks` を定義。
   - `Microsoft.Data.Sqlite` を採用し、全接続で `PRAGMA foreign_keys = ON;` を強制。
   - UTC日時は ISO 8601 文字列形式（`TEXT`, Roundtrip "o"）で統一。
2. **正式バージョンの不変性保護**:
   - `WorkflowVersions` に対する UPDATE および DELETE を拒否するトリガー（`trg_prevent_update_workflow_versions`, `trg_prevent_delete_workflow_versions`）をDB層に配備。
   - Repository APIにもバージョンの更新・削除メソッドを定義しないことで、アプリケーション層とDB層の二重防御を実現。
3. **ループ反復下でのアクション一意化**:
   - `ActionExecutions` に全体シーケンス番号 `ExecutionOrder` およびループ反復番号 `IterationIndex` を設け、`(ExecutionId, ExecutionOrder)` の一意制約を設定。
   - これにより、同一NodeIdがループ内で複数回実行されても完全に区別・追跡可能とする。
4. **単一ワークフロー排他制御と復旧待ちロック**:
   - `SystemLocks` テーブルに `GlobalExecutionLock` レコードを配置。
   - `CurrentExecutionId IS NULL AND IsRecoveryPending = 0` の条件下でのみアトミックに実行ロックを取得。
   - 異常終了時は `IsRecoveryPending = 1` としてロックを解放し、明示的な復旧確認（`ClearRecoveryPendingAsync`）が行われるまで次のワークフロー実行を物理的に遮断。
5. **同一トランザクションでの状態とイベントの更新**:
   - `IExecutionRepository.UpdateExecutionStatusAndAppendEventAsync` により、現在ステータス変更と監査イベント追記を同一トランザクション内でアトミックにコミットする設計を採用。

## 理由
- SQLiteのトリガー機能を用いることで、将来のリファクタリングや直接的なSQLクエリによる偶発的なIRバージョン改変を完全に遮断できるため。
- 単一実行ロックと復旧待ちフラグをDB永続化することで、プロセス再起動やクラッシュが発生しても安全停止状態が確実に維持されるため。

## 代替案
- **インメモリロック（SemaphoreSlim等）**: プロセス終了時に状態が消失し、クラッシュ後の復旧確認前新規実行を防止できないため不採用。
- **バージョン削除の論理削除フラグ**: 誤操作による変更リスクが残るため、DBトリガーによる物理的拒否を採用。

## 影響
- バージョン確定後のWorkflow IRは完全に読み取り専用となる（修正時は新バージョンまたはドラフトを作成する）。
- テスト実行時はインメモリ共有キャッシュ接続を利用することで、テスト分離性と高速性を両立する。
