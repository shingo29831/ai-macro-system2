namespace AI.MacroSystem.UnitTests.Workflow;

using AI.MacroSystem.Application.Workflow.Validation;
using AI.MacroSystem.Application.Workflow.Validation.Settings;
using FluentAssertions;
using Xunit;

public class WorkflowValidatorTests
{
    private readonly WorkflowValidator _validator = new();

    [Fact]
    public void ValidBasicWorkflow_ProducesValidatedWorkflowDefinition()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "valid_workflow_basic.json");
        var json = File.ReadAllText(fixturePath);

        var result = _validator.Validate(json);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        result.DetailedErrors.Should().BeEmpty();
        result.ValidatedWorkflow.Should().NotBeNull();
        result.ValidatedWorkflow!.Definition.WorkflowId.Should().Be("wf-simple-001");
    }

    [Fact]
    public void ValidControlFlowWorkflow_PassesSemanticValidation()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "valid_workflow_control_flow.json");
        var json = File.ReadAllText(fixturePath);

        var result = _validator.Validate(json);

        result.IsValid.Should().BeTrue();
        result.ValidatedWorkflow.Should().NotBeNull();
    }

    [Fact]
    public void InvalidSchemaVersion_IsRejectedWithCorrectErrorCode()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "invalid_schema_version.json");
        var json = File.ReadAllText(fixturePath);

        var result = _validator.Validate(json);

        result.IsValid.Should().BeFalse();
        result.ValidatedWorkflow.Should().BeNull();
    }

    [Fact]
    public void UnknownActionType_IsRejectedWithMechanicalErrorCode()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "invalid_unknown_action_type.json");
        var json = File.ReadAllText(fixturePath);

        var result = _validator.Validate(json);

        result.IsValid.Should().BeFalse();
        result.ValidatedWorkflow.Should().BeNull();
        result.DetailedErrors.Should().Contain(e => e.ErrorCode == ValidationErrorCode.UnknownActionType || e.ErrorCode == ValidationErrorCode.SchemaViolation);
    }

    [Fact]
    public void DuplicateNodeId_IsRejected()
    {
        var json = """
        {
          "schemaVersion": "0.7",
          "workflowId": "wf-dup",
          "version": 1,
          "root": {
            "kind": "Sequence",
            "nodeId": "seq_root",
            "nodes": [
              { "kind": "Action", "nodeId": "step_1", "actionType": "Delay", "parameters": { "milliseconds": 100 } },
              { "kind": "Action", "nodeId": "step_1", "actionType": "Log", "parameters": { "message": "hello" } }
            ]
          }
        }
        """;

        var result = _validator.Validate(json);

        result.IsValid.Should().BeFalse();
        result.ValidatedWorkflow.Should().BeNull();
        result.DetailedErrors.Should().ContainSingle(e => e.ErrorCode == ValidationErrorCode.DuplicateNodeId && e.NodeId == "step_1");
    }

    [Fact]
    public void UndefinedVariableReference_IsRejected()
    {
        var json = """
        {
          "schemaVersion": "0.7",
          "workflowId": "wf-undef",
          "version": 1,
          "root": {
            "kind": "Action",
            "nodeId": "step_write",
            "actionType": "ExcelWriteCell",
            "parameters": {
              "workbookPath": "C:\\data.xlsx",
              "sheetName": "Sheet1",
              "cellAddress": "A1",
              "value": {
                "kind": "Variable",
                "variableName": "nonExistentVar"
              }
            }
          }
        }
        """;

        var result = _validator.Validate(json);

        result.IsValid.Should().BeFalse();
        result.ValidatedWorkflow.Should().BeNull();
        result.DetailedErrors.Should().Contain(e => e.ErrorCode == ValidationErrorCode.UndefinedVariable);
    }

    [Fact]
    public void VariableDefinedOnlyInThenBranch_IsRejectedWhenReferencedAfterwards()
    {
        var json = """
        {
          "schemaVersion": "0.7",
          "workflowId": "wf-branch-uncertain",
          "version": 1,
          "root": {
            "kind": "Sequence",
            "nodeId": "seq_root",
            "nodes": [
              {
                "kind": "If",
                "nodeId": "if_node",
                "condition": {
                  "kind": "Binary",
                  "operator": "Equal",
                  "left": { "kind": "Literal", "value": 1 },
                  "right": { "kind": "Literal", "value": 1 }
                },
                "thenBranch": {
                  "kind": "Action",
                  "nodeId": "read_step",
                  "actionType": "ExcelReadCell",
                  "parameters": {
                    "workbookPath": "C:\\book.xlsx",
                    "sheetName": "Sheet1",
                    "cellAddress": "B2",
                    "outputVariable": "branchVar"
                  }
                }
              },
              {
                "kind": "Action",
                "nodeId": "write_step",
                "actionType": "ExcelWriteCell",
                "parameters": {
                  "workbookPath": "C:\\book.xlsx",
                  "sheetName": "Sheet1",
                  "cellAddress": "C3",
                  "value": {
                    "kind": "Variable",
                    "variableName": "branchVar"
                  }
                }
              }
            ]
          }
        }
        """;

        var result = _validator.Validate(json);

        result.IsValid.Should().BeFalse();
        result.ValidatedWorkflow.Should().BeNull();
        result.DetailedErrors.Should().Contain(e => e.ErrorCode == ValidationErrorCode.BranchVariableUncertain);
    }

    [Fact]
    public void VariableDefinedInBothBranches_IsAllowedAfterwards()
    {
        var json = """
        {
          "schemaVersion": "0.7",
          "workflowId": "wf-both-branches",
          "version": 1,
          "root": {
            "kind": "Sequence",
            "nodeId": "seq_root",
            "nodes": [
              {
                "kind": "If",
                "nodeId": "if_node",
                "condition": {
                  "kind": "Binary",
                  "operator": "Equal",
                  "left": { "kind": "Literal", "value": 1 },
                  "right": { "kind": "Literal", "value": 1 }
                },
                "thenBranch": {
                  "kind": "Action",
                  "nodeId": "read_then",
                  "actionType": "ExcelReadCell",
                  "parameters": {
                    "workbookPath": "C:\\book.xlsx",
                    "sheetName": "Sheet1",
                    "cellAddress": "B2",
                    "outputVariable": "commonVar"
                  }
                },
                "elseBranch": {
                  "kind": "Action",
                  "nodeId": "read_else",
                  "actionType": "ExcelReadCell",
                  "parameters": {
                    "workbookPath": "C:\\book.xlsx",
                    "sheetName": "Sheet1",
                    "cellAddress": "B3",
                    "outputVariable": "commonVar"
                  }
                }
              },
              {
                "kind": "Action",
                "nodeId": "write_step",
                "actionType": "ExcelWriteCell",
                "parameters": {
                  "workbookPath": "C:\\book.xlsx",
                  "sheetName": "Sheet1",
                  "cellAddress": "C3",
                  "value": {
                    "kind": "Variable",
                    "variableName": "commonVar"
                  }
                }
              }
            ]
          }
        }
        """;

        var result = _validator.Validate(json);

        result.IsValid.Should().BeTrue();
        result.ValidatedWorkflow.Should().NotBeNull();
    }

    [Fact]
    public void ForEachItemVariable_CannotBeReferencedOutsideLoop()
    {
        var json = """
        {
          "schemaVersion": "0.7",
          "workflowId": "wf-loop-scope",
          "version": 1,
          "root": {
            "kind": "Sequence",
            "nodeId": "seq_root",
            "nodes": [
              {
                "kind": "ForEach",
                "nodeId": "loop_1",
                "itemVariable": "currentItem",
                "source": { "kind": "Literal", "value": ["a", "b"] },
                "body": {
                  "kind": "Action",
                  "nodeId": "log_item",
                  "actionType": "Log",
                  "parameters": { "message": "inside" }
                }
              },
              {
                "kind": "Action",
                "nodeId": "write_outside",
                "actionType": "ExcelWriteCell",
                "parameters": {
                  "workbookPath": "C:\\data.xlsx",
                  "sheetName": "Sheet1",
                  "cellAddress": "A1",
                  "value": {
                    "kind": "Variable",
                    "variableName": "currentItem"
                  }
                }
              }
            ]
          }
        }
        """;

        var result = _validator.Validate(json);

        result.IsValid.Should().BeFalse();
        result.ValidatedWorkflow.Should().BeNull();
        result.DetailedErrors.Should().Contain(e => e.ErrorCode == ValidationErrorCode.UndefinedVariable);
    }

    [Fact]
    public void WhileLoopExceedingSystemLimit_IsRejected()
    {
        var json = """
        {
          "schemaVersion": "0.7",
          "workflowId": "wf-while-limit",
          "version": 1,
          "root": {
            "kind": "While",
            "nodeId": "while_1",
            "maxIterations": 100000,
            "condition": {
              "kind": "Binary",
              "operator": "Equal",
              "left": { "kind": "Literal", "value": 1 },
              "right": { "kind": "Literal", "value": 1 }
            },
            "body": {
              "kind": "Action",
              "nodeId": "act_1",
              "actionType": "Delay",
              "parameters": { "milliseconds": 10 }
            }
          }
        }
        """;

        var result = _validator.Validate(json);

        result.IsValid.Should().BeFalse();
        result.ValidatedWorkflow.Should().BeNull();
        result.DetailedErrors.Should().Contain(e => e.ErrorCode == ValidationErrorCode.LoopLimitExceeded);
    }

    [Fact]
    public void MalformedAiOutput_WithInvalidTypesAndBrokenVariables_IsSafelyRejected()
    {
        var aiHallucinatedJson = """
        {
          "schemaVersion": "0.7",
          "workflowId": "wf-ai-hallucination",
          "version": 1,
          "root": {
            "kind": "Sequence",
            "nodeId": "seq_ai",
            "nodes": [
              {
                "kind": "Action",
                "nodeId": "act_unknown",
                "actionType": "ExecuteBashScript",
                "parameters": { "command": "rm -rf /" }
              },
              {
                "kind": "Action",
                "nodeId": "act_bad_delay",
                "actionType": "Delay",
                "parameters": { "milliseconds": -500 }
              }
            ]
          }
        }
        """;

        var result = _validator.Validate(aiHallucinatedJson);

        result.IsValid.Should().BeFalse();
        result.ValidatedWorkflow.Should().BeNull();
        result.DetailedErrors.Should().NotBeEmpty();
    }
}
