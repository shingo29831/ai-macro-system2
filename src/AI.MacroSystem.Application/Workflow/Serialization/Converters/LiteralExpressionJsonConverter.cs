namespace AI.MacroSystem.Application.Workflow.Serialization.Converters;

using System.Text.Json;
using System.Text.Json.Serialization;
using AI.MacroSystem.Domain.Workflow.Expressions;

public sealed class LiteralExpressionJsonConverter : JsonConverter<LiteralExpression>
{
    public override LiteralExpression Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("LiteralExpression must be a JSON object.");
        }

        object? val = null;
        bool valueFound = false;

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.NameEquals("kind"))
            {
                if (prop.Value.GetString() != "Literal")
                {
                    throw new JsonException($"Invalid kind for LiteralExpression: {prop.Value.GetString()}");
                }
            }
            else if (prop.NameEquals("value"))
            {
                valueFound = true;
                val = ConvertJsonElement(prop.Value);
            }
            else
            {
                throw new JsonException($"Unmapped property '{prop.Name}' is not allowed on LiteralExpression.");
            }
        }

        if (!valueFound)
        {
            throw new JsonException("LiteralExpression must have a 'value' property.");
        }

        return new LiteralExpression { Value = val };
    }

    private static object? ConvertJsonElement(JsonElement elem)
    {
        return elem.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => elem.GetString(),
            JsonValueKind.Number when elem.TryGetInt64(out var l) => l,
            JsonValueKind.Number => elem.GetDouble(),
            _ => throw new JsonException($"Unsupported literal value kind: {elem.ValueKind}")
        };
    }

    public override void Write(Utf8JsonWriter writer, LiteralExpression value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "Literal");
        writer.WritePropertyName("value");
        if (value.Value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            JsonSerializer.Serialize(writer, value.Value, value.Value.GetType(), options);
        }
        writer.WriteEndObject();
    }
}
