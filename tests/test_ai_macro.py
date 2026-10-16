"""Module: @role: REQ-TEST-001からREQ-TEST-012および全受け入れ基準を網羅する自動テストスイート"""

import unittest
from typing import Any, Dict, Optional, Tuple

from ai_macro import (
    AIMode,
    AIModeConfig,
    AIModeController,
    BrowserAdapter,
    CommandType,
    Criticality,
    ExcelAdapter,
    ExecutableMacro,
    ExecutionJobContext,
    ExecutionStatus,
    FailurePolicy,
    LegacyStatusMapper,
    MacroGenerationPipeline,
    WorkflowIR,
    MacroRunner,
    MacroStep,
    MacroValidationError,
    MacroValidator,
    OperationResult,
    PhysicalInputAdapter,
    RecordedEvent,
    RetryPolicy,
    ScreenRecognitionAdapter,
    ScriptAdapter,
    SideEffectLevel,
    ValueOrigin,
    WorkflowIR,
)


class FakeBrowserDriver:
    def __init__(self) -> None:
        self.focus_ok: bool = True
        self.dom_values: Dict[str, Any] = {}
        self.checkboxes: Dict[str, bool] = {"#agree": False}
        self.click_ok: bool = True
        self.fallback_ok: bool = True
        self.wait_ok: bool = True
        self.scroll_ok: bool = True

    def check_focus_and_target(self, selector: str, expected_origin: Optional[str]) -> bool:
        return self.focus_ok

    def click(self, selector: str) -> Tuple[bool, Optional[str]]:
        return (True, None) if self.click_ok else (False, "Element not clickable")

    def click_coordinates(
        self, x: int, y: int, target_signature: str
    ) -> Tuple[bool, Optional[str]]:
        return (True, None) if self.fallback_ok else (False, "Fallback target mismatch")

    def input_text(self, selector: str, text: str) -> Tuple[bool, Optional[str]]:
        self.dom_values[selector] = text
        return True, None

    def get_element_value(self, selector: str) -> Tuple[bool, Any]:
        if selector not in self.dom_values:
            return False, None
        return True, self.dom_values[selector]

    def select_option(self, selector: str, value: str) -> Tuple[bool, Optional[str]]:
        self.dom_values[selector] = value
        return True, None

    def get_checkbox_state(self, selector: str) -> Tuple[bool, Optional[bool]]:
        if selector not in self.checkboxes:
            return False, None
        return True, self.checkboxes[selector]

    def set_checkbox_state(self, selector: str, checked: bool) -> Tuple[bool, Optional[str]]:
        self.checkboxes[selector] = checked
        return True, None

    def wait_for_element(self, selector: str, wait_ms: int) -> Tuple[bool, Optional[str]]:
        return (True, None) if self.wait_ok else (False, "Wait timeout")

    def scroll_to(
        self, scroll_x: int, scroll_y: int
    ) -> Tuple[bool, Optional[int], Optional[int], Optional[str]]:
        if not self.scroll_ok:
            return False, 0, 0, "Scroll blocked"
        return True, scroll_x, scroll_y, None


class FakeScreenRecognizer:
    def __init__(self, status: str = "matched") -> None:
        self.status = status

    def match_template(self, template_id: str, timeout_ms: int) -> Dict[str, Any]:
        return {"status": self.status, "reason": f"status={self.status}"}


class FakePhysicalDriver:
    def __init__(self, ok: bool = True) -> None:
        self.ok = ok
        self.clicks: int = 0

    def click_at(self, x: int, y: int, button: str) -> Tuple[bool, Optional[str]]:
        if not self.ok:
            return False, "Hardware input blocked"
        self.clicks += 1
        return True, None

    def drag_from_to(
        self, start_x: int, start_y: int, end_x: int, end_y: int, button: str
    ) -> Tuple[bool, Optional[str]]:
        return (True, None) if self.ok else (False, "Drag blocked")

    def press_key(self, key: str) -> Tuple[bool, Optional[str]]:
        return (True, None) if self.ok else (False, "Key press blocked")


class FakeExcelDriver:
    def __init__(self, supported: bool = True, exists: bool = True) -> None:
        self.supported = supported
        self.exists = exists
        self.cells: Dict[str, Any] = {}
        self.corrupt_write: bool = False

    def is_supported(self) -> bool:
        return self.supported

    def target_exists(self, workbook: str, sheet: str, cell: str) -> Tuple[bool, Optional[str]]:
        return (True, None) if self.exists else (False, "Sheet or cell missing")

    def write_cell(
        self, workbook: str, sheet: str, cell: str, value: Any
    ) -> Tuple[bool, Optional[str]]:
        self.cells[f"{workbook}!{sheet}!{cell}"] = "WRONG_VAL" if self.corrupt_write else value
        return True, None

    def read_cell(self, workbook: str, sheet: str, cell: str) -> Tuple[bool, Any]:
        key = f"{workbook}!{sheet}!{cell}"
        return (True, self.cells[key]) if key in self.cells else (False, None)


class FakeScriptRunner:
    def __init__(self, executed: bool = True, exit_code: int = 0) -> None:
        self.executed = executed
        self.exit_code = exit_code

    def run_script(self, script_id: str, entrypoint: str) -> Dict[str, Any]:
        return {"executed": self.executed, "exit_code": self.exit_code, "output": "ok"}


class TestAIMacroSystem(unittest.TestCase):
    def setUp(self) -> None:
        self._now = 1000
        self.clock = lambda: self._now
        self.browser_drv = FakeBrowserDriver()
        self.screen_rec = FakeScreenRecognizer()
        self.phys_drv = FakePhysicalDriver()
        self.excel_drv = FakeExcelDriver()
        self.script_drv = FakeScriptRunner()

        self.screen_adapter = ScreenRecognitionAdapter(self.screen_rec, self.phys_drv, self.clock)
        self.browser_adapter = BrowserAdapter(self.browser_drv, self.clock)
        self.phys_adapter = PhysicalInputAdapter(self.phys_drv, self.screen_adapter, self.clock)
        self.excel_adapter = ExcelAdapter(self.excel_drv, self.clock)
        self.script_adapter = ScriptAdapter(self.script_drv, self.clock)

        self.handlers = {
            CommandType.BROWSER_CLICK.value: self.browser_adapter.execute,
            CommandType.BROWSER_INPUT.value: self.browser_adapter.execute,
            CommandType.BROWSER_SELECT.value: self.browser_adapter.execute,
            CommandType.BROWSER_CHECKBOX.value: self.browser_adapter.execute,
            CommandType.BROWSER_WAIT.value: self.browser_adapter.execute,
            CommandType.BROWSER_SCROLL.value: self.browser_adapter.execute,
            CommandType.SCREEN_MATCH_CLICK.value: self.screen_adapter.execute,
            CommandType.PHYSICAL_MOUSE.value: self.phys_adapter.execute,
            CommandType.PHYSICAL_DRAG.value: self.phys_adapter.execute,
            CommandType.PHYSICAL_KEY.value: self.phys_adapter.execute,
            CommandType.EXCEL_WRITE.value: self.excel_adapter.execute,
            CommandType.RUN_SCRIPT.value: self.script_adapter.execute,
        }

    def test_req_test_001_pre_execution_batch_validation(self) -> None:
        bad_payload = {
            "macro_id": "m1",
            "workflow_id": "w1",
            "steps": [
                {
                    "step_id": "s1",
                    "command": "unknown_cmd",
                    "params": {},
                    "ir_node_id": "n1",
                    "source_event_ids": ["e1"],
                },
                {
                    "step_id": "s1",
                    "command": CommandType.BROWSER_WAIT.value,
                    "params": {"selector": "#a", "wait_ms": 999_999},
                    "timeout_ms": -5,
                    "ir_node_id": "n2",
                    "source_event_ids": ["e2"],
                    "next_step_id": "non_existent",
                },
            ],
        }
        with self.assertRaises(MacroValidationError) as ctx:
            MacroValidator.parse_and_validate_json(bad_payload, set(self.handlers.keys()))
        msg = str(ctx.exception)
        self.assertIn("REQ-LOAD-004: Duplicate step_id", msg)
        self.assertIn("REQ-LOAD-002", msg)
        self.assertIn("REQ-LOAD-005", msg)

    def test_req_test_002_and_008_failure_propagation_and_final_state(self) -> None:
        self.browser_drv.focus_ok = False
        step1 = MacroStep(
            step_id="s1",
            command=CommandType.BROWSER_INPUT.value,
            params={"selector": "#user", "text": "alice"},
            ir_node_id="n1",
            source_event_ids=["e1"],
        )
        step2 = MacroStep(
            step_id="s2",
            command=CommandType.BROWSER_CLICK.value,
            params={"selector": "#submit"},
            ir_node_id="n2",
            source_event_ids=["e2"],
        )
        macro = ExecutableMacro(macro_id="m1", workflow_id="w1", steps=[step1, step2])
        runner = MacroRunner(self.handlers, self.clock)
        report = runner.run(macro, ExecutionJobContext("job_1"))
        self.assertEqual(report.final_status, ExecutionStatus.FAILED)
        self.assertEqual(report.step_results[0].status, ExecutionStatus.FAILED)
        self.assertEqual(report.step_results[1].status, ExecutionStatus.SKIPPED)

    def test_req_test_003_screen_recognition_blocks_click_on_failure(self) -> None:
        self.screen_rec.status = "mismatch"
        step = MacroStep(
            step_id="s_screen",
            command=CommandType.SCREEN_MATCH_CLICK.value,
            params={"template_id": "btn.png", "x": 100, "y": 200},
            ir_node_id="n1",
            source_event_ids=["e1"],
        )
        res = self.screen_adapter.execute(step)
        self.assertEqual(res.status, ExecutionStatus.FAILED)
        self.assertEqual(self.phys_drv.clicks, 0)

    def test_req_test_004_browser_post_conditions_and_checkbox(self) -> None:
        cb_step = MacroStep(
            step_id="s_cb",
            command=CommandType.BROWSER_CHECKBOX.value,
            params={"selector": "#agree", "checked": True},
            ir_node_id="n1",
            source_event_ids=["e1"],
        )
        res = self.browser_adapter.execute(cb_step)
        self.assertEqual(res.status, ExecutionStatus.SUCCESS)
        self.assertTrue(res.observed_value)

        # 選択操作後に実値が異なる場合はFAILEDになることを検証する
        def tamper_select(selector: str, value: str) -> Tuple[bool, Optional[str]]:
            self.browser_drv.dom_values[selector] = "different_value"
            return True, None

        self.browser_drv.select_option = tamper_select  # type: ignore[method-assign]
        sel_step = MacroStep(
            step_id="s_sel",
            command=CommandType.BROWSER_SELECT.value,
            params={"selector": "#role", "value": "admin"},
            ir_node_id="n2",
            source_event_ids=["e2"],
        )
        sel_res = self.browser_adapter.execute(sel_step)
        self.assertEqual(sel_res.status, ExecutionStatus.FAILED)
        self.assertEqual(sel_res.requested_params["value"], "admin")
        self.assertEqual(sel_res.observed_value, "different_value")

    def test_req_test_005_and_006_physical_and_excel_verification(self) -> None:
        self.phys_drv.ok = False
        key_step = MacroStep(
            step_id="s_key",
            command=CommandType.PHYSICAL_KEY.value,
            params={"key": "Enter"},
            ir_node_id="n1",
            source_event_ids=["e1"],
        )
        self.assertEqual(self.phys_adapter.execute(key_step).status, ExecutionStatus.FAILED)

        self.excel_drv.corrupt_write = True
        xl_step = MacroStep(
            step_id="s_xl",
            command=CommandType.EXCEL_WRITE.value,
            params={"workbook": "a.xlsx", "sheet": "Sheet1", "cell": "A1", "value": 42},
            ir_node_id="n2",
            source_event_ids=["e2"],
        )
        xl_res = self.excel_adapter.execute(xl_step)
        self.assertEqual(xl_res.status, ExecutionStatus.FAILED)
        self.assertEqual(xl_res.error_code, "EXCEL_VERIFY_FAILED")

    def test_req_test_007_and_009_loop_partial_success_and_stop_isolation(self) -> None:
        opt_step = MacroStep(
            step_id="s_opt",
            command=CommandType.PHYSICAL_KEY.value,
            params={"key": "Tab"},
            ir_node_id="n1",
            source_event_ids=["e1"],
            criticality=Criticality.OPTIONAL,
            on_failure=FailurePolicy.SKIP,
            safe_to_continue_verified=True,
        )
        loop_step = MacroStep(
            step_id="s_loop",
            command=CommandType.LOOP.value,
            params={"condition_key": "always"},
            ir_node_id="n2",
            source_event_ids=["e2"],
            loop_step_ids=["s_opt"],
            max_iterations=2,
            allowed_loop_error_codes=["PHYSICAL_INPUT_ERROR"],
        )
        self.phys_drv.ok = False
        macro = ExecutableMacro(macro_id="m_loop", workflow_id="w1", steps=[opt_step, loop_step])
        runner = MacroRunner(self.handlers, self.clock)
        report = runner.run(macro, ExecutionJobContext("job_loop"))
        self.assertEqual(report.final_status, ExecutionStatus.PARTIAL_SUCCESS)

        # ジョブ単位の停止状態が他ジョブに波及しないことを検証する
        ctx_stopped = ExecutionJobContext("job_stopped")
        ctx_active = ExecutionJobContext("job_active")
        ctx_stopped.request_stop()
        self.phys_drv.ok = True
        rep_stopped = runner.run(macro, ctx_stopped)
        rep_active = runner.run(macro, ctx_active)
        self.assertEqual(rep_stopped.final_status, ExecutionStatus.STOPPED)
        self.assertEqual(rep_active.final_status, ExecutionStatus.SUCCESS)

    def test_req_test_010_and_ai_governance_generation_integrity(self) -> None:
        pipeline = MacroGenerationPipeline()
        events = [
            RecordedEvent(
                "e1",
                100,
                "input",
                "w1",
                "t1",
                "https://example.com",
                "#pw",
                "sec",
                ValueOrigin.USER_RECORDED,
            ),
            RecordedEvent(
                "e2",
                110,
                "input",
                "w1",
                "t1",
                "https://example.com",
                "#pw",
                "secret123",
                ValueOrigin.USER_RECORDED,
            ),
            RecordedEvent(
                "e3",
                120,
                "click",
                "w1",
                "t1",
                "https://example.com",
                "#login",
                None,
                ValueOrigin.USER_RECORDED,
            ),
        ]
        ir = pipeline.build_workflow_ir("wf_1", events)
        self.assertEqual(len(ir.nodes), 2)
        self.assertEqual(len(ir.transform_history), 1)
        macro = pipeline.generate_executable_macro("m_1", ir)
        self.assertEqual(len(macro.steps), 2)

        # No AI モードでのAI呼び出し遮断と External AI の明示許可強制を検証する
        no_ai = AIModeController(AIModeConfig(mode=AIMode.NO_AI))
        with self.assertRaises(PermissionError):
            no_ai.invoke_ai_suggestion({"prompt": "test"}, lambda x: x)
        with self.assertRaises(PermissionError):
            AIModeController(
                AIModeConfig(mode=AIMode.EXTERNAL_AI, endpoint_url="https://api.example.com")
            )

    def test_req_test_011_and_012_unknown_retry_guard_and_secret_redaction(self) -> None:
        call_count = 0

        def unknown_handler(step: MacroStep) -> OperationResult:
            nonlocal call_count
            call_count += 1
            return OperationResult(
                operation_id=step.step_id,
                command=step.command,
                status=ExecutionStatus.UNKNOWN,
                started_at_ms=100,
                ended_at_ms=110,
                requested_params=dict(step.params),
                executed_params=dict(step.params),
                error_code="RESULT_UNCONFIRMED",
                error_message="Cannot confirm completion",
            )

        handlers = dict(self.handlers)
        handlers[CommandType.BROWSER_CLICK.value] = unknown_handler
        step = MacroStep(
            step_id="s_unk",
            command=CommandType.BROWSER_CLICK.value,
            params={"selector": "#pay", "api_key": "sk-1234567890abcdef1234"},
            ir_node_id="n1",
            source_event_ids=["e1"],
            side_effect=SideEffectLevel.NON_IDEMPOTENT_MUTATION,
            retry_policy=RetryPolicy(max_attempts=3, require_state_probe_on_unknown=True),
        )
        macro = ExecutableMacro(macro_id="m_unk", workflow_id="w1", steps=[step])
        runner = MacroRunner(handlers, self.clock, state_probe=None)
        report = runner.run(macro, ExecutionJobContext("job_unk"))
        self.assertEqual(call_count, 1)
        self.assertEqual(report.final_status, ExecutionStatus.UNKNOWN)
        self.assertEqual(
            report.audit_logs[0]["requested_params"]["api_key"], "***REDACTED***"
        )

    def test_workflow_serialization_optimization_and_legacy_status(self) -> None:
        pipeline = MacroGenerationPipeline()
        raw_Events = pipeline.normalize_raw_events(
            [
                {
                    "event_id": "e1",
                    "timestamp_ms": 10,
                    "action_type": "scroll",
                    "window_id": "w1",
                    "tab_id": "t1",
                    "origin_url": "https://example.com",
                    "metadata": {"scroll_x": 0, "scroll_y": 100},
                },
                {
                    "event_id": "e2",
                    "timestamp_ms": 20,
                    "action_type": "scroll",
                    "window_id": "w1",
                    "tab_id": "t1",
                    "origin_url": "https://example.com",
                    "metadata": {"scroll_x": 0, "scroll_y": 350},
                },
                {
                    "event_id": "e3",
                    "timestamp_ms": 30,
                    "action_type": "physical_drag",
                    "window_id": "w1",
                    "tab_id": "t1",
                    "origin_url": "https://example.com",
                    "metadata": {"start_x": 10, "start_y": 10, "end_x": 50, "end_y": 50},
                },
            ]
        )
        ir = pipeline.build_workflow_ir("wf_opt", raw_Events)
        opt_ir = pipeline.optimize_workflow_ir(ir)
        self.assertEqual(len(opt_ir.nodes), 2)
        self.assertEqual(opt_ir.nodes[0].params["scroll_y"], 350)

        restored_ir = WorkflowIR.from_dict(opt_ir.to_dict())
        macro = pipeline.generate_executable_macro("m_opt", restored_ir)
        restored_macro = MacroValidator.parse_and_validate_json(
            macro.to_dict(), set(self.handlers.keys()) | {CommandType.LOOP.value}, restored_ir
        )
        self.assertEqual(len(restored_macro.steps), 2)

        self.assertEqual(
            LegacyStatusMapper.from_legacy("completed", post_condition_verified=False),
            ExecutionStatus.UNKNOWN,
        )
        self.assertEqual(
            LegacyStatusMapper.from_legacy("aborted", post_condition_verified=True),
            ExecutionStatus.STOPPED,
        )


if __name__ == "__main__":

    def test_workflow_serialization_optimization_and_legacy_status(self) -> None:
        pipeline = MacroGenerationPipeline()
        raw_events = pipeline.normalize_raw_events(
            [
                {
                    "event_id": "e1",
                    "timestamp_ms": 10,
                    "action_type": "scroll",
                    "window_id": "w1",
                    "tab_id": "t1",
                    "origin_url": "https://example.com",
                    "metadata": {"scroll_x": 0, "scroll_y": 100},
                },
                {
                    "event_id": "e2",
                    "timestamp_ms": 20,
                    "action_type": "scroll",
                    "window_id": "w1",
                    "tab_id": "t1",
                    "origin_url": "https://example.com",
                    "metadata": {"scroll_x": 0, "scroll_y": 350},
                },
                {
                    "event_id": "e3",
                    "timestamp_ms": 30,
                    "action_type": "physical_drag",
                    "window_id": "w1",
                    "tab_id": "t1",
                    "origin_url": "https://example.com",
                    "metadata": {"start_x": 10, "start_y": 10, "end_x": 50, "end_y": 50},
                },
            ]
        )
        ir = pipeline.build_workflow_ir("wf_opt", raw_events)
        opt_ir = pipeline.optimize_workflow_ir(ir)
        self.assertEqual(len(opt_ir.nodes), 2)
        self.assertEqual(opt_ir.nodes[0].params["scroll_y"], 350)

        restored_ir = WorkflowIR.from_dict(opt_ir.to_dict())
        macro = pipeline.generate_executable_macro("m_opt", restored_ir)
        restored_macro = MacroValidator.parse_and_validate_json(
            macro.to_dict(),
            set(self.handlers.keys()) | {CommandType.LOOP.value},
            restored_ir,
        )
        self.assertEqual(len(restored_macro.steps), 2)

        self.assertEqual(
            LegacyStatusMapper.from_legacy("completed", post_condition_verified=False),
            ExecutionStatus.UNKNOWN,
        )
        self.assertEqual(
            LegacyStatusMapper.from_legacy("aborted", post_condition_verified=True),
            ExecutionStatus.STOPPED,
        )


if __name__ == "__main__":
    unittest.main()
