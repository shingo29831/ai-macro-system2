-- スキーママイグレーション履歴テーブル
CREATE TABLE IF NOT EXISTS __SchemaMigrations (
    Version INTEGER PRIMARY KEY,
    Description TEXT NOT NULL,
    AppliedAtUtc TEXT NOT NULL
);

-- ワークフロー親エンティティ
CREATE TABLE IF NOT EXISTS Workflows (
    WorkflowId TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Description TEXT NOT NULL DEFAULT '',
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

-- 正式不変ワークフローバージョン
CREATE TABLE IF NOT EXISTS WorkflowVersions (
    WorkflowId TEXT NOT NULL,
    Version INTEGER NOT NULL,
    WorkflowJson TEXT NOT NULL,
    RiskLevel TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    CreatedBy TEXT NOT NULL,
    PRIMARY KEY (WorkflowId, Version),
    FOREIGN KEY (WorkflowId) REFERENCES Workflows(WorkflowId) ON DELETE CASCADE
);

-- 正式バージョンの不変性保護トリガー (DB層強制)
CREATE TRIGGER IF NOT EXISTS trg_prevent_update_workflow_versions
BEFORE UPDATE ON WorkflowVersions
BEGIN
    SELECT RAISE(FAIL, 'Workflow versions are immutable and cannot be updated.');
END;

CREATE TRIGGER IF NOT EXISTS trg_prevent_delete_workflow_versions
BEFORE DELETE ON WorkflowVersions
BEGIN
    SELECT RAISE(FAIL, 'Workflow versions are immutable and cannot be deleted.');
END;

-- ワークフロードラフト (編集可能)
CREATE TABLE IF NOT EXISTS WorkflowDrafts (
    WorkflowId TEXT NOT NULL,
    DraftVersion INTEGER NOT NULL,
    WorkflowJson TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (WorkflowId, DraftVersion),
    FOREIGN KEY (WorkflowId) REFERENCES Workflows(WorkflowId) ON DELETE CASCADE
);

-- 承認記録
CREATE TABLE IF NOT EXISTS Approvals (
    ApprovalId TEXT PRIMARY KEY,
    WorkflowId TEXT NOT NULL,
    Version INTEGER NOT NULL,
    Status TEXT NOT NULL,
    ApprovedBy TEXT,
    ApprovedAtUtc TEXT,
    Notes TEXT,
    CreatedAtUtc TEXT NOT NULL,
    FOREIGN KEY (WorkflowId, Version) REFERENCES WorkflowVersions(WorkflowId, Version) ON DELETE RESTRICT
);

-- ワークフロー実行記録
CREATE TABLE IF NOT EXISTS Executions (
    ExecutionId TEXT PRIMARY KEY,
    WorkflowId TEXT NOT NULL,
    Version INTEGER NOT NULL,
    Status TEXT NOT NULL,
    TriggeredBy TEXT NOT NULL,
    StartedAtUtc TEXT,
    CompletedAtUtc TEXT,
    ErrorMessage TEXT,
    CreatedAtUtc TEXT NOT NULL,
    FOREIGN KEY (WorkflowId, Version) REFERENCES WorkflowVersions(WorkflowId, Version) ON DELETE RESTRICT
);

-- アクション実行記録 (ループ呼び出し区別対応)
CREATE TABLE IF NOT EXISTS ActionExecutions (
    ActionExecutionId TEXT PRIMARY KEY,
    ExecutionId TEXT NOT NULL,
    NodeId TEXT NOT NULL,
    ExecutionOrder INTEGER NOT NULL,
    IterationIndex INTEGER NOT NULL DEFAULT 0,
    ActionType TEXT NOT NULL,
    Status TEXT NOT NULL,
    ParametersJson TEXT NOT NULL,
    ResultJson TEXT,
    ErrorMessage TEXT,
    ErrorCategory TEXT NOT NULL DEFAULT 'None',
    StartedAtUtc TEXT NOT NULL,
    CompletedAtUtc TEXT,
    FOREIGN KEY (ExecutionId) REFERENCES Executions(ExecutionId) ON DELETE CASCADE,
    UNIQUE (ExecutionId, ExecutionOrder)
);

CREATE INDEX IF NOT EXISTS idx_action_executions_node
ON ActionExecutions (ExecutionId, NodeId, IterationIndex);

-- 監査・実行イベントログ
CREATE TABLE IF NOT EXISTS ExecutionEvents (
    EventId TEXT PRIMARY KEY,
    ExecutionId TEXT NOT NULL,
    SequenceNumber INTEGER NOT NULL,
    EventType TEXT NOT NULL,
    PayloadJson TEXT NOT NULL,
    TimestampUtc TEXT NOT NULL,
    FOREIGN KEY (ExecutionId) REFERENCES Executions(ExecutionId) ON DELETE CASCADE,
    UNIQUE (ExecutionId, SequenceNumber)
);

-- 異常終了時復旧記録
CREATE TABLE IF NOT EXISTS RecoveryRecords (
    RecoveryId TEXT PRIMARY KEY,
    ExecutionId TEXT NOT NULL,
    Reason TEXT NOT NULL,
    RecoveryStatus TEXT NOT NULL,
    ConfirmedBy TEXT,
    ConfirmedAtUtc TEXT,
    DetailsJson TEXT,
    CreatedAtUtc TEXT NOT NULL,
    FOREIGN KEY (ExecutionId) REFERENCES Executions(ExecutionId) ON DELETE RESTRICT
);

-- 単一ワークフロー排他制御・復旧待ちロックテーブル
CREATE TABLE IF NOT EXISTS SystemLocks (
    LockKey TEXT PRIMARY KEY,
    CurrentExecutionId TEXT,
    IsRecoveryPending INTEGER NOT NULL DEFAULT 0,
    AcquiredAtUtc TEXT,
    FOREIGN KEY (CurrentExecutionId) REFERENCES Executions(ExecutionId) ON DELETE SET NULL
);

-- グローバル排他ロック行の初期化
INSERT OR IGNORE INTO SystemLocks (LockKey, CurrentExecutionId, IsRecoveryPending, AcquiredAtUtc)
VALUES ('GlobalExecutionLock', NULL, 0, NULL);
