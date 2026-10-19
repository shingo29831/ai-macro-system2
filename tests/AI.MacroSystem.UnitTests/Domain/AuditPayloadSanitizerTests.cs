namespace AI.MacroSystem.UnitTests.Domain;

using AI.MacroSystem.Domain.Audit;
using FluentAssertions;
using Xunit;

public class AuditPayloadSanitizerTests
{
    [Fact]
    public void SanitizeJson_RedactsSensitiveFieldsRecursively()
    {
        var rawJson = """
        {
            "nodeId": "step_1",
            "password": "super-secret-password",
            "token": "ghp_123456789",
            "apiKey": "sk-proj-xyz",
            "nested": {
                "credential": "admin-password",
                "normalValue": 42
            },
            "array": [
                { "secret": "shhh" },
                { "publicInfo": "ok" }
            ]
        }
        """;

        var sanitized = AuditPayloadSanitizer.SanitizeJson(rawJson);

        sanitized.Should().NotContain("super-secret-password");
        sanitized.Should().NotContain("ghp_123456789");
        sanitized.Should().NotContain("sk-proj-xyz");
        sanitized.Should().NotContain("admin-password");
        sanitized.Should().NotContain("shhh");

        sanitized.Should().Contain("\"step_1\"");
        sanitized.Should().Contain("\"normalValue\":42");
        sanitized.Should().Contain("\"publicInfo\":\"ok\"");
        sanitized.Should().Contain(AuditPayloadSanitizer.RedactedValue);
    }

    [Fact]
    public void SanitizePlainText_RedactsBearerTokens()
    {
        var text = "Request failed with Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.test and status 401";
        var sanitized = AuditPayloadSanitizer.SanitizePlainText(text);

        sanitized.Should().NotContain("eyJhbGciOiJIUzI1NiJ9.test");
        sanitized.Should().Contain($"Bearer {AuditPayloadSanitizer.RedactedValue}");
    }
}
