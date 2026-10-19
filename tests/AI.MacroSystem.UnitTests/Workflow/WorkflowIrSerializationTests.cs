namespace AI.MacroSystem.UnitTests.Workflow;

using AI.MacroSystem.Application.Workflow.Serialization;
using AI.MacroSystem.Domain.Workflow;
using AI.MacroSystem.Domain.Workflow.Actions;
using AI.MacroSystem.Domain.Workflow.Expressions;
using AI.MacroSystem.Domain.Workflow.Nodes;
using FluentAssertions;
using Xunit;

public class WorkflowIrSerializationTests
{
    private readonly WorkflowIrJsonSerializer _serializer = new();

    [Fact]
    public void RoundTripSerializationSucceedsForBasicWorkflow()
    {
        var workflow = new WorkflowDefinition
        {
            SchemaVersion = "0.7",
            WorkflowId = "wf-roundtrip-1",
            Version = 1,
            Name = "Roundtrip Test",
            Description = "Test description",
            Root = new SequenceNode
            {
                NodeId = "seq_1",
                Nodes =
                [
                    new ActionNode
                    {
                        NodeId = "act_1",
                        ActionType = "Delay",
                        Parameters = new DelayParameters { Milliseconds = 250 },
                    },
                ],
            },
        };

        var json = _serializer.Serialize(workflow);
        var deserialized = _serializer.Deserialize(json);

        deserialized.Should().NotBeNull();
        deserialized.SchemaVersion.Should().Be("0.7");
        deserialized.WorkflowId.Should().Be("wf-roundtrip-1");
        deserialized.Version.Should().Be(1);
        deserialized.Root.Should().BeOfType<SequenceNode>();

        var seq = (SequenceNode)deserialized.Root;
        seq.Nodes.Should().HaveCount(1);
        var act = seq.Nodes[0].Should().BeOfType<ActionNode>().Subject;
        act.ActionType.Should().Be("Delay");
        act.Parameters.Should().BeOfType<DelayParameters>().Which.Milliseconds.Should().Be(250);
    }

    [Fact]
    public void RoundTripSerializationSucceedsForControlFlowWorkflow()
    {
        var workflow = new WorkflowDefinition
        {
            SchemaVersion = "0.7",
            WorkflowId = "wf-roundtrip-control",
            Version = 2,
            Root = new SequenceNode
            {
                NodeId = "seq_root",
                Nodes =
                [
                    new IfNode
                    {
                        NodeId = "if_1",
                        Condition = new BinaryConditionExpression
                        {
                            Operator = BinaryOperator.Equal,
                            Left = new VariableExpression { VariableName = "status" },
                            Right = new LiteralExpression { Value = "ok" },
                        },
                        ThenBranch = new ActionNode
                        {
                            NodeId = "act_then",
                            ActionType = "Log",
                            Parameters = new LogParameters { Message = "ok", Level = "Info" },
                        },
                        ElseBranch = new ActionNode
                        {
                            NodeId = "act_else",
                            ActionType = "Log",
                            Parameters = new LogParameters { Message = "ng", Level = "Error" },
                        },
                    },
                    new WhileNode
                    {
                        NodeId = "while_1",
                        MaxIterations = 5,
                        Condition = new UnaryConditionExpression
                        {
                            Operator = UnaryOperator.Not,
                            Operand = new VariableExpression { VariableName = "isDone" },
                        },
                        Body = new ActionNode
                        {
                            NodeId = "act_while_body",
                            ActionType = "Delay",
                            Parameters = new DelayParameters { Milliseconds = 50 },
                        },
                    },
                    new ForEachNode
                    {
                        NodeId = "for_1",
                        ItemVariable = "item",
                        MaxIterations = 100,
                        Source = new VariableExpression { VariableName = "list" },
                        Body = new ActionNode
                        {
                            NodeId = "act_for_body",
                            ActionType = "WebNavigate",
                            Parameters = new WebNavigateParameters { Url = "https://example.com" },
                        },
                    },
                ],
            },
        };

        var json = _serializer.Serialize(workflow);
        var deserialized = _serializer.Deserialize(json);

        deserialized.Root.Should().BeOfType<SequenceNode>();
        var seq = (SequenceNode)deserialized.Root;
        seq.Nodes.Should().HaveCount(3);
        seq.Nodes[0].Should().BeOfType<IfNode>();
        seq.Nodes[1].Should().BeOfType<WhileNode>();
        seq.Nodes[2].Should().BeOfType<ForEachNode>();
    }

    [Fact]
    public void DeserializeRejectsUnknownSchemaVersion()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "invalid_schema_version.json");
        var json = File.ReadAllText(fixturePath);

        var act = () => _serializer.Deserialize(json);
        act.Should().Throw<WorkflowIrSerializationException>()
            .WithMessage("*Unsupported schemaVersion*");
    }

    [Fact]
    public void DeserializeRejectsUnknownProperty()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "invalid_unknown_property.json");
        var json = File.ReadAllText(fixturePath);

        var act = () => _serializer.Deserialize(json);
        act.Should().Throw<WorkflowIrSerializationException>();
    }

    [Fact]
    public void DeserializeRejectsUnknownNodeType()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "invalid_unknown_node_type.json");
        var json = File.ReadAllText(fixturePath);

        var act = () => _serializer.Deserialize(json);
        act.Should().Throw<WorkflowIrSerializationException>();
    }

    [Fact]
    public void DeserializeRejectsUnknownActionType()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "invalid_unknown_action_type.json");
        var json = File.ReadAllText(fixturePath);

        var act = () => _serializer.Deserialize(json);
        act.Should().Throw<WorkflowIrSerializationException>();
    }

    [Fact]
    public void DeserializeRejectsMissingWhileMaxIterations()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "invalid_missing_loop_limit.json");
        var json = File.ReadAllText(fixturePath);

        var act = () => _serializer.Deserialize(json);
        act.Should().Throw<WorkflowIrSerializationException>();
    }
}
