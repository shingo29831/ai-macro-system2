namespace AI.MacroSystem.Infrastructure.Persistence.Repositories;

using AI.MacroSystem.Domain.Models;
using Microsoft.Data.Sqlite;

public interface IWorkflowRepository
{
    Task AddWorkflowAsync(WorkflowRecord workflow, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);
    Task<WorkflowRecord?> GetWorkflowAsync(string workflowId, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowRecord>> GetAllWorkflowsAsync(SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);

    Task AddVersionAsync(WorkflowVersionRecord version, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);
    Task<WorkflowVersionRecord?> GetVersionAsync(string workflowId, int version, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowVersionRecord>> GetVersionsAsync(string workflowId, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);

    Task SaveDraftAsync(WorkflowDraftRecord draft, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);
    Task<WorkflowDraftRecord?> GetDraftAsync(string workflowId, int draftVersion, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);
    Task DeleteDraftAsync(string workflowId, int draftVersion, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);

    Task AddApprovalAsync(ApprovalRecord approval, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);
    Task<ApprovalRecord?> GetApprovalAsync(string approvalId, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default);
}
