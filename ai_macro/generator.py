"""Module: @role: 操作記録の正規化・集約・Workflow IR生成・最適化・AIモード制御および実行可能マクロ生成"""

import ipaddress
from dataclasses import dataclass
from typing import Any, Callable, Dict, List, Optional, Tuple
from urllib.parse import urlparse

from ai_macro.models import (
    AIMode,
    CommandType,
    Criticality,
    EventTransformRecord,
    ExecutableMacro,
    FailurePolicy,
    MacroStep,
    PostConditionSpec,
    RecordedEvent,
    RetryPolicy,
    SideEffectLevel,
    ValueOrigin,
    WorkflowIR,
    WorkflowIRNode,
)
from ai_macro.validator import MacroValidationError, MacroValidator


@dataclass(frozen=True)
class AIModeConfig:
    mode: AIMode = AIMode.NO_AI
    endpoint_url: Optional[str] = None
    explicit_external_consent: bool = False


class AIModeController:
    def __init__(self, config: AIModeConfig) -> None:
        self._config = config
        self._validate_mode_config()

    @property
    def mode(self) -> AIMode:
        return self._config.mode

    def _validate_mode_config(self) -> None:
        mode = self._config.mode
        url = self._config.endpoint_url
        if mode == AIMode.NO_AI:
            return
        if not url:
            raise ValueError(f"REQ-AI-002: endpoint_url is required for AI mode '{mode.value}'")
        parsed = urlparse(url)
        host = parsed.hostname or ""
        if mode == AIMode.LOCAL_AI:
            if host not in {"localhost", "127.0.0.1", "::1"}:
                raise ValueError(f"REQ-AI-002: LOCAL_AI only allows loopback host, got '{host}'")
        elif mode == AIMode.LAN_AI_SERVER:
            try:
                ip_obj = ipaddress.ip_address(host)
                if not ip_obj.is_private or ip_obj.is_loopback:
                    raise ValueError("Not a LAN private IP")
            except ValueError as exc:
                raise ValueError(
                    f"REQ-AI-002: LAN_AI_SERVER requires RFC1918 private IP host, got '{host}'"
                ) from exc
        elif mode == AIMode.EXTERNAL_AI:
            if not self._config.explicit_external_consent:
                raise PermissionError(
                    "REQ-AI-003: EXTERNAL_AI requires explicit_external_consent=True"
                )

    def invoke_ai_suggestion(
        self,
        payload: Dict[str, Any],
        ai_client: Optional[Callable[[Dict[str, Any]], Dict[str, Any]]] = None,
    ) -> Tuple[Dict[str, Any], ValueOrigin]:
        if self._config.mode == AIMode.NO_AI:
            raise PermissionError("REQ-AI-004: AI invocation is strictly prohibited in NO_AI mode")
        if ai_client is None:
            raise RuntimeError("AI client callable is not configured")
        # 外部AI送信時は認証情報や機密キーを事前マスクする
        safe_payload = (
            self._redact_for_external(payload)
            if self._config.mode == AIMode.EXTERNAL_AI
            else payload
        )
        suggestion = ai_client(safe_payload)
        return suggestion, ValueOrigin.AI_INFERRED

    @classmethod
    def _redact_for_external(cls, data: Any) -> Any:
        if isinstance(data, dict):
            return {
                k: (
                    "***REDACTED***"
                    if any(s in str(k).lower() for s in ("password", "secret", "token", "api_key"))
                    else cls._redact_for_external(v)
                )
                for k, v in data.items()
            }
        if isinstance(data, list):
            return [cls._redact_for_external(x) for x in data]
        return data


class MacroGenerationPipeline:
    _ACTION_TO_COMMAND: Dict[str, Tuple[CommandType, SideEffectLevel]] = {
        "click": (CommandType.BROWSER_CLICK, SideEffectLevel.NON_IDEMPOTENT_MUTATION),
        "input": (CommandType.BROWSER_INPUT, SideEffectLevel.IDEMPOTENT_WRITE),
        "select": (CommandType.BROWSER_SELECT, SideEffectLevel.IDEMPOTENT_WRITE),
        "checkbox": (CommandType.BROWSER_CHECKBOX, SideEffectLevel.IDEMPOTENT_WRITE),
        "wait": (CommandType.BROWSER_WAIT, SideEffectLevel.READ_ONLY),
        "scroll": (CommandType.BROWSER_SCROLL, SideEffectLevel.READ_ONLY),
        "screen_click": (CommandType.SCREEN_MATCH_CLICK, SideEffectLevel.NON_IDEMPOTENT_MUTATION),
        "physical_drag": (CommandType.PHYSICAL_DRAG, SideEffectLevel.NON_IDEMPOTENT_MUTATION),
        "excel_write": (CommandType.EXCEL_WRITE, SideEffectLevel.IDEMPOTENT_WRITE),
        "script": (CommandType.RUN_SCRIPT, SideEffectLevel.NON_IDEMPOTENT_MUTATION),
    }

    def __init__(self, ai_controller: Optional[AIModeController] = None) -> None:
        self._ai_controller = ai_controller or AIModeController(AIModeConfig(mode=AIMode.NO_AI))

    @staticmethod
    def normalize_raw_events(raw_records: List[Dict[str, Any]]) -> List[RecordedEvent]:
        normalized: List[RecordedEvent] = []
        for idx, rec in enumerate(raw_records):
            event_id = str(rec.get("event_id") or f"ev_{idx + 1:04d}")
            normalized.append(
                RecordedEvent(
                    event_id=event_id,
                    timestamp_ms=int(rec["timestamp_ms"]),
                    action_type=str(rec["action_type"]).strip().lower(),
                    window_id=str(rec["window_id"]).strip(),
                    tab_id=str(rec["tab_id"]).strip(),
                    origin_url=str(rec["origin_url"]).strip(),
                    selector=str(rec["selector"]).strip() if rec.get("selector") else None,
                    value=rec.get("value"),
                    value_origin=ValueOrigin(
                        rec.get("value_origin", ValueOrigin.USER_RECORDED.value)
                    ),
                    criticality=Criticality(
                        rec.get("criticality", Criticality.MANDATORY.value)
                    ),
                    metadata=dict(rec.get("metadata", {})),
                )
            )
        return normalized

    def optimize_workflow_ir(self, workflow_ir: WorkflowIR) -> WorkflowIR:
        optimized_nodes: List[WorkflowIRNode] = []
        transforms = list(workflow_ir.transform_history)
        idx = 0
        nodes = workflow_ir.nodes
        total = len(nodes)

        while idx < total:
            curr = nodes[idx]
            # 同一コンテキスト内の連続スクロールのみ最終位置へ集約する
            if curr.command == CommandType.BROWSER_SCROLL.value:
                merged_events = list(curr.source_event_ids)
                last_scroll = curr
                while (
                    idx + 1 < total
                    and nodes[idx + 1].command == CommandType.BROWSER_SCROLL.value
                    and nodes[idx + 1].window_id == curr.window_id
                    and nodes[idx + 1].tab_id == curr.tab_id
                    and nodes[idx + 1].origin_url == curr.origin_url
                ):
                    idx += 1
                    last_scroll = nodes[idx]
                    merged_events.extend(last_scroll.source_event_ids)

                if last_scroll.node_id != curr.node_id:
                    transforms.append(
                        EventTransformRecord(
                            source_event_ids=merged_events,
                            resulting_node_id=curr.node_id,
                            transform_type="optimize_consecutive_scroll",
                            reason="Merged consecutive scroll nodes in identical context",
                            impact_summary="Intermediate scroll positions consolidated into final coordinates",
                        )
                    )
                depends = [optimized_nodes[-1].node_id] if optimized_nodes else []
                optimized_nodes.append(
                    WorkflowIRNode(
                        node_id=curr.node_id,
                        command=curr.command,
                        source_event_ids=merged_events,
                        window_id=curr.window_id,
                        tab_id=curr.tab_id,
                        origin_url=curr.origin_url,
                        params=dict(last_scroll.params),
                        value_origin=last_scroll.value_origin,
                        criticality=curr.criticality,
                        depends_on=depends,
                        side_effect=curr.side_effect,
                        human_approved=curr.human_approved and last_scroll.human_approved,
                    )
                )
            else:
                depends = [optimized_nodes[-1].node_id] if optimized_nodes else []
                optimized_nodes.append(
                    WorkflowIRNode(
                        node_id=curr.node_id,
                        command=curr.command,
                        source_event_ids=list(curr.source_event_ids),
                        window_id=curr.window_id,
                        tab_id=curr.tab_id,
                        origin_url=curr.origin_url,
                        params=dict(curr.params),
                        value_origin=curr.value_origin,
                        criticality=curr.criticality,
                        depends_on=depends,
                        side_effect=curr.side_effect,
                        human_approved=curr.human_approved,
                    )
                )
            idx += 1

        return WorkflowIR(
            workflow_id=workflow_ir.workflow_id,
            nodes=optimized_nodes,
            transform_history=transforms,
        )

    def build_workflow_ir(
        self, workflow_id: str, raw_events: List[RecordedEvent]
    ) -> WorkflowIR:
        if not raw_events:
            raise MacroValidationError(["REQ-GEN-008: Cannot generate workflow from empty events"])

        transforms: List[EventTransformRecord] = []
        nodes: List[WorkflowIRNode] = []
        idx = 0
        total = len(raw_events)

        while idx < total:
            ev = raw_events[idx]
            if ev.action_type not in self._ACTION_TO_COMMAND:
                transforms.append(
                    EventTransformRecord(
                        source_event_ids=[ev.event_id],
                        resulting_node_id=None,
                        transform_type="excluded_unsupported_event",
                        reason=f"Unsupported raw action_type '{ev.action_type}'",
                        impact_summary="Event excluded before IR generation",
                    )
                )
                if ev.criticality == Criticality.MANDATORY:
                    raise MacroValidationError(
                        [f"REQ-GEN-008: Mandatory event '{ev.event_id}' could not be mapped"]
                    )
                idx += 1
                continue

            # 同一コンテキスト・同一セレクターへの連続文字入力のみ安全に集約する
            merged_ids = [ev.event_id]
            final_ev = ev
            while (
                ev.action_type == "input"
                and idx + 1 < total
                and raw_events[idx + 1].action_type == "input"
                and self._is_same_context(ev, raw_events[idx + 1])
                and ev.selector == raw_events[idx + 1].selector
                and ev.value_origin == raw_events[idx + 1].value_origin
            ):
                idx += 1
                final_ev = raw_events[idx]
                merged_ids.append(final_ev.event_id)

            node_id = f"node_{len(nodes) + 1:03d}"
            if len(merged_ids) > 1:
                transforms.append(
                    EventTransformRecord(
                        source_event_ids=merged_ids,
                        resulting_node_id=node_id,
                        transform_type="aggregate_consecutive_input",
                        reason="Consecutive input events on identical context and selector",
                        impact_summary=f"Consolidated {len(merged_ids)} events into final input state",
                    )
                )

            cmd_type, side_effect = self._ACTION_TO_COMMAND[final_ev.action_type]
            params = self._build_node_params(cmd_type, final_ev)
            depends_on = [nodes[-1].node_id] if nodes else []
            nodes.append(
                WorkflowIRNode(
                    node_id=node_id,
                    command=cmd_type.value,
                    source_event_ids=merged_ids,
                    window_id=final_ev.window_id,
                    tab_id=final_ev.tab_id,
                    origin_url=final_ev.origin_url,
                    params=params,
                    value_origin=final_ev.value_origin,
                    criticality=final_ev.criticality,
                    depends_on=depends_on,
                    side_effect=side_effect,
                    human_approved=(final_ev.value_origin == ValueOrigin.USER_RECORDED)
                    or bool(final_ev.metadata.get("human_approved", False)),
                )
            )
            idx += 1

        return WorkflowIR(workflow_id=workflow_id, nodes=nodes, transform_history=transforms)

    @staticmethod
    def _is_same_context(a: RecordedEvent, b: RecordedEvent) -> bool:
        return (
            a.window_id == b.window_id
            and a.tab_id == b.tab_id
            and a.origin_url == b.origin_url
        )

    @staticmethod
    def _build_node_params(cmd: CommandType, ev: RecordedEvent) -> Dict[str, Any]:
        if cmd == CommandType.BROWSER_CLICK:
            if not ev.selector:
                raise MacroValidationError([f"REQ-GEN-005: Missing selector in '{ev.event_id}'"])
            return {"selector": ev.selector, "context_origin": ev.origin_url}
        if cmd == CommandType.BROWSER_INPUT:
            return {"selector": ev.selector, "text": str(ev.value), "context_origin": ev.origin_url}
        if cmd == CommandType.BROWSER_SELECT:
            return {"selector": ev.selector, "value": str(ev.value), "context_origin": ev.origin_url}
        if cmd == CommandType.BROWSER_CHECKBOX:
            return {"selector": ev.selector, "checked": bool(ev.value), "context_origin": ev.origin_url}
        if cmd == CommandType.BROWSER_WAIT:
            return {"selector": ev.selector, "wait_ms": int(ev.value or 1000)}
        if cmd == CommandType.BROWSER_SCROLL:
            meta = ev.metadata
            return {"scroll_x": int(meta.get("scroll_x", 0)), "scroll_y": int(meta.get("scroll_y", 0))}
        if cmd == CommandType.SCREEN_MATCH_CLICK:
            meta = ev.metadata
            return {
                "template_id": str(meta["template_id"]),
                "x": int(meta["x"]),
                "y": int(meta["y"]),
            }
        if cmd == CommandType.PHYSICAL_DRAG:
            meta = ev.metadata
            return {
                "start_x": int(meta["start_x"]),
                "start_y": int(meta["start_y"]),
                "end_x": int(meta["end_x"]),
                "end_y": int(meta["end_y"]),
                "button": str(meta.get("button", "left")),
                "verified_window_id": ev.window_id,
            }
        if cmd == CommandType.EXCEL_WRITE:
            meta = ev.metadata
            return {
                "workbook": str(meta["workbook"]),
                "sheet": str(meta["sheet"]),
                "cell": str(meta["cell"]),
                "value": ev.value,
            }
        if cmd == CommandType.RUN_SCRIPT:
            meta = ev.metadata
            return {"script_id": str(meta["script_id"]), "entrypoint": str(meta["entrypoint"])}
        return dict(ev.metadata)

    def generate_executable_macro(
        self,
        macro_id: str,
        workflow_ir: WorkflowIR,
        registered_commands: Optional[set[str]] = None,
    ) -> ExecutableMacro:
        steps: List[MacroStep] = []
        for idx, node in enumerate(workflow_ir.nodes):
            post_cond = self._default_post_condition(node)
            step = MacroStep(
                step_id=f"step_{idx + 1:03d}",
                command=node.command,
                params=dict(node.params),
                ir_node_id=node.node_id,
                source_event_ids=list(node.source_event_ids),
                criticality=node.criticality,
                on_failure=FailurePolicy.ABORT
                if node.criticality == Criticality.MANDATORY
                else FailurePolicy.SKIP,
                safe_to_continue_verified=(node.criticality == Criticality.OPTIONAL),
                value_origin=node.value_origin,
                human_approved=node.human_approved,
                side_effect=node.side_effect,
                post_condition=post_cond,
            )
            steps.append(step)

        macro = ExecutableMacro(
            macro_id=macro_id,
            workflow_id=workflow_ir.workflow_id,
            steps=steps,
        )
        reg = registered_commands or {c.value for c in CommandType}
        MacroValidator.validate_macro(macro, reg, workflow_ir)
        return macro

    @staticmethod
    def _default_post_condition(node: WorkflowIRNode) -> Optional[PostConditionSpec]:
        if node.command == CommandType.BROWSER_INPUT.value:
            return PostConditionSpec(
                condition_type="dom_value_equals",
                expected_value=node.params.get("text"),
                target_locator=node.params.get("selector"),
            )
        if node.command == CommandType.BROWSER_SELECT.value:
            return PostConditionSpec(
                condition_type="selected_value_equals",
                expected_value=node.params.get("value"),
                target_locator=node.params.get("selector"),
            )
        if node.command == CommandType.BROWSER_CHECKBOX.value:
            return PostConditionSpec(
                condition_type="checkbox_state_equals",
                expected_value=node.params.get("checked"),
                target_locator=node.params.get("selector"),
            )
        if node.command == CommandType.EXCEL_WRITE.value:
            return PostConditionSpec(
                condition_type="excel_cell_equals",
                expected_value=node.params.get("value"),
                target_locator=f"{node.params.get('workbook')}!{node.params.get('sheet')}!{node.params.get('cell')}",
            )
        return None
