namespace AI.MacroSystem.UnitTests.Workflow;

using AI.MacroSystem.Application.Workflow.Serialization;
using AI.MacroSystem.Application.Workflow.Validation;
using FluentAssertions;
using Xunit;

public class WorkflowSchemaValidationTests
{
    private readonly WorkflowSchemaValidator _validator = new();
    private readonly WorkflowIrJsonSerializer _serializer = new();

    [Theory]
    [InlineData("valid_workflow_basic.json")]
    [InlineData("valid_workflow_control_flow.json")]
    public void ValidFixturesPassSchemaValidationAndDeserialization(string fixtureName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        var json = File.ReadAllText(path);

        var validationResult = _validator.Validate(json);
        validationResult.IsValid.Should().BeTrue(string.Join(", ", validationResult.Errors));

        var model = _serializer.Deserialize(json);
        model.Should().NotBeNull();
        model.SchemaVersion.Should().Be("0.7");
    }

    [Theory]
    [InlineData("invalid_unknown_property.json")]
    [InlineData("invalid_unknown_node_type.json")]
    [InlineData("invalid_unknown_action_type.json")]
    [InlineData("invalid_schema_version.json")]
    [InlineData("invalid_missing_loop_limit.json")]
    public void InvalidFixturesFailSchemaValidation(string fixtureName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        var json = File.ReadAllText(path);

        var validationResult = _validator.Validate(json);
        validationResult.IsValid.Should().BeFalse($"Expected {fixtureName} to fail schema validation.");
        validationResult.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void SerializedModelOutputAlwaysPassesSchemaValidation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "valid_workflow_control_flow.json");
        var originalJson = File.ReadAllText(path);

        var model = _serializer.Deserialize(originalJson);
        var reSerializedJson = _serializer.Serialize(model);

        var validationResult = _validator.Validate(reSerializedJson);
        validationResult.IsValid.Should().BeTrue(string.Join(", ", validationResult.Errors));
    }
}
