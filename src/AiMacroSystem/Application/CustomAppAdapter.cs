// Module: @role: カスタムデスクトップアプリ操作の実行・一意性検証・読み戻し照合アダプタ
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Application;

public sealed class CustomAppAdapter : IActionAdapter
{
    private readonly ICustomAppDriver _driver;

    public CustomAppAdapter(ICustomAppDriver driver)
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
            case IrConstants.ActionCustomAppClick:
            {
                var args = argsElement.Deserialize<CustomAppClickArgs>()!;
                var preCheck = await EnsureUniqueControlAsync(args.ApplicationName, args.Locator!, policy, ct);
                if (preCheck is not null)
                {
                    return preCheck;
                }

                try
                {
                    await _driver.ClickControlAsync(args.ApplicationName, args.Locator!, ct);
                    return AdapterExecutionResult.Succeeded(
                        $"Clicked control in '{args.ApplicationName}'.",
                        new Dictionary<string, object?> { ["clicked"] = true });
                }
                catch (Exception ex)
                {
                    return AdapterExecutionResult.Unknown($"CustomApp.Click outcome is uncertain due to runtime error: {ex.Message}");
                }
            }

            case IrConstants.ActionCustomAppFill:
            {
                var args = argsElement.Deserialize<CustomAppFillArgs>()!;
                var preCheck = await EnsureUniqueControlAsync(args.ApplicationName, args.Locator!, policy, ct);
                if (preCheck is not null)
                {
                    return preCheck;
                }

                string fillValue = ResolveStringArgument(args.Value, args.ValueRef, runtimeOutputs);
                try
                {
                    await _driver.FillControlAsync(args.ApplicationName, args.Locator!, fillValue, ct);
                }
                catch (TimeoutException ex)
                {
                    return AdapterExecutionResult.Failed(
                        FailureClassification.TransientError,
                        $"CustomApp.Fill timed out before completion: {ex.Message}",
                        sideEffectCommitted: false);
                }
                catch (Exception ex)
                {
                    return AdapterExecutionResult.Unknown($"CustomApp.Fill outcome is uncertain due to runtime error: {ex.Message}");
                }

                string? readback;
                try
                {
                    readback = await _driver.ReadControlTextAsync(args.ApplicationName, args.Locator!, ct);
                }
                catch (Exception ex)
                {
                    return AdapterExecutionResult.Unknown($"CustomApp.Fill completed but readback failed: {ex.Message}");
                }

                if (!string.Equals(readback, fillValue, StringComparison.Ordinal))
                {
                    return AdapterExecutionResult.Failed(
                        FailureClassification.PostconditionMismatch,
                        $"Readback verification failed on CustomApp '{args.ApplicationName}'.",
                        sideEffectCommitted: true);
                }

                string outputDisplay = args.IsSensitive ? SensitiveDataProtector.RedactedPlaceholder : fillValue;
                return AdapterExecutionResult.Succeeded(
                    $"Filled and verified control in '{args.ApplicationName}'.",
                    new Dictionary<string, object?> { ["filledValue"] = outputDisplay });
            }

            case IrConstants.ActionCustomAppReadText:
            {
                var args = argsElement.Deserialize<CustomAppReadTextArgs>()!;
                var preCheck = await EnsureUniqueControlAsync(args.ApplicationName, args.Locator!, policy, ct);
                if (preCheck is not null)
                {
                    return preCheck;
                }

                try
                {
                    string text = await _driver.ReadControlTextAsync(args.ApplicationName, args.Locator!, ct) ?? string.Empty;
                    return AdapterExecutionResult.Succeeded(
                        $"Read text from '{args.ApplicationName}'.",
                        new Dictionary<string, object?> { ["text"] = text },
                        sideEffectCommitted: false);
                }
                catch (Exception ex)
                {
                    return AdapterExecutionResult.Failed(
                        FailureClassification.TransientError,
                        $"CustomApp.ReadText failed: {ex.Message}",
                        sideEffectCommitted: false);
                }
            }

            default:
                return AdapterExecutionResult.Failed(FailureClassification.ValidationFailure, $"Unsupported CustomApp action '{node.ActionType}'.");
        }
    }

    public async Task<ExternalStateProbeResult> ProbeExternalStateAsync(WorkflowNode node, CancellationToken ct)
    {
        if (node.ActionType == IrConstants.ActionCustomAppFill && node.Args.HasValue)
        {
            var args = node.Args.Value.Deserialize<CustomAppFillArgs>();
            if (args?.Locator is not null && !string.IsNullOrEmpty(args.Value))
            {
                try
                {
                    string? actual = await _driver.ReadControlTextAsync(args.ApplicationName, args.Locator, ct);
                    if (string.Equals(actual, args.Value, StringComparison.Ordinal))
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

    private async Task<AdapterExecutionResult?> EnsureUniqueControlAsync(
        string applicationName,
        SemanticLocator locator,
        PolicyDeclaration policy,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(applicationName))
        {
            return AdapterExecutionResult.Failed(FailureClassification.ValidationFailure, "Explicit applicationName is required for CustomApp action.");
        }
        if (locator.UsesCoordinatesOnly() && !policy.AllowCoordinateClick)
        {
            return AdapterExecutionResult.Failed(FailureClassification.PolicyViolation, "Coordinate operation is not permitted by policy.");
        }
        if (!await _driver.WindowExistsAsync(applicationName, ct))
        {
            return AdapterExecutionResult.Failed(FailureClassification.TargetNotFound, $"Custom application window '{applicationName}' was not found.");
        }

        int count = await _driver.CountMatchingControlsAsync(applicationName, locator, ct);
        if (count == 0)
        {
            return AdapterExecutionResult.Failed(FailureClassification.TargetNotFound, $"Control not found in '{applicationName}'.");
        }
        if (count > 1)
        {
            return AdapterExecutionResult.Failed(FailureClassification.TargetAmbiguous, $"Control locator is ambiguous in '{applicationName}' ({count} matches).");
        }
        return null;
    }

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
