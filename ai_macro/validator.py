"""Module: @role: 実行前マクロの一括構造検証・参照整合性検証・Workflow IRとの意味的整合性検証"""

from typing import Any, Dict, List, Optional, Set
from ai_macro.models import (
    CommandType,
    Criticality,
    ExecutableMacro,
    FailurePolicy,
    MAX_COORDINATE,
    MAX_LOOP_ITERATIONS,
    MAX_STEPS,
    MAX_TIMEOUT_MS,
    MAX_WAIT_MS,
    MIN_COORDINATE,
    MIN_TIMEOUT_MS,
    MIN_WAIT_MS,
    MacroStep,
    PostConditionSpec,
    RetryPolicy,
    ValueOrigin,
    WorkflowIR,
)


class MacroValidationError(Exception):
    def __init__(self, errors: List[str]) -> None:
        self.errors: List[str] = errors
        super().__init__("; ".join(errors))


class MacroValidator:
    _REQUIRED_PARAMS: Dict[str, Dict[str, type]] = {
        CommandType.BROWSER_CLICK.value: {"selector": str},
        CommandType.BROWSER_INPUT.value: {"selector": str, "text": str},
        CommandType.BROWSER_SELECT.value: {"selector": str, "value": str},
        CommandType.BROWSER_CHECKBOX.value: {"selector": str, "checked": bool},
        CommandType.BROWSER_WAIT.value: {"selector": str, "wait_ms": int},
        CommandType.BROWSER_SCROLL.value: {"scroll_x": int, "scroll_y": int},
        CommandType.SCREEN_MATCH_CLICK.value: {"template_id": str, "x": int, "y": int},
        CommandType.PHYSICAL_MOUSE.value: {"x": int, "y": int, "button": str},
        CommandType.PHYSICAL_DRAG.value: {
            "start_x": int,
            "start_y": int,
            "end_x": int,
            "end_y": int,
            "button": str,
        },
        CommandType.PHYSICAL_KEY.value: {"key": str},
        CommandType.EXCEL_WRITE.value: {"workbook": str, "sheet": str, "cell": str, "value": object},
        CommandType.RUN_SCRIPT.value: {"script_id": str, "entrypoint": str},
        CommandType.LOOP.value: {"condition_key": str},
    }

    @classmethod
    def parse_and_validate_json(
        cls,
        raw_payload: Dict[str, Any],
        registered_commands: Set[str],
        workflow_ir: Optional[WorkflowIR] = None,
    ) -> ExecutableMacro:
        errors: List[str] = []
        if not isinstance(raw_payload, dict):
            raise MacroValidationError(["REQ-LOAD-001: Root payload must be a JSON object"])

        macro_id = raw_payload.get("macro_id")
        workflow_id = raw_payload.get("workflow_id")
        raw_steps = raw_payload.get("steps")
        if not isinstance(macro_id, str) or not macro_id.strip():
            errors.append("REQ-LOAD-001: 'macro_id' must be a non-empty string")
        if not isinstance(workflow_id, str) or not workflow_id.strip():
            errors.append("REQ-LOAD-001: 'workflow_id' must be a non-empty string")
        if not isinstance(raw_steps, list) or len(raw_steps) == 0:
            errors.append("REQ-LOAD-001: 'steps' must be a non-empty list")
            raise MacroValidationError(errors)

        steps: List[MacroStep] = []
        for idx, item in enumerate(raw_steps):
            if not isinstance(item, dict):
                errors.append(f"REQ-LOAD-001: Step at index {idx} must be an object")
                continue
            try:
                step = cls._dict_to_step(item)
                steps.append(step)
            except (KeyError, ValueError, TypeError) as exc:
                errors.append(f"REQ-LOAD-001: Step index {idx} structural error: {exc}")

        if errors:
            raise MacroValidationError(errors)

        macro = ExecutableMacro(
            macro_id=str(macro_id),
            workflow_id=str(workflow_id),
            steps=steps,
            final_post_conditions=[
                PostConditionSpec(**pc) for pc in raw_payload.get("final_post_conditions", [])
            ],
        )
        cls.validate_macro(macro, registered_commands, workflow_ir)
        return macro

    @classmethod
    def _dict_to_step(cls, data: Dict[str, Any]) -> MacroStep:
        retry_raw = data.get("retry_policy", {})
        post_raw = data.get("post_condition")
        return MacroStep(
            step_id=str(data["step_id"]),
            command=str(data["command"]),
            params=dict(data.get("params", {})),
            ir_node_id=str(data.get("ir_node_id", "")),
            source_event_ids=list(data.get("source_event_ids", [])),
            criticality=Criticality(data.get("criticality", Criticality.MANDATORY.value)),
            on_failure=FailurePolicy(data.get("on_failure", FailurePolicy.ABORT.value)),
            safe_to_continue_verified=bool(data.get("safe_to_continue_verified", False)),
            value_origin=ValueOrigin(data.get("value_origin", ValueOrigin.USER_RECORDED.value)),
            human_approved=bool(data.get("human_approved", True)),
            timeout_ms=int(data.get("timeout_ms", 5_000)),
            retry_policy=RetryPolicy(**retry_raw) if isinstance(retry_raw, dict) else RetryPolicy(),
            post_condition=PostConditionSpec(**post_raw) if isinstance(post_raw, dict) else None,
            loop_step_ids=list(data.get("loop_step_ids", [])),
            max_iterations=data.get("max_iterations"),
            allowed_loop_error_codes=list(data.get("allowed_loop_error_codes", [])),
            next_step_id=data.get("next_step_id"),
        )

    @classmethod
    def validate_macro(
        cls,
        macro: ExecutableMacro,
        registered_commands: Set[str],
        workflow_ir: Optional[WorkflowIR] = None,
    ) -> None:
        errors: List[str] = []
        if not macro.steps:
            errors.append("REQ-LOAD-001: Macro must contain at least one step")
        if len(macro.steps) > MAX_STEPS:
            errors.append(f"REQ-LOAD-005: Step count exceeds MAX_STEPS ({MAX_STEPS})")

        step_ids: Set[str] = set()
        for step in macro.steps:
            if not step.step_id:
                errors.append("REQ-LOAD-004: Empty step_id detected")
            elif step.step_id in step_ids:
                errors.append(f"REQ-LOAD-004: Duplicate step_id '{step.step_id}'")
            step_ids.add(step.step_id)

        valid_command_values = {c.value for c in CommandType}
        for step in macro.steps:
            if step.command not in valid_command_values or step.command not in registered_commands:
                errors.append(
                    f"REQ-LOAD-002/REQ-DSP-001: Unregistered or unknown command '{step.command}' in '{step.step_id}'"
                )
                continue

            cls._validate_step_params(step, errors)
            cls._validate_step_boundaries(step, errors)
            cls._validate_step_references(step, step_ids, errors)
            cls._validate_step_semantics(step, errors)

        if workflow_ir is not None:
            cls._validate_workflow_consistency(macro, workflow_ir, errors)

        if errors:
            raise MacroValidationError(errors)

    @classmethod
    def _validate_step_params(cls, step: MacroStep, errors: List[str]) -> None:
        req_map = cls._REQUIRED_PARAMS.get(step.command, {})
        for param_name, expected_type in req_map.items():
            if param_name not in step.params or step.params[param_name] is None:
                errors.append(
                    f"REQ-LOAD-003: Missing required param '{param_name}' in step '{step.step_id}'"
                )
                continue
            val = step.params[param_name]
            # boolはintのサブクラスのため厳密に型を区別する
            if expected_type is int and (isinstance(val, bool) or not isinstance(val, int)):
                errors.append(
                    f"REQ-LOAD-003: Param '{param_name}' in '{step.step_id}' must be int"
                )
            elif expected_type is not int and not isinstance(val, expected_type):
                errors.append(
                    f"REQ-LOAD-003: Param '{param_name}' in '{step.step_id}' must be {expected_type.__name__}"
                )

        if step.command == CommandType.BROWSER_CLICK.value:
            if step.params.get("fallback_coordinates") is not None:
                if not step.params.get("allow_semantic_fallback") or not step.params.get("target_signature"):
                    errors.append(
                        f"REQ-ACT-008: Fallback coordinates in '{step.step_id}' require allow_semantic_fallback and target_signature"
                    )

        if step.command in {CommandType.PHYSICAL_MOUSE.value, CommandType.PHYSICAL_DRAG.value}:
            if step.params.get("button") not in {"left", "right", "middle"}:
                errors.append(f"REQ-LOAD-003: Invalid mouse button in '{step.step_id}'")
            if not step.params.get("require_screen_match", False) and not step.params.get("verified_window_id"):
                errors.append(
                    f"REQ-ACT-002: Physical pointer action in '{step.step_id}' requires verified_window_id or require_screen_match"
                )

    @classmethod
    def _validate_step_boundaries(cls, step: MacroStep, errors: List[str]) -> None:
        if isinstance(step.timeout_ms, bool) or not (MIN_TIMEOUT_MS <= step.timeout_ms <= MAX_TIMEOUT_MS):
            errors.append(
                f"REQ-LOAD-005: timeout_ms out of range in '{step.step_id}' ({step.timeout_ms})"
            )
        if step.command == CommandType.BROWSER_WAIT.value:
            wait_ms = step.params.get("wait_ms")
            if isinstance(wait_ms, int) and not isinstance(wait_ms, bool):
                if not (MIN_WAIT_MS <= wait_ms <= MAX_WAIT_MS):
                    errors.append(f"REQ-LOAD-005: wait_ms out of range in '{step.step_id}'")
        for coord_key in ("x", "y", "start_x", "start_y", "end_x", "end_y"):
            if coord_key in step.params and isinstance(step.params[coord_key], int):
                coord_val = step.params[coord_key]
                if not (MIN_COORDINATE <= coord_val <= MAX_COORDINATE):
                    errors.append(
                        f"REQ-LOAD-005: Coordinate '{coord_key}' out of bounds in '{step.step_id}'"
                    )
        if step.command == CommandType.LOOP.value:
            cond_key = str(step.params.get("condition_key", "")).strip()
            if not cond_key:
                errors.append(f"REQ-GEN-007: Loop '{step.step_id}' requires non-empty condition_key")
            if step.max_iterations is None or isinstance(step.max_iterations, bool) or not (
                1 <= step.max_iterations <= MAX_LOOP_ITERATIONS
            ):
                errors.append(
                    f"REQ-LOAD-005/REQ-GEN-007: Loop '{step.step_id}' must define valid max_iterations (1..{MAX_LOOP_ITERATIONS})"
                )
            if step.value_origin in {ValueOrigin.AI_INFERRED, ValueOrigin.SNAPSHOT_INFERRED}:
                errors.append(
                    f"REQ-GEN-007: Speculative or inferred loop in '{step.step_id}' is prohibited"
                )

    @classmethod
    def _validate_step_references(
        cls, step: MacroStep, step_ids: Set[str], errors: List[str]
    ) -> None:
        if step.next_step_id is not None and step.next_step_id not in step_ids:
            errors.append(
                f"REQ-LOAD-004: Unknown next_step_id '{step.next_step_id}' in '{step.step_id}'"
            )
        if step.command == CommandType.LOOP.value:
            if not step.loop_step_ids:
                errors.append(f"REQ-LOAD-004: Loop '{step.step_id}' has empty loop_step_ids")
            for target_id in step.loop_step_ids:
                if target_id == step.step_id or target_id not in step_ids:
                    errors.append(
                        f"REQ-LOAD-004: Invalid loop target '{target_id}' in '{step.step_id}'"
                    )

    @classmethod
    def _validate_step_semantics(cls, step: MacroStep, errors: List[str]) -> None:
        # AI推測値やスナップショット推測値は人間承認なしでの実行を禁止する
        if step.value_origin in {ValueOrigin.AI_INFERRED, ValueOrigin.SNAPSHOT_INFERRED}:
            if not step.human_approved:
                errors.append(
                    f"REQ-GEN-004/REQ-AI-005: Inferred value in '{step.step_id}' requires explicit human_approved=True"
                )
        if step.criticality == Criticality.MANDATORY and step.on_failure in {
            FailurePolicy.CONTINUE,
            FailurePolicy.SKIP,
        }:
            if not step.safe_to_continue_verified:
                errors.append(
                    f"REQ-DSP-004: Mandatory step '{step.step_id}' cannot use '{step.on_failure.value}' without safe_to_continue_verified=True"
                )

    @classmethod
    def _validate_workflow_consistency(
        cls, macro: ExecutableMacro, workflow_ir: WorkflowIR, errors: List[str]
    ) -> None:
        if macro.workflow_id != workflow_ir.workflow_id:
            errors.append(
                f"REQ-LOAD-007: workflow_id mismatch ('{macro.workflow_id}' != '{workflow_ir.workflow_id}')"
            )
        ir_nodes = {n.node_id: n for n in workflow_ir.nodes}
        mapped_ir_ids: Set[str] = set()
        ir_order_index = {n.node_id: idx for idx, n in enumerate(workflow_ir.nodes)}
        observed_indices: Dict[str, int] = {}

        for idx, step in enumerate(macro.steps):
            if not step.ir_node_id or step.ir_node_id not in ir_nodes:
                errors.append(
                    f"REQ-LOAD-007/REQ-GEN-001: Step '{step.step_id}' lacks valid ir_node_id reference"
                )
                continue
            if not step.source_event_ids:
                errors.append(
                    f"REQ-GEN-001: Step '{step.step_id}' lacks source_event_ids provenance"
                )
            ir_node = ir_nodes[step.ir_node_id]
            mapped_ir_ids.add(step.ir_node_id)
            observed_indices[step.ir_node_id] = idx

            if step.command != CommandType.LOOP.value and step.command != ir_node.command:
                errors.append(
                    f"REQ-GEN-003: Semantic command mismatch on '{step.step_id}' ('{step.command}' vs '{ir_node.command}')"
                )
            for dep_id in ir_node.depends_on:
                if dep_id in observed_indices and observed_indices[dep_id] > idx:
                    errors.append(
                        f"REQ-GEN-006: Dependency order violated for '{step.ir_node_id}' depending on '{dep_id}'"
                    )

        for node in workflow_ir.nodes:
            if node.criticality == Criticality.MANDATORY and node.node_id not in mapped_ir_ids:
                errors.append(
                    f"REQ-LOAD-007/REQ-GEN-008: Mandatory IR node '{node.node_id}' is missing from executable macro"
                )
