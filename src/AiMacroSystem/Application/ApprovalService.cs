// Module: @role: 実行計画承認レコード照合・高リスク個別承認の整合性検証
using System;
using System.Linq;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Application;

public sealed class ApprovalService
{
    public (bool IsApproved, string Reason) VerifyApprovalForPlan(ExecutionPlan plan, ApprovalRecord? approval)
    {
        if (plan.PlannedActions.Any(a => a.EvaluatedRisk == RiskLevel.Prohibited))
        {
            return (false, "Prohibited operations cannot be executed even with approval.");
        }

        if (approval is null)
        {
            return (false, "Workflow execution requires explicit prior approval.");
        }

        if (approval.Decision != ApprovalDecision.Approved)
        {
            return (false, "The workflow execution plan was rejected.");
        }

        if (!string.Equals(approval.VersionId, plan.VersionId, StringComparison.Ordinal))
        {
            return (false, $"Approval version mismatch (approved '{approval.VersionId}', current '{plan.VersionId}').");
        }

        if (!string.Equals(approval.PlanHash, plan.PlanHash, StringComparison.Ordinal))
        {
            return (false, "Execution plan has changed since approval. Re-approval is required.");
        }

        var requiredIndividualNodes = plan.PlannedActions
            .Where(a => a.RequiresIndividualApproval)
            .Select(a => a.NodeId)
            .ToList();

        foreach (string highRiskNodeId in requiredIndividualNodes)
        {
            if (!approval.ApprovedHighRiskNodeIds.Contains(highRiskNodeId, StringComparer.Ordinal))
            {
                return (false, $"High-risk action node '{highRiskNodeId}' requires explicit individual approval.");
            }
        }

        return (true, "Approved");
    }
}
