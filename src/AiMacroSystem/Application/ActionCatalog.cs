// Module: @role: 登録済みアクション契約定義・入出力型・リトライ・副作用カタログ
using System;
using System.Collections.Generic;
using AiMacroSystem.Domain;

namespace AiMacroSystem.Application;

public static class ActionCatalog
{
    private static readonly Dictionary<string, ActionContract> _contracts = new(StringComparer.Ordinal)
    {
        [IrConstants.ActionChromeNavigate] = new ActionContract
        {
            ActionType = IrConstants.ActionChromeNavigate,
            DeclaredSideEffects = SideEffectKind.Read,
            BaseRiskLevel = RiskLevel.Low,
            OutputTypes = new Dictionary<string, string> { ["finalUrl"] = IrConstants.TypeString },
            Timeout = TimeSpan.FromSeconds(30),
            AllowRetry = true,
            MaxRetries = 2,
            RetryableClassifications = new HashSet<FailureClassification> { FailureClassification.TransientError },
            RequiresPostconditionVerification = true
        },
        [IrConstants.ActionChromeClick] = new ActionContract
        {
            ActionType = IrConstants.ActionChromeClick,
            DeclaredSideEffects = SideEffectKind.LocalMutation,
            BaseRiskLevel = RiskLevel.Medium,
            OutputTypes = new Dictionary<string, string> { ["clicked"] = IrConstants.TypeBoolean },
            Timeout = TimeSpan.FromSeconds(15),
            AllowRetry = false,
            MaxRetries = 0,
            RetryableClassifications = new HashSet<FailureClassification>(),
            RequiresPostconditionVerification = true
        },
        [IrConstants.ActionChromeFill] = new ActionContract
        {
            ActionType = IrConstants.ActionChromeFill,
            DeclaredSideEffects = SideEffectKind.LocalMutation,
            BaseRiskLevel = RiskLevel.Low,
            OutputTypes = new Dictionary<string, string> { ["filledValue"] = IrConstants.TypeString },
            Timeout = TimeSpan.FromSeconds(15),
            AllowRetry = true,
            MaxRetries = 1,
            RetryableClassifications = new HashSet<FailureClassification> { FailureClassification.TransientError },
            RequiresPostconditionVerification = true
        },
        [IrConstants.ActionChromeSubmit] = new ActionContract
        {
            ActionType = IrConstants.ActionChromeSubmit,
            DeclaredSideEffects = SideEffectKind.ExternalSend,
            BaseRiskLevel = RiskLevel.High,
            OutputTypes = new Dictionary<string, string> { ["submitted"] = IrConstants.TypeBoolean },
            Timeout = TimeSpan.FromSeconds(30),
            AllowRetry = false,
            MaxRetries = 0,
            RetryableClassifications = new HashSet<FailureClassification>(),
            RequiresPostconditionVerification = true
        },
        [IrConstants.ActionExcelReadRange] = new ActionContract
        {
            ActionType = IrConstants.ActionExcelReadRange,
            DeclaredSideEffects = SideEffectKind.Read,
            BaseRiskLevel = RiskLevel.Low,
            OutputTypes = new Dictionary<string, string>
            {
                ["values"] = IrConstants.TypeArrayString,
                ["table"] = IrConstants.TypeTable2D
            },
            Timeout = TimeSpan.FromSeconds(20),
            AllowRetry = true,
            MaxRetries = 2,
            RetryableClassifications = new HashSet<FailureClassification> { FailureClassification.TransientError },
            RequiresPostconditionVerification = true
        },
        [IrConstants.ActionExcelWriteRange] = new ActionContract
        {
            ActionType = IrConstants.ActionExcelWriteRange,
            DeclaredSideEffects = SideEffectKind.LocalMutation,
            BaseRiskLevel = RiskLevel.Medium,
            OutputTypes = new Dictionary<string, string> { ["writtenCells"] = IrConstants.TypeNumber },
            Timeout = TimeSpan.FromSeconds(30),
            AllowRetry = false,
            MaxRetries = 0,
            RetryableClassifications = new HashSet<FailureClassification>(),
            RequiresPostconditionVerification = true
        },
        [IrConstants.ActionCustomAppClick] = new ActionContract
        {
            ActionType = IrConstants.ActionCustomAppClick,
            DeclaredSideEffects = SideEffectKind.LocalMutation,
            BaseRiskLevel = RiskLevel.Medium,
            OutputTypes = new Dictionary<string, string> { ["clicked"] = IrConstants.TypeBoolean },
            Timeout = TimeSpan.FromSeconds(15),
            AllowRetry = false,
            MaxRetries = 0,
            RetryableClassifications = new HashSet<FailureClassification>(),
            RequiresPostconditionVerification = true
        },
        [IrConstants.ActionCustomAppFill] = new ActionContract
        {
            ActionType = IrConstants.ActionCustomAppFill,
            DeclaredSideEffects = SideEffectKind.LocalMutation,
            BaseRiskLevel = RiskLevel.Low,
            OutputTypes = new Dictionary<string, string> { ["filledValue"] = IrConstants.TypeString },
            Timeout = TimeSpan.FromSeconds(15),
            AllowRetry = true,
            MaxRetries = 1,
            RetryableClassifications = new HashSet<FailureClassification> { FailureClassification.TransientError },
            RequiresPostconditionVerification = true
        },
        [IrConstants.ActionCustomAppReadText] = new ActionContract
        {
            ActionType = IrConstants.ActionCustomAppReadText,
            DeclaredSideEffects = SideEffectKind.Read,
            BaseRiskLevel = RiskLevel.Low,
            OutputTypes = new Dictionary<string, string> { ["text"] = IrConstants.TypeString },
            Timeout = TimeSpan.FromSeconds(15),
            AllowRetry = true,
            MaxRetries = 2,
            RetryableClassifications = new HashSet<FailureClassification> { FailureClassification.TransientError },
            RequiresPostconditionVerification = true
        }
    };

    public static bool TryGetContract(string actionType, out ActionContract contract) =>
        _contracts.TryGetValue(actionType, out contract!);

    public static IReadOnlyDictionary<string, ActionContract> All => _contracts;
}
