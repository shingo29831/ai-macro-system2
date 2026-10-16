"""Module: @role: AIモード管理・イベント記録・IR生成・事前検証・マクロ実行・監査ログ閲覧を行うデスクトップGUI"""

import json
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, scrolledtext, ttk
from typing import Any, Dict, List, Optional

from ai_macro.cli import get_sample_raw_events
from ai_macro.generator import AIModeConfig, AIModeController, MacroGenerationPipeline
from ai_macro.models import AIMode, ExecutableMacro, WorkflowIR
from ai_macro.runner import ExecutionJobContext
from ai_macro.runtime import WorkspaceRuntimeState, build_default_runtime
from ai_macro.validator import MacroValidationError, MacroValidator


class AIMacroGUIApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("AI Macro System - 高信頼マクロ生成・検証・実行コンソール")
        self.root.geometry("1180x760")

        self._raw_events: List[Dict[str, Any]] = []
        self._current_ir: Optional[WorkflowIR] = None
        self._current_macro: Optional[ExecutableMacro] = None
        self._active_job: Optional[ExecutionJobContext] = None
        self._ai_controller = AIModeController(AIModeConfig(mode=AIMode.NO_AI))
        self._runtime_bundle = build_default_runtime(WorkspaceRuntimeState())

        self._build_ui()
        self._load_sample_events()

    def _build_ui(self) -> None:
        top_frame = ttk.LabelFrame(
            self.root, text="AIモード・実行権限ガバナンス (REQ-AI-001〜005)", padding=8
        )
        top_frame.pack(fill=tk.X, padx=10, pady=6)

        ttk.Label(top_frame, text="AI Mode:").pack(side=tk.LEFT, padx=4)
        self._ai_mode_var = tk.StringVar(value=AIMode.NO_AI.value)
        mode_combo = ttk.Combobox(
            top_frame,
            textvariable=self._ai_mode_var,
            values=[m.value for m in AIMode],
            state="readonly",
            width=16,
        )
        mode_combo.pack(side=tk.LEFT, padx=4)

        ttk.Label(top_frame, text="Endpoint URL:").pack(side=tk.LEFT, padx=4)
        self._endpoint_var = tk.StringVar(value="http://127.0.0.1:11434")
        ttk.Entry(top_frame, textvariable=self._endpoint_var, width=28).pack(
            side=tk.LEFT, padx=4
        )

        self._consent_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            top_frame,
            text="External AI 通信を明示的に許可 (REQ-AI-003)",
            variable=self._consent_var,
        ).pack(side=tk.LEFT, padx=8)

        ttk.Button(top_frame, text="AI設定を適用", command=self._apply_ai_mode).pack(
            side=tk.LEFT, padx=6
        )
        self._ai_status_label = ttk.Label(
            top_frame, text="現在モード: no_ai (AI呼出完全遮断)", foreground="#1f6feb"
        )
        self._ai_status_label.pack(side=tk.RIGHT, padx=8)

        paned = ttk.PanedWindow(self.root, orient=tk.HORIZONTAL)
        paned.pack(fill=tk.BOTH, expand=True, padx=10, pady=4)

        left_frame = ttk.LabelFrame(
            paned, text="1. 操作イベント記録 & マクロ生成 (REQ-GEN)", padding=8
        )
        right_frame = ttk.LabelFrame(
            paned, text="2. 事前一括検証・実行制御・監査ログ (REQ-LOAD / REQ-RES / REQ-LOG)", padding=8
        )
        paned.add(left_frame, weight=1)
        paned.add(right_frame, weight=1)

        self._build_left_pane(left_frame)
        self._build_right_pane(right_frame)

    def _build_left_pane(self, parent: ttk.LabelFrame) -> None:
        form = ttk.Frame(parent)
        form.pack(fill=tk.X, pady=4)

        ttk.Label(form, text="種別:").grid(row=0, column=0, sticky=tk.W, padx=2, pady=2)
        self._act_type_var = tk.StringVar(value="input")
        ttk.Combobox(
            form,
            textvariable=self._act_type_var,
            values=["input", "click", "checkbox", "select", "scroll", "excel_write"],
            state="readonly",
            width=12,
        ).grid(row=0, column=1, sticky=tk.W, padx=2, pady=2)

        ttk.Label(form, text="Selector:").grid(row=0, column=2, sticky=tk.W, padx=2, pady=2)
        self._selector_var = tk.StringVar(value="#search_box")
        ttk.Entry(form, textvariable=self._selector_var, width=18).grid(
            row=0, column=3, sticky=tk.W, padx=2, pady=2
        )

        ttk.Label(form, text="値 (Value):").grid(row=1, column=0, sticky=tk.W, padx=2, pady=2)
        self._value_var = tk.StringVar(value="AI Macro Query")
        ttk.Entry(form, textvariable=self._value_var, width=16).grid(
            row=1, column=1, sticky=tk.W, padx=2, pady=2
        )

        ttk.Label(form, text="値の由来:").grid(row=1, column=2, sticky=tk.W, padx=2, pady=2)
        self._origin_var = tk.StringVar(value="user_recorded")
        ttk.Combobox(
            form,
            textvariable=self._origin_var,
            values=["user_recorded", "ai_inferred", "snapshot_inferred"],
            state="readonly",
            width=16,
        ).grid(row=1, column=3, sticky=tk.W, padx=2, pady=2)

        self._approved_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            form, text="人間承認済 (human_approved)", variable=self._approved_var
        ).grid(row=2, column=0, columnspan=2, sticky=tk.W, padx=2, pady=2)

        self._mandatory_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            form, text="必須ステップ (mandatory)", variable=self._mandatory_var
        ).grid(row=2, column=2, columnspan=2, sticky=tk.W, padx=2, pady=2)

        btn_bar = ttk.Frame(parent)
        btn_bar.pack(fill=tk.X, pady=4)
        ttk.Button(btn_bar, text="＋ イベント追加", command=self._add_event).pack(
            side=tk.LEFT, padx=2
        )
        ttk.Button(btn_bar, text="選択削除", command=self._remove_selected_event).pack(
            side=tk.LEFT, padx=2
        )
        ttk.Button(btn_bar, text="サンプル復元", command=self._load_sample_events).pack(
            side=tk.LEFT, padx=2
        )
        ttk.Button(
            btn_bar, text="⚡ IR & 実行マクロ生成", command=self._generate_macro
        ).pack(side=tk.RIGHT, padx=2)

        cols = ("id", "type", "selector", "value", "origin", "approved", "criticality")
        self._tree = ttk.Treeview(parent, columns=cols, show="headings", height=16)
        for col, width in zip(cols, (70, 80, 110, 110, 100, 60, 75)):
            self._tree.heading(col, text=col)
            self._tree.column(col, width=width, anchor=tk.W)
        self._tree.pack(fill=tk.BOTH, expand=True, pady=4)

    def _build_right_pane(self, parent: ttk.LabelFrame) -> None:
        ctrl = ttk.Frame(parent)
        ctrl.pack(fill=tk.X, pady=4)

        ttk.Button(ctrl, text="✔ 実行前検証", command=self._validate_current_macro).pack(
            side=tk.LEFT, padx=3
        )
        ttk.Button(ctrl, text="▶ マクロ実行", command=self._run_current_macro).pack(
            side=tk.LEFT, padx=3
        )
        ttk.Button(ctrl, text="■ 停止要求 (Stop)", command=self._request_stop).pack(
            side=tk.LEFT, padx=3
        )
        ttk.Button(ctrl, text="✖ キャンセル", command=self._request_cancel).pack(
            side=tk.LEFT, padx=3
        )
        ttk.Button(ctrl, text="JSON保存", command=self._save_macro_json).pack(
            side=tk.RIGHT, padx=3
        )
        ttk.Button(ctrl, text="JSON読込", command=self._load_macro_json).pack(
            side=tk.RIGHT, padx=3
        )

        status_bar = ttk.Frame(parent)
        status_bar.pack(fill=tk.X, pady=4)
        ttk.Label(status_bar, text="最終状態 (Final Status):").pack(side=tk.LEFT, padx=4)
        self._status_badge = ttk.Label(
            status_bar, text="PENDING", font=("Consolas", 11, "bold")
        )
        self._status_badge.pack(side=tk.LEFT, padx=6)

        notebook = ttk.Notebook(parent)
        notebook.pack(fill=tk.BOTH, expand=True, pady=4)

        self._macro_text = scrolledtext.ScrolledText(
            notebook, wrap=tk.NONE, font=("Consolas", 10)
        )
        self._audit_text = scrolledtext.ScrolledText(
            notebook, wrap=tk.WORD, font=("Consolas", 10)
        )
        notebook.add(self._macro_text, text="executable_macro.json / Workflow IR")
        notebook.add(self._audit_text, text="実行レポート & 監査ログ (Audit Log)")

    def _apply_ai_mode(self) -> None:
        try:
            mode = AIMode(self._ai_mode_var.get())
            cfg = AIModeConfig(
                mode=mode,
                endpoint_url=self._endpoint_var.get().strip() or None,
                explicit_external_consent=bool(self._consent_var.get()),
            )
            self._ai_controller = AIModeController(cfg)
            self._ai_status_label.config(
                text=f"現在モード: {mode.value} (検証済)", foreground="#1a7f37"
            )
        except (PermissionError, ValueError) as exc:
            self._ai_status_label.config(
                text=f"AI設定拒否: {exc}", foreground="#cf222e"
            )
            messagebox.showerror("AIモード検証エラー", str(exc))

    def _load_sample_events(self) -> None:
        self._raw_events = get_sample_raw_events()
        self._refresh_event_tree()
        self._generate_macro()

    def _refresh_event_tree(self) -> None:
        for item in self._tree.get_children():
            self._tree.delete(item)
        for ev in self._raw_events:
            approved = (
                ev.get("value_origin", "user_recorded") == "user_recorded"
                or ev.get("metadata", {}).get("human_approved", False)
            )
            self._tree.insert(
                "",
                tk.END,
                values=(
                    ev["event_id"],
                    ev["action_type"],
                    ev.get("selector") or "(meta)",
                    ev.get("value"),
                    ev.get("value_origin", "user_recorded"),
                    "Yes" if approved else "No",
                    ev.get("criticality", "mandatory"),
                ),
            )

    def _add_event(self) -> None:
        act = self._act_type_var.get()
        raw_val: Any = self._value_var.get()
        if act == "checkbox":
            raw_val = str(raw_val).lower() in {"true", "1", "yes", "checked"}
        meta: Dict[str, Any] = {"human_approved": bool(self._approved_var.get())}
        if act == "scroll":
            meta.update({"scroll_x": 0, "scroll_y": 300})
        elif act == "excel_write":
            meta.update(
                {"workbook": "daily_report.xlsx", "sheet": "Summary", "cell": "B2"}
            )

        ev = {
            "event_id": f"ev_{len(self._raw_events) + 1:04d}",
            "timestamp_ms": 1000 + len(self._raw_events) * 250,
            "action_type": act,
            "window_id": "win_main",
            "tab_id": "tab_login",
            "origin_url": "https://portal.example.com",
            "selector": self._selector_var.get().strip() or None,
            "value": raw_val,
            "value_origin": self._origin_var.get(),
            "criticality": "mandatory" if self._mandatory_var.get() else "optional",
            "metadata": meta,
        }
        self._raw_events.append(ev)
        self._refresh_event_tree()

    def _remove_selected_event(self) -> None:
        selected = self._tree.selection()
        if not selected:
            return
        selected_ids = {self._tree.item(i, "values")[0] for i in selected}
        self._raw_events = [
            e for e in self._raw_events if e["event_id"] not in selected_ids
        ]
        self._refresh_event_tree()

    def _generate_macro(self) -> None:
        try:
            pipeline = MacroGenerationPipeline(self._ai_controller)
            norm = pipeline.normalize_raw_events(self._raw_events)
            ir = pipeline.optimize_workflow_ir(
                pipeline.build_workflow_ir("wf_gui_001", norm)
            )
            macro = pipeline.generate_executable_macro(
                "macro_gui_001", ir, self._runtime_bundle.runner.registered_commands
            )
            self._current_ir = ir
            self._current_macro = macro
            self._macro_text.delete("1.0", tk.END)
            self._macro_text.insert(
                tk.END,
                json.dumps(
                    {
                        "executable_macro": macro.to_dict(),
                        "workflow_ir": ir.to_dict(),
                    },
                    ensure_ascii=False,
                    indent=2,
                ),
            )
            self._status_badge.config(text="GENERATED & VALIDATED", foreground="#1a7f37")
        except MacroValidationError as exc:
            self._status_badge.config(text="GENERATION BLOCKED", foreground="#cf222e")
            messagebox.showerror("マクロ生成・意味的整合性エラー", "\n".join(exc.errors))

    def _validate_current_macro(self) -> bool:
        try:
            content = json.loads(self._macro_text.get("1.0", tk.END))
            macro_dict = content.get("executable_macro", content)
            ir_dict = content.get("workflow_ir")
            wf_ir = WorkflowIR.from_dict(ir_dict) if isinstance(ir_dict, dict) else None
            self._current_macro = MacroValidator.parse_and_validate_json(
                macro_dict, self._runtime_bundle.runner.registered_commands, wf_ir
            )
            self._current_ir = wf_ir
            self._status_badge.config(text="VALIDATED OK", foreground="#1a7f37")
            return True
        except ( json.JSONDecodeError, MacroValidationError) as exc:
            self._status_badge.config(text="VALIDATION FAILED", foreground="#cf222e")
            messagebox.showerror("実行前一括検証エラー (REQ-LOAD)", str(exc))
            return False

    def _run_current_macro(self) -> None:
        if not self._validate_current_macro() or self._current_macro is None:
            return
        self._active_job = ExecutionJobContext(job_id="gui_job_001")
        report = self._runtime_bundle.runner.run(
            self._current_macro, self._active_job, self._current_ir
        )
        self._status_badge.config(
            text=f"{report.final_status.value.upper()} ({report.reason})"
        )
        self._audit_text.delete("1.0", tk.END)
        self._audit_text.insert(
            tk.END,
            json.dumps(
                {
                    "job_id": report.job_id,
                    "macro_id": report.macro_id,
                    "ai_mode": self._ai_controller.mode.value,
                    "final_status": report.final_status.value,
                    "reason": report.reason,
                    "audit_logs": report.audit_logs,
                },
                ensure_ascii=False,
                indent=2,
            ),
        )

    def _request_stop(self) -> None:
        if self._active_job is not None:
            self._active_job.request_stop()
        self._status_badge.config(text="STOPPED (User Request)", foreground="#9a6700")

    def _request_cancel(self) -> None:
        if self._active_job is not None:
            self._active_job.request_cancel()
        self._status_badge.config(text="CANCELLED", foreground="#cf222e")

    def _save_macro_json(self) -> None:
        file_path = filedialog.asksaveasfilename(
            defaultextension=".json",
            filetypes=[("JSON files", "*.json")],
            initialfile="executable_macro.json",
        )
        if not file_path:
            return
        Path(file_path).write_text(
            self._macro_text.get("1.0", tk.END), encoding="utf-8"
        )

    def _load_macro_json(self) -> None:
        file_path = filedialog.askopenfilename(filetypes=[("JSON files", "*.json")])
        if not file_path:
            return
        raw_text = Path(file_path).read_text(encoding="utf-8")
        self._macro_text.delete("1.0", tk.END)
        self._macro_text.insert(tk.END, raw_text)
        self._validate_current_macro()


def launch_gui() -> None:
    root = tk.Tk()
    AIMacroGUIApp(root)
    root.mainloop()
