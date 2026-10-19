namespace AI.MacroSystem.Application.Workflow.Validation;

using System.Text.Json.Nodes;
using Json.Schema;

public sealed class WorkflowSchemaValidator : IWorkflowSchemaValidator
{
    private readonly JsonSchema _schema;

    public WorkflowSchemaValidator(JsonSchema? schema = null)
    {
        _schema = schema ?? LoadEmbeddedSchema();
    }

    public ValidationResult Validate(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (Exception ex)
        {
            return ValidationResult.Failure([$"Invalid JSON syntax: {ex.Message}"]);
        }

        if (node is null)
        {
            return ValidationResult.Failure(["JSON root cannot be null."]);
        }

        var options = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        };

        var results = _schema.Evaluate(node, options);
        if (results.IsValid)
        {
            return ValidationResult.Success();
        }

        var errors = new List<string>();
        if (results.Errors != null)
        {
            foreach (var (k, v) in results.Errors)
            {
                errors.Add($"{k}: {v}");
            }
        }

        foreach (var detail in results.Details)
        {
            if (detail.Errors != null)
            {
                foreach (var (k, v) in detail.Errors)
                {
                    errors.Add($"{detail.InstanceLocation} - {k}: {v}");
                }
            }
        }

        if (errors.Count == 0)
        {
            errors.Add("Schema validation failed with unspecified errors.");
        }

        return ValidationResult.Failure(errors);
    }

    private static JsonSchema LoadEmbeddedSchema()
    {
        var assembly = typeof(WorkflowSchemaValidator).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("workflow-ir.schema.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded schema 'workflow-ir.schema.json' was not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Unable to open stream for '{resourceName}'.");
        using var reader = new StreamReader(stream);
        var schemaText = reader.ReadToEnd();
        return JsonSchema.FromText(schemaText);
    }
}
