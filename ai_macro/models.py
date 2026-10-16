"""Module: @role: マクロ生成・検証・実行・監査ログの共通ドメインモデルおよび状態・列挙型定義"""

from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Dict, List, Optional

MAX_STEPS: int = 10_000
MAX_LOOP_ITERATIONS: int = 1_000
MIN_TIMEOUT_MS: int = 1
MAX_TIMEOUT_MS: int = 300_000
MIN_WAIT_MS: int = 0
MAX_WAIT_MS: int = 60_000
MIN_COORDINATE: int = 0
MAX_COORDINATE: int = 16_384


class ExecutionStatus(str, Enum):
    PENDING = "pending"
    RUNNING = "running"
    SUCCESS = "success"
    PARTIAL_SUCCESS = "partial_success"
    FAILED = "failed"
    SKIPPED = "skipped"
    STOPPED = "stopped"
    CANCELLED = "cancelled"
    TIMEOUT = "timeout"
    UNKNOWN = "unknown"


class CommandType(str, Enum):
    BROWSER_CLICK = "browser_click"
    BROWSER_INPUT = "browser_input"
    BROWSER_SELECT = "browser_select"
    BROWSER_CHECKBOX = "browser_checkbox"
    BROWSER_WAIT = "browser_wait"
    BROWSER_SCROLL = "browser_scroll"
    SCREEN_MATCH_CLICK = "screen_match_click"
    PHYSICAL_MOUSE = "physical_mouse"
    PHYSICAL_DRAG = "physical_drag"
    PHYSICAL_KEY = "physical_key"
    EXCEL_WRITE = "excel_write"
    RUN_SCRIPT = "run_script"
    LOOP = "loop"


class Criticality(str, Enum):
    MANDATORY = "mandatory"
    OPTIONAL = "optional"


class FailurePolicy(str, Enum):
    ABORT = "abort"
    CONTINUE = "continue"
    SKIP = "skip"


class SideEffectLevel(str, Enum):
    READ_ONLY = "read_only"
    IDEMPOTENT_WRITE = "idempotent_write"
    NON_IDEMPOTENT_MUTATION = "non_idempotent_mutation"
    EXTERNAL_TRANSMISSION = "external_transmission"


class ValueOrigin(str, Enum):
    USER_RECORDED = "user_recorded"
    SNAPSHOT_INFERRED = "snapshot_inferred"
    AI_INFERRED = "ai_inferred"
    SYSTEM_DEFAULT = "system_default"


class AIMode(str, Enum):
    NO_AI = "no_ai"
    LOCAL_AI = "local_ai"
    LAN_AI_SERVER = "lan_ai_server"
    EXTERNAL_AI = "external_ai"


@dataclass(frozen=True)
class RecordedEvent:
    event_id: str
    timestamp_ms: int
    action_type: str
    window_id: str
    tab_id: str
    origin_url: str
    selector: Optional[str] = None
    value: Any = None
    value_origin: ValueOrigin = ValueOrigin.USER_RECORDED
    criticality: Criticality = Criticality.MANDATORY
    metadata: Dict[str, Any] = field(default_factory=dict)


@dataclass(frozen=True)
class EventTransformRecord:
    source_event_ids: List[str]
    resulting_node_id: Optional[str]
    transform_type: str
    reason: str
    impact_summary: str


@dataclass(frozen=True)
class WorkflowIRNode:
    node_id: str
    command: str
    source_event_ids: List[str]
    window_id: str
    tab_id: str
    origin_url: str
    params: Dict[str, Any]
    value_origin: ValueOrigin = ValueOrigin.USER_RECORDED
    criticality: Criticality = Criticality.MANDATORY
    depends_on: List[str] = field(default_factory=list)
    side_effect: SideEffectLevel = SideEffectLevel.IDEMPOTENT_WRITE
    human_approved: bool = True


@dataclass(frozen=True)
class WorkflowIR:
    workflow_id: str
    nodes: List[WorkflowIRNode]
    transform_history: List[EventTransformRecord] = field(default_factory=list)

    def to_dict(self) -> Dict[str, Any]:
        return {
            "workflow_id": self.workflow_id,
            "nodes": [
                {
                    "node_id": n.node_id,
                    "command": n.command,
                    "source_event_ids": list(n.source_event_ids),
                    "window_id": n.window_id,
                    "tab_id": n.tab_id,
                    "origin_url": n.origin_url,
                    "params": dict(n.params),
                    "value_origin": n.value_origin.value,
                    "criticality": n.criticality.value,
                    "depends_on": list(n.depends_on),
                    "side_effect": n.side_effect.value,
                    "human_approved": n.human_approved,
                }
                for n in self.nodes
            ],
            "transform_history": [
                {
                    "source_event_ids": list(t.source_event_ids),
                    "resulting_node_id": t.resulting_node_id,
                    "transform_type": t.transform_type,
                    "reason": t.reason,
                    "impact_summary": t.impact_summary,
                }
                for t in self.transform_history
            ],
        }

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "WorkflowIR":
        return cls(
            workflow_id=str(data["workflow_id"]),
            nodes=[
                WorkflowIRNode(
                    node_id=str(n["node_id"]),
                    command=str(n["command"]),
                    source_event_ids=list(n["source_event_ids"]),
                    window_id=str(n["window_id"]),
                    tab_id=str(n["tab_id"]),
                    origin_url=str(n["origin_url"]),
                    params=dict(n.get("params", {})),
                    value_origin=ValueOrigin(n.get("value_origin", ValueOrigin.USER_RECORDED.value)),
                    criticality=Criticality(n.get("criticality", Criticality.MANDATORY.value)),
                    depends_on=list(n.get("depends_on", [])),
                    side_effect=SideEffectLevel(
                        n.get("side_effect", SideEffectLevel.IDEMPOTENT_WRITE.value)
                    ),
                    human_approved=bool(n.get("human_approved", True)),
                )
                for n in data.get("nodes", [])
            ],
            transform_history=[
                EventTransformRecord(
                    source_event_ids=list(t["source_event_ids"]),
                    resulting_node_id=t.get("resulting_node_id"),
                    transform_type=str(t["transform_type"]),
                    reason=str(t["reason"]),
                    impact_summary=str(t["impact_summary"]),
                )
                for t in data.get("transform_history", [])
            ],
        )


@dataclass(frozen=True)
class PostConditionSpec:
    condition_type: str
    expected_value: Any
    target_locator: Optional[str] = None
    strict_verification: bool = True


@dataclass(frozen=True)
class RetryPolicy:
    max_attempts: int = 1
    backoff_ms: int = 0
    require_state_probe_on_unknown: bool = True
    allow_non_idempotent_retry: bool = False


@dataclass(frozen=True)
class MacroStep:
    step_id: str
    command: str
    params: Dict[str, Any]
    ir_node_id: str
    source_event_ids: List[str]
    criticality: Criticality = Criticality.MANDATORY
    on_failure: FailurePolicy = FailurePolicy.ABORT
    safe_to_continue_verified: bool = False
    value_origin: ValueOrigin = ValueOrigin.USER_RECORDED
    human_approved: bool = True
    side_effect: SideEffectLevel = SideEffectLevel.IDEMPOTENT_WRITE
    timeout_ms: int = 5_000
    retry_policy: RetryPolicy = field(default_factory=RetryPolicy)
    post_condition: Optional[PostConditionSpec] = None
    loop_step_ids: List[str] = field(default_factory=list)
    max_iterations: Optional[int] = None
    allowed_loop_error_codes: List[str] = field(default_factory=list)
    next_step_id: Optional[str] = None


@dataclass(frozen=True)
class ExecutableMacro:
    macro_id: str
    workflow_id: str
    steps: List[MacroStep]
    final_post_conditions: List[PostConditionSpec] = field(default_factory=list)

    def to_dict(self) -> Dict[str, Any]:
        return {
            "macro_id": self.macro_id,
            "workflow_id": self.workflow_id,
            "steps": [
                {
                    "step_id": s.step_id,
                    "command": s.command,
                    "params": dict(s.params),
                    "ir_node_id": s.ir_node_id,
                    "source_event_ids": list(s.source_event_ids),
                    "criticality": s.criticality.value,
                    "on_failure": s.on_failure.value,
                    "safe_to_continue_verified": s.safe_to_continue_verified,
                    "value_origin": s.value_origin.value,
                    "human_approved": s.human_approved,
                    "side_effect": s.side_effect.value,
                    "timeout_ms": s.timeout_ms,
                    "retry_policy": {
                        "max_attempts": s.retry_policy.max_attempts,
                        "backoff_ms": s.retry_policy.backoff_ms,
                        "require_state_probe_on_unknown": s.retry_policy.require_state_probe_on_unknown,
                        "allow_non_idempotent_retry": s.retry_policy.allow_non_idempotent_retry,
                    },
                    "post_condition": (
                        {
                            "condition_type": s.post_condition.condition_type,
                            "expected_value": s.post_condition.expected_value,
                            "target_locator": s.post_condition.target_locator,
                            "strict_verification": s.post_condition.strict_verification,
                        }
                        if s.post_condition
                        else None
                    ),
                    "loop_step_ids": list(s.loop_step_ids),
                    "max_iterations": s.max_iterations,
                    "allowed_loop_error_codes": list(s.allowed_loop_error_codes),
                    "next_step_id": s.next_step_id,
                }
                for s in self.steps
            ],
            "final_post_conditions": [
                {
                    "condition_type": pc.condition_type,
                    "expected_value": pc.expected_value,
                    "target_locator": pc.target_locator,
                    "strict_verification": pc.strict_verification,
                }
                for pc in self.final_post_conditions
            ],
        }


@dataclass(frozen=True)
class PostConditionResult:
    verified: bool
    status: ExecutionStatus
    expected_value: Any
    observed_value: Any
    reason: str


@dataclass(frozen=True)
class OperationResult:
    operation_id: str
    command: str
    status: ExecutionStatus
    started_at_ms: int
    ended_at_ms: int
    requested_params: Dict[str, Any]
    executed_params: Dict[str, Any]
    observed_value: Any = None
    error_code: Optional[str] = None
    error_message: Optional[str] = None
    post_condition_result: Optional[PostConditionResult] = None
    retry_count: int = 0
    retry_reasons: List[str] = field(default_factory=list)
    skipped_reason: Optional[str] = None
    criticality: Criticality = Criticality.MANDATORY
    downstream_impact: Optional[str] = None
    iteration_results: List["OperationResult"] = field(default_factory=list)


@dataclass(frozen=True)
class MacroExecutionReport:
    job_id: str
    macro_id: str
    final_status: ExecutionStatus
    reason: str
    step_results: List[OperationResult]
    final_post_condition_results: List[PostConditionResult]
    audit_logs: List[Dict[str, Any]]


class LegacyStatusMapper:
    _LEGACY_MAP: Dict[str, ExecutionStatus] = {
        "ok": ExecutionStatus.SUCCESS,
        "completed": ExecutionStatus.SUCCESS,
        "done": ExecutionStatus.SUCCESS,
        "partial": ExecutionStatus.PARTIAL_SUCCESS,
        "ng": ExecutionStatus.FAILED,
        "error": ExecutionStatus.FAILED,
        "fail": ExecutionStatus.FAILED,
        "aborted": ExecutionStatus.STOPPED,
        "halted": ExecutionStatus.STOPPED,
        "canceled": ExecutionStatus.CANCELLED,
        "timed_out": ExecutionStatus.TIMEOUT,
        "unverified": ExecutionStatus.UNKNOWN,
        "indeterminate": ExecutionStatus.UNKNOWN,
    }

    @classmethod
    def from_legacy(
        cls, raw_status: str, post_condition_verified: Optional[bool] = None
    ) -> ExecutionStatus:
        norm = raw_status.strip().lower()
        for st in ExecutionStatus:
            if st.value == norm:
                mapped = st
                break
        else:
            mapped = cls._LEGACY_MAP.get(norm, ExecutionStatus.UNKNOWN)

        # 事後条件が未確認の完了報告は成功にせずUNKNOWNへ降格する
        if mapped == ExecutionStatus.SUCCESS and post_condition_verified is False:
            return ExecutionStatus.UNKNOWN
        return mapped
