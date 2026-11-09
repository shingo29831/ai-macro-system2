// Module: @role: Chromeブラウザ操作の実行・許可オリジン強制・要素読み戻し照合アダプタ
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Application;

public sealed class ChromeAdapter : IActionAdapter
{
    private readonly IBrowserDriver _driver;

    public ChromeAdapter(IBrowserDriver driver)
    {
        _driver = driver;
    }

    public async Task<AdapterExecutionResult> ExecuteAsync(
        WorkflowNode node,
        PolicyDeclaration policy,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> runtimeOutputs,
        CancellationToken ct)
    {
        JsonElement argsElement = node.Args!.Value;

        switch (node.ActionType)
        {
            case IrConstants.ActionChromeNavigate:
            {
                var args = argsElement.Deserialize<ChromeNavigateArgs>()!;
                if (!Uri.TryCreate(args.Url, UriKind.Absolute, out Uri? initialUri))
                {
                    return AdapterExecutionResult.Failed(FailureClassification.ValidationFailure, "Invalid URL format.");
                }
                if (!IsOriginAllowed(initialUri, policy.AllowedOrigins))
                {
                    return AdapterExecutionResult.Failed(FailureClassification.PolicyViolation, $"Initial navigation URL '{initialUri}' is blocked by allowlist.");
                }

                try
                {
                    var navOutcome = await _driver.NavigateAsync(initialUri, ct);

                    // リダイレクト経路・最終URL・ポップアップすべてに許可リストを強制適用（仕様9.2）
                    foreach (Uri redirectUri in navOutcome.RedirectChain)
                    {
                        if (!IsOriginAllowed(redirectUri, policy.AllowedOrigins))
                        {
                            return AdapterExecutionResult.Failed(FailureClassification.PolicyViolation, $"Redirect to '{redirectUri}' blocked by allowlist.", sideEffectCommitted: true);
                        }
                    }
                    if (!IsOriginAllowed(navOutcome.FinalUrl, policy.AllowedOrigins))
                    {
                        return AdapterExecutionResult.Failed(FailureClassification.PolicyViolation, $"Final navigation URL '{navOutcome.FinalUrl}' blocked by allowlist.", sideEffectCommitted: true);
                    }
                    foreach (Uri popupUri in navOutcome.OpenedPopups)
                    {
                        if (!IsOriginAllowed(popupUri, policy.AllowedOrigins))
                        {
                            return AdapterExecutionResult.Failed(FailureClassification.PolicyViolation, $"Popup/new tab URL '{popupUri}' blocked by allowlist.", sideEffectCommitted: true);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(args.ExpectedFinalUrlPrefix) &&
                        !navOutcome.FinalUrl.ToString().StartsWith(args.ExpectedFinalUrlPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return AdapterExecutionResult.Failed(
                            FailureClassification.PostconditionMismatch,
                            $"Final URL '{navOutcome.FinalUrl}' did not match expected prefix '{args.ExpectedFinalUrlPrefix}'.",
                            sideEffectCommitted: true);
                    }

                    return AdapterExecutionResult.Succeeded(
                        $"Navigated to {navOutcome.FinalUrl}",
                        new Dictionary<string, object?> { ["finalUrl"] = navOutcome.FinalUrl.ToString() },
                        sideEffectCommitted: false);
                }
                catch (TimeoutException ex)
                {
                    return AdapterExecutionResult.Failed(FailureClassification.TransientError, $"Navigation timed out: {ex.Message}", sideEffectCommitted: false);
                }
            }

            case IrConstants.ActionChromeClick:
            {
                var args = argsElement.Deserialize<ChromeClickArgs>()!;
                var currentUrlcheck = VerifyCurrentBrowserOrigin(policy);
                if (currentUrlcheck is not null)
                {
                    return currentUrlcheck;
                }

                var targetCheck = await EnsureUniqueTargetAsync(args.Locator!, policy, ct);
                if (targetCheck is not null)
                {
                    return targetCheck;
                }

                try
                {
                    await _driver.ClickElementAsync(args.Locator!, ct);
                }
                catch (Exception ex)
                {
                    return AdapterExecutionResult.Unknown($"Click side-effect outcome is uncertain due to runtime error: {ex.Message}");
                }

                var postNavCheck = VerifyCurrentBrowserOrigin(policy);
                if (postNavCheck is not null)
                {
                    return postNavCheck;
                }

                if (!string.IsNullOrWhiteSpace(args.ExpectedStateSelector))
                {
                    bool exists = await _driver.ElementExistsAsync(args.ExpectedStateSelector, ct);
                    if (!exists)
                    {
                        return AdapterExecutionResult.Failed(
                            FailureClassification.PostconditionMismatch,
                            $"Expected post-click element '{args.ExpectedStateSelector}' was not found.",
                            sideEffectCommitted: true);
                    }
                }

                return AdapterExecutionResult.Succeeded(
                    "Click completed and verified.",
                    new Dictionary<string, object?> { ["clicked"] = true });
            }

            case IrConstants.ActionChromeFill:
            {
                var args = argsElement.Deserialize<ChromeFillArgs>()!;
                var currentUrlCheck = VerifyCurrentBrowserOrigin(policy);
                if (currentUrlCheck is not null)
                {
                    return currentUrlCheck;
                }

                var targetCheck = await EnsureUniqueTargetAsync(args.Locator!, policy, ct);
                if (targetCheck is not null)
                {
                    return targetCheck;
                }

                string fillValue = ResolveStringArgument(args.Value, args.ValueRef, runtimeOutputs);
                try
                {
                    await _driver.FillElementAsync(args.Locator!, fillValue, ct);
                }
                catch (TimeoutException ex)
                {
                    return AdapterExecutionResult.Failed(
                        FailureClassification.TransientError,
                        $"Chrome.Fill timed out before completion: {ex.Message}",
                        sideEffectCommitted: false);
                }
                catch (Exception ex)
                {
                    return AdapterExecutionResult.Unknown($"Chrome.Fill outcome is uncertain due to runtime error: {ex.Message}");
                }

                // 入力後の読み戻し検証（仕様9.4）
                string? readback;
                try
                {
                    readback = await _driver.ReadElementValueAsync(args.Locator!, ct);
                }
                catch (Exception ex)
                {
                    return AdapterExecutionResult.Unknown($"Chrome.Fill completed but readback failed: {ex.Message}");
                }

                if (!string.Equals(readback, fillValue, StringComparison.Ordinal))
                {
                    return AdapterExecutionResult.Failed(
                        FailureClassification.PostconditionMismatch,
                        "Readback verification failed after Chrome.Fill.",
                        sideEffectCommitted: true);
                }

                string outputDisplay = args.IsSensitive ? "[REDACTED]" : fillValue;
                return AdapterExecutionResult.Succeeded(
                    "Input filled and verified via readback.",
                    new Dictionary<string, object?> { ["filledValue"] = outputDisplay });
            }

            case IrConstants.ActionChromeSubmit:
            {
                var args = argsElement.Deserialize<ChromeSubmitArgs>()!;
                var currentUrlCheck = VerifyCurrentBrowserOrigin(policy);
                if (currentUrlCheck is not null)
                {
                    return currentUrlCheck;
                }

                if (!string.IsNullOrWhiteSpace(args.DestinationUrl))
                {
                    if (!Uri.TryCreate(args.DestinationUrl, UriKind.Absolute, out Uri? destUri) ||
                        !IsOriginAllowed(destUri, policy.AllowedOrigins))
                    {
                        return AdapterExecutionResult.Failed(
                            FailureClassification.PolicyViolation,
                            $"Submit destination URL '{args.DestinationUrl}' is blocked by allowlist.");
                    }
                }

                var targetCheck = await EnsureUniqueTargetAsync(args.Locator!, policy, ct);
                if (targetCheck is not null)
                {
                    return targetCheck;
                }

                try
                {
                    await _driver.ClickElementAsync(args.Locator!, ct);
                    bool successConfirmed = await _driver.ElementExistsAsync(args.ExpectedSuccessSelector, ct);
                    if (!successConfirmed)
                    {
                        return AdapterExecutionResult.Unknown(
                            $"Form submitted, but expected postcondition selector '{args.ExpectedSuccessSelector}' could not be confirmed.");
                    }

                    var postSubmitOriginCheck = VerifyCurrentBrowserOrigin(policy);
                    if (postSubmitOriginCheck is not null)
                    {
                        return postSubmitOriginCheck;
                    }

                    return AdapterExecutionResult.Succeeded(
                        "Form submission verified.",
                        new Dictionary<string, object?> { ["submitted"] = true });
                }
                catch (Exception ex)
                {
                    return AdapterExecutionResult.Unknown($"External form submission outcome is unknown: {ex.Message}");
                }
            }

            default:
                return AdapterExecutionResult.Failed(FailureClassification.ValidationFailure, $"Unsupported Chrome action '{node.ActionType}'.");
        }
    }

    public async Task<ExternalStateProbeResult> ProbeExternalStateAsync(WorkflowNode node, CancellationToken ct)
    {
        if (node.ActionType == IrConstants.ActionChromeSubmit && node.Args.HasValue)
        {
            var args = node.Args.Value.Deserialize<ChromeSubmitArgs>();
            if (args is not null && !string.IsNullOrWhiteSpace(args.ExpectedSuccessSelector))
            {
                try
                {
                    bool exists = await _driver.ElementExistsAsync(args.ExpectedSuccessSelector, ct);
                    if (exists)
                    {
                        return ExternalStateProbeResult.ConfirmedCompleted;
                    }
                }
                catch
                {
                    return ExternalStateProbeResult.OutcomeUnknown;
                }
            }
        }
        return ExternalStateProbeResult.OutcomeUnknown;
    }

    private AdapterExecutionResult? VerifyCurrentBrowserOrigin(PolicyDeclaration policy)
    {
        if (_driver.CurrentUrl is not null && !IsOriginAllowed(_driver.CurrentUrl, policy.AllowedOrigins))
        {
            return AdapterExecutionResult.Failed(
                FailureClassification.PolicyViolation,
                $"Current browser URL '{_driver.CurrentUrl}' is outside allowed origins.",
                sideEffectCommitted: true);
        }
        return null;
    }

    private async Task<AdapterExecutionResult?> EnsureUniqueTargetAsync(SemanticLocator locator, PolicyDeclaration policy, CancellationToken ct)
    {
        if (locator.UsesCoordinatesOnly() && !policy.AllowCoordinateClick)
        {
            return AdapterExecutionResult.Failed(FailureClassification.PolicyViolation, "Coordinate operation is not permitted by policy.");
        }

        int count = await _driver.CountMatchingElementsAsync(locator, ct);
        if (count == 0)
        {
            return AdapterExecutionResult.Failed(FailureClassification.TargetNotFound, "Target element does not exist (0 matches).");
        }
        if (count > 1)
        {
            return AdapterExecutionResult.Failed(FailureClassification.TargetAmbiguous, $"Target locator is ambiguous ({count} matches found). Unconditional substitution is prohibited.");
        }
        return null;
    }

    private static bool IsOriginAllowed(Uri uri, IReadOnlyList<OriginRule> allowedOrigins) =>
        allowedOrigins.Any(rule => rule.Matches(uri));

    private static string ResolveStringArgument(
        string? literalValue,
        DataReference? reference,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> runtimeOutputs)
    {
        if (reference is not null)
        {
            if (runtimeOutputs.TryGetValue(reference.SourceNodeId, out var outputs) &&
                outputs.TryGetValue(reference.OutputName, out object? val) &&
                val is not null)
            {
                return val.ToString() ?? string.Empty;
            }
            if (reference.IsOptional)
            {
                return literalValue ?? string.Empty;
            }
            throw new InvalidOperationException($"Runtime reference '{reference.SourceNodeId}.{reference.OutputName}' could not be resolved.");
        }
        return literalValue ?? string.Empty;
    }
}
