namespace AI.MacroSystem.Application.Workflow.Serialization;

using System.Text.Json;
using System.Text.Json.Serialization;
using AI.MacroSystem.Application.Workflow.Serialization.Converters;
using AI.MacroSystem.Domain.Workflow;
using AI.MacroSystem.Domain.Workflow.Actions;

public sealed class WorkflowIrJsonSerializer
{
    private readonly JsonSerializerOptions _options;
    private readonly IActionTypeRegistry _registry;

    public WorkflowIrJsonSerializer(IActionTypeRegistry? registry = null)
    {
        _registry = registry ?? ActionTypeRegistry.CreateDefault();

        _options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };

        _options.Converters.Add(new ActionNodeJsonConverter(_registry));
        _options.Converters.Add(new LiteralExpressionJsonConverter());
    }

    public string Serialize(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.SchemaVersion != WorkflowDefinition.SupportedSchemaVersion)
        {
            throw new WorkflowIrSerializationException(
                $"Unsupported schemaVersion '{definition.SchemaVersion}'. Expected '{WorkflowDefinition.SupportedSchemaVersion}'.");
        }

        return JsonSerializer.Serialize(definition, _options);
    }

    public WorkflowDefinition Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        try
        {
            var definition = JsonSerializer.Deserialize<WorkflowDefinition>(json, _options)
                ?? throw new WorkflowIrSerializationException("Deserialized WorkflowDefinition was null.");

            if (definition.SchemaVersion != WorkflowDefinition.SupportedSchemaVersion)
            {
                throw new WorkflowIrSerializationException(
                    $"Unsupported schemaVersion '{definition.SchemaVersion}'. Expected '{WorkflowDefinition.SupportedSchemaVersion}'.");
            }

            return definition;
        }
        catch (JsonException ex)
        {
            throw new WorkflowIrSerializationException("Failed to deserialize Workflow IR JSON.", ex);
        }
    }
}
