namespace AI.MacroSystem.Application.Workflow.Serialization.Converters;

using System.Text.Json;
using System.Text.Json.Serialization;
using AI.MacroSystem.Domain.Workflow.Actions;
using AI.MacroSystem.Domain.Workflow.Nodes;

public sealed class ActionNodeJsonConverter : JsonConverter<ActionNode>
{
    private readonly IActionTypeRegistry _registry;

    public ActionNodeJsonConverter(IActionTypeRegistry registry)
    {
        _registry = registry;
    }

    public override ActionNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("ActionNode must be a JSON object.");
        }

        string? nodeId = null;
        string? actionType = null;
        JsonElement? parametersElement = null;

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.NameEquals("kind"))
            {
                if (prop.Value.GetString() != "Action")
                {
                    throw new JsonException($"Invalid kind for ActionNode: {prop.Value.GetString()}");
                }
            }
            else if (prop.NameEquals("nodeId"))
            {
                nodeId = prop.Value.GetString();
            }
            else if (prop.NameEquals("actionType"))
            {
                actionType = prop.Value.GetString();
            }
            else if (prop.NameEquals("parameters"))
            {
                parametersElement = prop.Value.Clone();
            }
            else
            {
                throw new JsonException($"Unmapped property '{prop.Name}' is not allowed on ActionNode.");
            }
        }

        if (string.IsNullOrWhiteSpace(nodeId))
        {
            throw new JsonException("ActionNode must specify a valid 'nodeId'.");
        }

        if (string.IsNullOrWhiteSpace(actionType))
        {
            throw new JsonException("ActionNode must specify a valid 'actionType'.");
        }

        if (!parametersElement.HasValue || parametersElement.Value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("ActionNode must specify a valid 'parameters' object.");
        }

        var paramType = _registry.GetParametersType(actionType)
            ?? throw new JsonException($"Action type '{actionType}' is not registered.");

        var parameters = (ActionParameters?)JsonSerializer.Deserialize(parametersElement.Value.GetRawText(), paramType, options)
            ?? throw new JsonException($"Failed to deserialize parameters for action type '{actionType}'.");

        return new ActionNode
        {
            NodeId = nodeId,
            ActionType = actionType,
            Parameters = parameters,
        };
    }

    public override void Write(Utf8JsonWriter writer, ActionNode value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "Action");
        writer.WriteString("nodeId", value.NodeId);
        writer.WriteString("actionType", value.ActionType);
        writer.WritePropertyName("parameters");
        JsonSerializer.Serialize(writer, value.Parameters, value.Parameters.GetType(), options);
        writer.WriteEndObject();
    }
}
