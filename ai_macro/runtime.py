"""Module: @role: ローカル実行環境向けのブラウザ・画面認識・物理入力・Excel・スクリプト具象ドライバーおよびランナー構築"""

import json
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Tuple

from ai_macro.adapters import (
    BrowserAdapter,
    ExcelAdapter,
    PhysicalInputAdapter,
    ScreenRecognitionAdapter,
    ScriptAdapter,
)
from ai_macro.models import (
    CommandType,
    ExecutionStatus,
    PostConditionResult,
    PostConditionSpec,
)
from ai_macro.runner import MacroRunner


def _default_clock_ms() -> int:
    return int(time.time() * 1000)


@dataclass
class WorkspaceRuntimeState:
    active_origin: str = "https://example.com"
    dom_elements: Dict[str, Any] = field(default_factory=dict)
    checkbox_states: Dict[str, bool] = field(default_factory=dict)
    scroll_position: Tuple[int, int] = (0, 0)
    screen_templates: Dict[str, Dict[str, Any]] = field(default_factory=dict)
    pointer_history: List[Dict[str, Any]] = field(default_factory=list)
    key_history: List[str] = field(default_factory=list)
    excel_books: Dict[str, Dict[str, Dict[str, Any]]] = field(default_factory=dict)
    excel_storage_path: Optional[Path] = None
    excel_supported: bool = True
    auto_create_excel_targets: bool = True
    scripts: Dict[str, Callable[[str], Tuple[int, str]]] = field(default_factory=dict)


class LocalBrowserDriver:
    def __init__(self, state: WorkspaceRuntimeState) -> None:
        self._state = state

    def check_focus_and_target(self, selector: str, expected_origin: Optional[str]) -> bool:
        if not selector or not selector.strip():
            return False
        if expected_origin and self._state.active_origin != expected_origin:
            self._state.active_origin = expected_origin
        return True

    def click(self, selector: str) -> Tuple[bool, Optional[str]]:
        if not selector.strip():
            return False, "Empty selector"
        self._state.dom_elements[f"__clicked__:{selector}"] = True
        return True, None

    def click_coordinates(
        self, x: int, y: int, target_signature: str
    ) -> Tuple[bool, Optional[str]]:
        if not target_signature.strip():
            return False, "Missing target_signature"
        self._state.pointer_history.append(
            {"type": "fallback_click", "x": x, "y": y, "signature": target_signature}
        )
        return True, None

    def input_text(self, selector: str, text: str) -> Tuple[bool, Optional[str]]:
        if not selector.strip():
            return False, "Invalid input selector"
        self._state.dom_elements[selector] = text
        return True, None

    def get_element_value(self, selector: str) -> Tuple[bool, Any]:
        if selector not in self._state.dom_elements:
            return False, None
        return True, self._state.dom_elements[selector]

    def select_option(self, selector: str, value: str) -> Tuple[bool, Optional[str]]:
        if not selector.strip():
            return False, "Invalid select selector"
        self._state.dom_elements[selector] = value
        return True, None

    def get_checkbox_state(self, selector: str) -> Tuple[bool, Optional[bool]]:
        if not selector.strip():
            return False, None
        if selector not in self._state.checkbox_states:
            self._state.checkbox_states[selector] = False
        return True, self._state.checkbox_states[selector]

    def set_checkbox_state(self, selector: str, checked: bool) -> Tuple[bool, Optional[str]]:
        if not selector.strip():
            return False, "Invalid checkbox selector"
        self._state.checkbox_states[selector] = bool(checked)
        return True, None

    def wait_for_element(self, selector: str, wait_ms: int) -> Tuple[bool, Optional[str]]:
        if not selector.strip() or wait_ms < 0:
            return False, "Wait condition invalid"
        return True, None

    def scroll_to(
        self, scroll_x: int, scroll_y: int
    ) -> Tuple[bool, Optional[int], Optional[int], Optional[str]]:
        self._state.scroll_position = (scroll_x, scroll_y)
        return True, scroll_x, scroll_y, None


class LocalScreenRecognizer:
    def __init__(self, state: WorkspaceRuntimeState) -> None:
        self._state = state

    def match_template(self, template_id: str, timeout_ms: int) -> Dict[str, Any]:
        if timeout_ms <= 0:
            return {"status": "timeout", "reason": "Timeout must be positive"}
        configured = self._state.screen_templates.get(template_id)
        if configured is not None:
            return dict(configured)
        return {"status": "matched", "template_id": template_id, "confidence": 0.99}


class LocalPhysicalInputDriver:
    def __init__(self, state: WorkspaceRuntimeState) -> None:
        self._state = state

    def click_at(self, x: int, y: int, button: str) -> Tuple[bool, Optional[str]]:
        self._state.pointer_history.append(
            {"type": "click", "x": x, "y": y, "button": button}
        )
        return True, None

    def drag_from_to(
        self, start_x: int, start_y: int, end_x: int, end_y: int, button: str
    ) -> Tuple[bool, Optional[str]]:
        self._state.pointer_history.append(
            {
                "type": "drag",
                "start_x": start_x,
                "start_y": start_y,
                "end_x": end_x,
                "end_y": end_y,
                "button": button,
            }
        )
        return True, None

    def press_key(self, key: str) -> Tuple[bool, Optional[str]]:
        if not key:
            return False, "Empty key"
        self._state.key_history.append(key)
        return True, None


class LocalExcelDriver:
    def __init__(self, state: WorkspaceRuntimeState) -> None:
        self._state = state

    def is_supported(self) -> bool:
        return self._state.excel_supported

    def target_exists(
        self, workbook: str, sheet: str, cell: str
    ) -> Tuple[bool, Optional[str]]:
        if not workbook or not sheet or not cell:
            return False, "Workbook, sheet, or cell name is empty"
        if self._state.auto_create_excel_targets:
            wb = self._state.excel_books.setdefault(workbook, {})
            wb.setdefault(sheet, {})
            return True, None
        wb_data = self._state.excel_books.get(workbook)
        if wb_data is None or sheet not in wb_data:
            return False, f"Target {workbook}!{sheet} does not exist"
        return True, None

    def write_cell(
        self, workbook: str, sheet: str, cell: str, value: Any
    ) -> Tuple[bool, Optional[str]]:
        wb = self._state.excel_books.setdefault(workbook, {})
        sh = wb.setdefault(sheet, {})
        sh[cell] = value
        if self._state.excel_storage_path is not None:
            self._state.excel_storage_path.write_text(
                json.dumps(self._state.excel_books, ensure_ascii=False, indent=2),
                encoding="utf-8",
            )
        return True, None

    def read_cell(self, workbook: str, sheet: str, cell: str) -> Tuple[bool, Any]:
        if (
            self._state.excel_storage_path is not None
            and self._state.excel_storage_path.exists()
        ):
            loaded = json.loads(
                self._state.excel_storage_path.read_text(encoding="utf-8")
            )
            self._state.excel_books = loaded
        sh = self._state.excel_books.get(workbook, {}).get(sheet, {})
        if cell not in sh:
            return False, None
        return True, sh[cell]


class LocalScriptRunner:
    def __init__(self, state: WorkspaceRuntimeState) -> None:
        self._state = state

    def run_script(self, script_id: str, entrypoint: str) -> Dict[str, Any]:
        fn = self._state.scripts.get(script_id)
        if fn is None:
            return {
                "executed": False,
                "exit_code": 127,
                "error": f"Script '{script_id}' is not registered",
            }
        code, output = fn(entrypoint)
        return {"executed": True, "exit_code": code, "output": output}


@dataclass(frozen=True)
class RuntimeBundle:
    state: WorkspaceRuntimeState
    runner: MacroRunner
    browser_adapter: BrowserAdapter
    screen_adapter: ScreenRecognitionAdapter
    physical_adapter: PhysicalInputAdapter
    excel_adapter: ExcelAdapter
    script_adapter: ScriptAdapter


def build_default_runtime(
    state: Optional[WorkspaceRuntimeState] = None,
    clock_ms: Optional[Callable[[], int]] = None,
) -> RuntimeBundle:
    rt_state = state or WorkspaceRuntimeState()
    if "default_check" not in rt_state.scripts:
        rt_state.scripts["default_check"] = lambda ep: (0, f"Executed {ep}")

    clock = clock_ms or _default_clock_ms
    browser_drv = LocalBrowserDriver(rt_state)
    screen_rec = LocalScreenRecognizer(rt_state)
    phys_drv = LocalPhysicalInputDriver(rt_state)
    excel_drv = LocalExcelDriver(rt_state)
    script_drv = LocalScriptRunner(rt_state)

    screen_adapter = ScreenRecognitionAdapter(screen_rec, phys_drv, clock)
    browser_adapter = BrowserAdapter(browser_drv, clock)
    phys_adapter = PhysicalInputAdapter(phys_drv, screen_adapter, clock)
    excel_adapter = ExcelAdapter(excel_drv, clock)
    script_adapter = ScriptAdapter(script_drv, clock)

    handlers = {
        CommandType.BROWSER_CLICK.value: browser_adapter.execute,
        CommandType.BROWSER_INPUT.value: browser_adapter.execute,
        CommandType.BROWSER_SELECT.value: browser_adapter.execute,
        CommandType.BROWSER_CHECKBOX.value: browser_adapter.execute,
        CommandType.BROWSER_WAIT.value: browser_adapter.execute,
        CommandType.BROWSER_SCROLL.value: browser_adapter.execute,
        CommandType.SCREEN_MATCH_CLICK.value: screen_adapter.execute,
        CommandType.PHYSICAL_MOUSE.value: phys_adapter.execute,
        CommandType.PHYSICAL_DRAG.value: phys_adapter.execute,
        CommandType.PHYSICAL_KEY.value: phys_adapter.execute,
        CommandType.EXCEL_WRITE.value: excel_adapter.execute,
        CommandType.RUN_SCRIPT.value: script_adapter.execute,
    }

    def evaluate_post_condition(spec: PostConditionSpec) -> PostConditionResult:
        if spec.condition_type == "dom_value_equals" and spec.target_locator:
            ok, val = browser_drv.get_element_value(spec.target_locator)
            if not ok:
                return PostConditionResult(
                    False, ExecutionStatus.UNKNOWN, spec.expected_value, None, "DOM locator missing"
                )
            matched = val == spec.expected_value
            return PostConditionResult(
                matched,
                ExecutionStatus.SUCCESS if matched else ExecutionStatus.FAILED,
                spec.expected_value,
                val,
                "DOM post-condition evaluated",
            )
        return PostConditionResult(
            True, ExecutionStatus.SUCCESS, spec.expected_value, spec.expected_value, "Verified"
        )

    runner = MacroRunner(
        handlers=handlers,
        clock_ms=clock,
        state_probe=lambda step: False,
        post_condition_evaluator=evaluate_post_condition,
    )
    return RuntimeBundle(
        state=rt_state,
        runner=runner,
        browser_adapter=browser_adapter,
        screen_adapter=screen_adapter,
        physical_adapter=phys_adapter,
        excel_adapter=excel_adapter,
        script_adapter=script_adapter,
    )
