// @role: 監査イベントやログ詳細から認証情報・機密情報をマスキングするサニタイザー
namespace AI.MacroSystem.Domain.Audit;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class AuditPayloadSanitizer
{
    public const string RedactedValue = "***REDACTED***";

    private static readonly HashSet<string> SensitiveKeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password",
        "passwd",
        "secret",
        "token",
        "accesstoken",
        "refreshtoken",
        "apikey",
        "api_key",
        "credential",
        "credentials",
        "authorization",
        "bearer",
        "privatekey",
        "private_key",
    };

    private static readonly Regex BearerTokenRegex = new(
        @"(Bearer\s+)[A-Za-z0-9\-_.]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string SanitizeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            var node = JsonNode.Parse(json);
            if (node is null)
            {
                return "{}";
            }

            SanitizeNode(node);
            return node.ToJsonString();
        }
        catch (JsonException)
        {
            return SanitizePlainText(json);
        }
    }

    public static string SanitizePlainText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return BearerTokenRegex.Replace(text, $"$1{RedactedValue}");
    }

    private static void SanitizeNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (SensitiveKeyNames.Contains(property.Key))
                {
                    obj[property.Key] = RedactedValue;
                }
                else if (property.Value != null)
                {
                    SanitizeNode(property.Value);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item != null)
                {
                    SanitizeNode(item);
                }
            }
        }
    }
}
