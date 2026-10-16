"""Module: @role: 画面認識・ブラウザ・物理入力・スクリプト・Excel操作の実行アダプターおよび事後条件検証"""

from typing import Any, Callable, Dict, Optional, Protocol, Tuple
from ai_macro.models import (
    CommandType,
    ExecutionStatus,
    MacroStep,
    OperationResult,
    PostConditionResult,
)


class BrowserDriver(Protocol):
    def check_focus_and_target(self, selector: str, expected_origin: Optional[str]) -> bool: ...
    def click(self, selector: str) -> Tuple[bool, Optional[str]]: ...
    def click_coordinates(self, x: int, y: int, target_signature: str) -> Tuple[bool, Optional[str]]: ...
    def input_text(self, selector: str, text: str) -> Tuple[bool, Optional[str]]: ...
    def get_element_value(self, selector: str) -> Tuple[bool, Any]: ...
    def select_option(self, selector: str, value: str) -> Tuple[bool, Optional[str]]: ...
    def get_checkbox_state(self, selector: str) -> Tuple[bool, Optional[bool]]: ...
    def set_checkbox_state(self, selector: str, checked: bool) -> Tuple[bool, Optional[str]]: ...
    def wait_for_element(self, selector: str, wait_ms: int) -> Tuple[bool, Optional[str]]: ...
    def scroll_to(self, scroll_x: int, scroll_y: int) -> Tuple[bool, Optional[int], Optional[int], Optional[str]]: ...


class ScreenRecognizer(Protocol):
    def match_template(self, template_id: str, timeout_ms: int) -> Dict[str, Any]: ...


class PhysicalInputDriver(Protocol):
    def click_at(self, x: int, y: int, button: str) -> Tuple[bool, Optional[str]]: ...
    def drag_from_to(
        self, start_x: int, start_y: int, end_x: int, end_y: int, button: str
    ) -> Tuple[bool, Optional[str]]: ...
    def press_key(self, key: str) -> Tuple[bool, Optional[str]]: ...


class ExcelDriver(Protocol):
    def is_supported(self) -> bool: ...
    def target_exists(self, workbook: str, sheet: str, cell: str) -> Tuple[bool, Optional[str]]: ...
    def write_cell(self, workbook: str, sheet: str, cell: str, value: Any) -> Tuple[bool, Optional[str]]: ...
    def read_cell(self, workbook: str, sheet: str, cell: str) -> Tuple[bool, Any]: ...


class ScriptRunner(Protocol):
    def run_script(self, script_id: str, entrypoint: str) -> Dict[str, Any]: ...


def _build_post_condition_result(
    can_observe: bool, expected: Any, observed: Any, label: str
) -> PostConditionResult:
    if not can_observe:
        return PostConditionResult(
            verified=False,
            status=ExecutionStatus.UNKNOWN,
            expected_value=expected,
            observed_value=None,
            reason=f"REQ-RES-003: Could not observe post-condition for {label}",
        )
    if observed == expected:
        return PostConditionResult(
            verified=True,
            status=ExecutionStatus.SUCCESS,
            expected_value=expected,
            observed_value=observed,
            reason=f"{label} verified equal to expected value",
        )
    return PostConditionResult(
        verified=False,
        status=ExecutionStatus.FAILED,
        expected_value=expected,
        observed_value=observed,
        reason=f"{label} mismatch: expected={expected!r}, observed={observed!r}",
    )


class ScreenRecognitionAdapter:
    def __init__(
        self,
        recognizer: ScreenRecognizer,
        input_driver: PhysicalInputDriver,
        clock_ms: Callable[[], int],
    ) -> None:
        self._recognizer = recognizer
        self._input = input_driver
        self._clock = clock_ms
        self.last_match_succeeded: bool = False

    def execute(self, step: MacroStep) -> OperationResult:
        start = self._clock()
        template_id = str(step.params["template_id"])
        x = int(step.params["x"])
        y = int(step.params["y"])
        match_res = self._recognizer.match_template(template_id, step.timeout_ms)
        outcome = match_res.get("status", "failed")

        if outcome in {"timeout", "cancelled", "image_load_error", "window_invalid", "mismatch"}:
            self.last_match_succeeded = False
            mapped_status = (
                ExecutionStatus.TIMEOUT
                if outcome == "timeout"
                else ExecutionStatus.CANCELLED
                if outcome == "cancelled"
                else ExecutionStatus.FAILED
            )
            # 画面照合失敗時は後続のクリック操作を絶対に実行しない
            return OperationResult(
                operation_id=step.step_id,
                command=step.command,
                status=mapped_status,
                started_at_ms=start,
                ended_at_ms=self._clock(),
                requested_params=dict(step.params),
                executed_params={"template_id": template_id, "click_executed": False},
                observed_value=match_res,
                error_code=f"SCREEN_{outcome.upper()}",
                error_message=match_res.get("reason", f"Screen recognition failed: {outcome}"),
                criticality=step.criticality,
            )

        self.last_match_succeeded = True
        ok, err = self._input.click_at(x, y, "left")
        end = self._clock()
        if not ok:
            return OperationResult(
                operation_id=step.step_id,
                command=step.command,
                status=ExecutionStatus.FAILED,
                started_at_ms=start,
                ended_at_ms=end,
                requested_params=dict(step.params),
                executed_params={"template_id": template_id, "x": x, "y": y},
                observed_value={"match": match_res, "click_ok": False},
                error_code="PHYSICAL_CLICK_FAILED",
                error_message=err or "Click after screen match failed",
                criticality=step.criticality,
            )

        return OperationResult(
            operation_id=step.step_id,
            command=step.command,
            status=ExecutionStatus.SUCCESS,
            started_at_ms=start,
            ended_at_ms=end,
            requested_params=dict(step.params),
            executed_params={"template_id": template_id, "x": x, "y": y},
            observed_value={"matched": True, "clicked_at": (x, y)},
            post_condition_result=PostConditionResult(
                verified=True,
                status=ExecutionStatus.SUCCESS,
                expected_value=True,
                observed_value=True,
                reason="Screen matched and click executed",
            ),
            criticality=step.criticality,
        )


class BrowserAdapter:
    def __init__(self, driver: BrowserDriver, clock_ms: Callable[[], int]) -> None:
        self._driver = driver
        self._clock = clock_ms

    def execute(self, step: MacroStep) -> OperationResult:
        start = self._clock()
        cmd = step.command
        p = step.params

        if cmd == CommandType.BROWSER_CLICK.value:
            return self._exec_click(step, start)
        if cmd == CommandType.BROWSER_INPUT.value:
            return self._exec_input(step, start)
        if cmd == CommandType.BROWSER_SELECT.value:
            return self._exec_select(step, start)
        if cmd == CommandType.BROWSER_CHECKBOX.value:
            return self._exec_checkbox(step, start)
        if cmd == CommandType.BROWSER_WAIT.value:
            ok, err = self._driver.wait_for_element(str(p["selector"]), int(p["wait_ms"]))
            return OperationResult(
                operation_id=step.step_id,
                command=cmd,
                status=ExecutionStatus.SUCCESS if ok else ExecutionStatus.TIMEOUT,
                started_at_ms=start,
                ended_at_ms=self._clock(),
                requested_params=dict(p),
                executed_params=dict(p),
                observed_value={"element_present": ok},
                error_code=None if ok else "BROWSER_WAIT_TIMEOUT",
                error_message=err,
                criticality=step.criticality,
            )
        if cmd == CommandType.BROWSER_SCROLL.value:
            sx, sy = int(p["scroll_x"]), int(p["scroll_y"])
            ok, obs_x, obs_y, err = self._driver.scroll_to(sx, sy)
            if not ok:
                return OperationResult(
                    operation_id=step.step_id,
                    command=cmd,
                    status=ExecutionStatus.FAILED,
                    started_at_ms=start,
                    ended_at_ms=self._clock(),
                    requested_params=dict(p),
                    executed_params=dict(p),
                    observed_value={"scroll_x": obs_x, "scroll_y": obs_y},
                    error_code="BROWSER_SCROLL_FAILED",
                    error_message=err or "Scroll failed",
                    criticality=step.criticality,
                )
            pc = _build_post_condition_result(
                obs_x is not None and obs_y is not None,
                (sx, sy),
                (obs_x, obs_y) if obs_x is not None else None,
                "scroll_position",
            )
            return OperationResult(
                operation_id=step.step_id,
                command=cmd,
                status=pc.status,
                started_at_ms=start,
                ended_at_ms=self._clock(),
                requested_params=dict(p),
                executed_params=dict(p),
                observed_value={"scroll_x": obs_x, "scroll_y": obs_y},
                error_code=None if pc.verified else "SCROLL_POST_CONDITION_FAILED",
                error_message=None if pc.verified else pc.reason,
                post_condition_result=pc,
                criticality=step.criticality,
            )

        raise ValueError(f"Unsupported browser command: {cmd}")

    def _exec_click(self, step: MacroStep, start: int) -> OperationResult:
        p = step.params
        selector = str(p["selector"])
        ok, err = self._driver.click(selector)
        if ok:
            return OperationResult(
                operation_id=step.step_id,
                command=step.command,
                status=ExecutionStatus.SUCCESS,
                started_at_ms=start,
                ended_at_ms=self._clock(),
                requested_params=dict(p),
                executed_params={"selector": selector, "fallback_used": False},
                observed_value={"clicked_selector": selector},
                criticality=step.criticality,
            )
        # 意味的同等性が保証された代替座標のみフォールバックを許可する
        fb = p.get("fallback_coordinates")
        if fb and p.get("allow_semantic_fallback") and p.get("target_signature"):
            fb_ok, fb_err = self._driver.click_coordinates(
                int(fb["x"]), int(fb["y"]), str(p["target_signature"])
            )
            if fb_ok:
                return OperationResult(
                    operation_id=step.step_id,
                    command=step.command,
                    status=ExecutionStatus.SUCCESS,
                    started_at_ms=start,
                    ended_at_ms=self._clock(),
                    requested_params=dict(p),
                    executed_params={"fallback_coordinates": fb, "fallback_used": True},
                    observed_value={"clicked_fallback": fb},
                    criticality=step.criticality,
                )
            err = fb_err or err
        return OperationResult(
            operation_id=step.step_id,
            command=step.command,
            status=ExecutionStatus.FAILED,
            started_at_ms=start,
            ended_at_ms=self._clock(),
            requested_params=dict(p),
            executed_params={"selector": selector},
            observed_value=None,
            error_code="BROWSER_CLICK_FAILED",
            error_message=err or "Selector click failed and no equivalent fallback succeeded",
            criticality=step.criticality,
        )

    def _exec_input(self, step: MacroStep, start: int) -> OperationResult:
        p = step.params
        selector = str(p["selector"])
        text = str(p["text"])
        if not self._driver.check_focus_and_target(selector, p.get("context_origin")):
            return OperationResult(
                operation_id=step.step_id,
                command=step.command,
                status=ExecutionStatus.FAILED,
                started_at_ms=start,
                ended_at_ms=self._clock(),
                requested_params=dict(p),
                executed_params={"selector": selector},
                observed_value=None,
                error_code="INPUT_TARGET_UNVERIFIED",
                error_message="REQ-ACT-004: Target element or focus could not be verified before input",
                criticality=step.criticality,
            )
        ok, err = self._driver.input_text(selector, text)
        if not ok:
            return OperationResult(
                operation_id=step.step_id,
                command=step.command,
                status=ExecutionStatus.FAILED,
                started_at_ms=start,
                ended_at_ms=self._clock(),
                requested_params=dict(p),
                executed_params=dict(p),
                observed_value=None,
                error_code="BROWSER_INPUT_FAILED",
                error_message=err or "Input failed",
                criticality=step.criticality,
            )
        can_obs, obs_val = self._driver.get_element_value(selector)
        pc = _build_post_condition_result(can_obs, text, obs_val, "dom_input_value")
        return OperationResult(
            operation_id=step.step_id,
            command=step.command,
            status=pc.status,
            started_at_ms=start,
            ended_at_ms=self._clock(),
            requested_params=dict(p),
            executed_params=dict(p),
            observed_value=obs_val,
            error_code=None if pc.verified else "INPUT_POST_CONDITION_MISMATCH",
            error_message=None if pc.verified else pc.reason,
            post_condition_result=pc,
            criticality=step.criticality,
        )

    def _exec_select(self, step: MacroStep, start: int) -> OperationResult:
        p = step.params
        selector = str(p["selector"])
        val = str(p["value"])
        ok, err = self._driver.select_option(selector, val)
        if not ok:
            return OperationResult(
                operation_id=step.step_id,
                command=step.command,
                status=ExecutionStatus.FAILED,
                started_at_ms=start,
                ended_at_ms=self._clock(),
                requested_params=dict(p),
                executed_params=dict(p),
                observed_value=None,
                error_code="BROWSER_SELECT_FAILED",
                error_message=err or "Select option failed",
                criticality=step.criticality,
            )
        can_obs, obs_val = self._driver.get_element_value(selector)
        pc = _build_post_condition_result(can_obs, val, obs_val, "selected_option_value")
        return OperationResult(
            operation_id=step.step_id,
            command=step.command,
            status=pc.status,
            started_at_ms=start,
            ended_at_ms=self._clock(),
            requested_params=dict(p),
            executed_params=dict(p),
            observed_value=obs_val,
            error_code=None if pc.verified else "SELECT_VALUE_MISMATCH",
            error_message=None if pc.verified else pc.reason,
            post_condition_result=pc,
            criticality=step.criticality,
        )

    def _exec_checkbox(self, step: MacroStep, start: int) -> OperationResult:
        p = step.params
        selector = str(p["selector"])
        expected_checked = bool(p["checked"])
        pre_ok, pre_state = self._driver.get_checkbox_state(selector)
        if not pre_ok:
            return OperationResult(
                operation_id=step.step_id,
                command=step.command,
                status=ExecutionStatus.FAILED,
                started_at_ms=start,
                ended_at_ms=self._clock(),
                requested_params=dict(p),
                executed_params=dict(p),
                observed_value=None,
                error_code="CHECKBOX_PRE_STATE_UNAVAILABLE",
                error_message="REQ-ACT-006: Cannot verify checkbox state before operation",
                criticality=step.criticality,
            )
        ok, err = self._driver.set_checkbox_state(selector, expected_checked)
        if not ok:
            return OperationResult(
                operation_id=step.step_id,
                command=step.command,
                status=ExecutionStatus.FAILED,
                started_at_ms=start,
                ended_at_ms=self._clock(),
                requested_params=dict(p),
                executed_params={"selector": selector, "pre_state": pre_state},
                observed_value=pre_state,
                error_code="CHECKBOX_ACTION_FAILED",
                error_message=err or "Checkbox action failed",
                criticality=step.criticality,
            )
        post_ok, post_state = self._driver.get_checkbox_state(selector)
        pc = _build_post_condition_result(post_ok, expected_checked, post_state, "checkbox_state")
        return OperationResult(
            operation_id=step.step_id,
            command=step.command,
            status=pc.status,
            started_at_ms=start,
            ended_at_ms=self._clock(),
            requested_params=dict(p),
            executed_params={"selector": selector, "pre_state": pre_state},
            observed_value=post_state,
            error_code=None if pc.verified else "CHECKBOX_STATE_MISMATCH",
            error_message=None if pc.verified else pc.reason,
            post_condition_result=pc,
            criticality=step.criticality,
        )


class PhysicalInputAdapter:
    def __init__(
        self,
        driver: PhysicalInputDriver,
        screen_adapter: ScreenRecognitionAdapter,
        clock_ms: Callable[[], int],
    ) -> None:
        self._driver = driver
        self._screen_adapter = screen_adapter
        self._clock = clock_ms

    def execute(self, step: MacroStep) -> OperationResult:
        start = self._clock()
        p = step.params
        if step.command in {CommandType.PHYSICAL_MOUSE.value, CommandType.PHYSICAL_DRAG.value}:
            if p.get("require_screen_match") and not self._screen_adapter.last_match_succeeded:
                return OperationResult(
                    operation_id=step.step_id,
                    command=step.command,
                    status=ExecutionStatus.FAILED,
                    started_at_ms=start,
                    ended_at_ms=self._clock(),
                    requested_params=dict(p),
                    executed_params={"pointer_action_executed": False},
                    observed_value=None,
                    error_code="PRECONDITION_SCREEN_MATCH_FAILED",
                    error_message="REQ-ACT-002: Screen match required before physical pointer action",
                    criticality=step.criticality,
                )
            if step.command == CommandType.PHYSICAL_MOUSE.value:
                ok, err = self._driver.click_at(int(p["x"]), int(p["y"]), str(p["button"]))
            else:
                ok, err = self._driver.drag_from_to(
                    int(p["start_x"]),
                    int(p["start_y"]),
                    int(p["end_x"]),
                    int(p["end_y"]),
                    str(p["button"]),
                )
        else:
            ok, err = self._driver.press_key(str(p["key"]))

        return OperationResult(
            operation_id=step.step_id,
            command=step.command,
            status=ExecutionStatus.SUCCESS if ok else ExecutionStatus.FAILED,
            started_at_ms=start,
            ended_at_ms=self._clock(),
            requested_params=dict(p),
            executed_params=dict(p),
            observed_value={"dispatched": ok},
            error_code=None if ok else "PHYSICAL_INPUT_ERROR",
            error_message=err,
            criticality=step.criticality,
        )


class ScriptAdapter:
    def __init__(self, runner: ScriptRunner, clock_ms: Callable[[], int]) -> None:
        self._runner = runner
        self._clock = clock_ms

    def execute(self, step: MacroStep) -> OperationResult:
        start = self._clock()
        p = step.params
        res = self._runner.run_script(str(p["script_id"]), str(p["entrypoint"]))
        executed = bool(res.get("executed", False))
        exit_code = res.get("exit_code", -1)
        if not executed or exit_code != 0:
            return OperationResult(
                operation_id=step.step_id,
                command=step.command,
                status=ExecutionStatus.FAILED,
                started_at_ms=start,
                ended_at_ms=self._clock(),
                requested_params=dict(p),
                executed_params=dict(p),
                observed_value=res,
                error_code="SCRIPT_EXECUTION_FAILED",
                error_message=res.get("error", "REQ-ACT-009: Script did not execute cleanly"),
                criticality=step.criticality,
            )
        return OperationResult(
            operation_id=step.step_id,
            command=step.command,
            status=ExecutionStatus.SUCCESS,
            started_at_ms=start,
            ended_at_ms=self._clock(),
            requested_params=dict(p),
            executed_params=dict(p),
            observed_value=res.get("output"),
            criticality=step.criticality,
        )


class ExcelAdapter:
    def __init__(self, driver: ExcelDriver, clock_ms: Callable[[], int]) -> None:
        self._driver = driver
        self._clock = clock_ms

    def execute(self, step: MacroStep) -> OperationResult:
        start = self._clock()
        p = step.params
        if not self._driver.is_supported():
            return OperationResult(
                operation_id=step.step_id,
                command=step.command,
                status=ExecutionStatus.FAILED,
                started_at_ms=start,
                ended_at_ms=self._clock(),
                requested_params=dict(p),
                executed_params={},
                observed_value=None,
                error_code="EXCEL_ENV_UNSUPPORTED",
                error_message="REQ-XL-001: Excel operations are not supported in this environment",
                criticality=step.criticality,
            )
        wb, sh, cell, val = str(p["workbook"]), str(p["sheet"]), str(p["cell"]), p["value"]
        exists, exist_err = self._driver.target_exists(wb, sh, cell)
        if not exists:
            return OperationResult(
                operation_id=step.step_id,
                command=step.command,
                status=ExecutionStatus.FAILED,
                started_at_ms=start,
                ended_at_ms=self._clock(),
                requested_params=dict(p),
                executed_params={},
                observed_value=None,
                error_code="EXCEL_TARGET_MISSING",
                error_message=exist_err or f"REQ-XL-002: Missing target {wb}!{sh}!{cell}",
                criticality=step.criticality,
            )
        ok, write_err = self._driver.write_cell(wb, sh, cell, val)
        if not ok:
            return OperationResult(
                operation_id=step.step_id,
                command=step.command,
                status=ExecutionStatus.FAILED,
                started_at_ms=start,
                ended_at_ms=self._clock(),
                requested_params=dict(p),
                executed_params=dict(p),
                observed_value=None,
                error_code="EXCEL_WRITE_FAILED",
                error_message=write_err or "Excel write failed",
                criticality=step.criticality,
            )
        read_ok, obs_val = self._driver.read_cell(wb, sh, cell)
        pc = _build_post_condition_result(read_ok, val, obs_val, f"excel_cell({wb}!{sh}!{cell})")
        return OperationResult(
            operation_id=step.step_id,
            command=step.command,
            status=pc.status,
            started_at_ms=start,
            ended_at_ms=self._clock(),
            requested_params=dict(p),
            executed_params=dict(p),
            observed_value=obs_val,
            error_code=None if pc.verified else "EXCEL_VERIFY_FAILED",
            error_message=None if pc.verified else pc.reason,
            post_condition_result=pc,
            criticality=step.criticality,
        )
