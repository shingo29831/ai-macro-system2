"""Module: @role: AI Macro System パッケージ公開インターフェースおよびレイヤー統合定義"""

from ai_macro.models import (
    AIMode,
    CommandType,
    Criticality,
    EventTransformRecord,
    ExecutableMacro,
    ExecutionStatus,
    FailurePolicy,
    LegacyStatusMapper,
    MacroExecutionReport,
    MacroStep,
    OperationResult,
    PostConditionResult,
    PostConditionSpec,
    RecordedEvent,
    RetryPolicy,
    SideEffectLevel,
    ValueOrigin,
    WorkflowIR,
    WorkflowIRNode,
)
from ai_macro.validator import MacroValidationError, MacroValidator
from ai_macro.generator import AIModeConfig, AIModeController, MacroGenerationPipeline
from ai_macro.adapters import (
    BrowserAdapter,
    ExcelAdapter,
    PhysicalInputAdapter,
    ScreenRecognitionAdapter,
    ScriptAdapter,
)
from ai_macro.gui import FriendlyUIPresenter
from ai_macro.runner import AuditLogger, ExecutionJobContext, MacroRunner
from ai_macro.runtime import (
    LocalBrowserDriver,
    LocalExcelDriver,
    LocalPhysicalInputDriver,
    LocalScreenRecognizer,
    LocalScriptRunner,
    RuntimeBundle,
    WorkspaceRuntimeState,
    build_default_runtime,
)

__all__ = [
    "AIMode",
    "AIModeConfig",
    "AIModeController",
    "AuditLogger",
    "BrowserAdapter",
    "CommandType",
    "Criticality",
    "EventTransformRecord",
    "ExcelAdapter",
    "ExecutableMacro",
    "ExecutionJobContext",
    "ExecutionStatus",
    "FailurePolicy",
    "FriendlyUIPresenter",
    "LegacyStatusMapper",
    "LocalBrowserDriver",
    "LocalExcelDriver",
    "LocalPhysicalInputDriver",
    "LocalScreenRecognizer",
    "LocalScriptRunner",
    "RuntimeBundle",
    "WorkflowIR",
    "WorkflowIRNode",
    "WorkspaceRuntimeState",
    "build_default_runtime",
]
