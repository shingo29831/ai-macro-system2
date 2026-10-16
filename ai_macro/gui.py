"""Module: @role: IT非熟練者向けに日常語・プリセット選択・ワンクリック承認・日本語レポートを提供する直感的GUI"""

import json
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, scrolledtext, ttk
from typing import Any, Dict, List, Optional, Tuple

from ai_macro.cli import get_sample_raw_events
from ai_macro.generator import AIModeConfig, AIModeController, MacroGenerationPipeline
from ai_macro.models import (
    AIMode,
    CommandType,
    ExecutableMacro,
    ExecutionStatus,
    MacroExecutionReport,
    WorkflowIR,
)
from ai_macro.runner import ExecutionJobContext
from ai_macro.runtime import WorkspaceRuntimeState, build_default_runtime
from ai_macro.validator import MacroValidationError, MacroValidator

ACTION_LABELS: Dict[str, str] = {
    "文字を入力する": "input",
    "ボタンを押す・クリックする": "click",
    "チェックを入れる・外す": "checkbox",
    "リストから選ぶ": "select",
    "画面をスクロールする": "scroll",
    "Excelに書き込む": "excel_write",
}

REVERSE_ACTION_LABELS: Dict[str, str] = {v: k for k, v in ACTION_LABELS.items()}

TARGET_PRESETS: Dict[str, Dict[str, Any]] = {
    "ログインID入力欄": {"selector": "#username"},
    "パスワード入力欄": {"selector": "#password"},
    "検索キーワード入力欄": {"selector": "#search_box"},
    "利用規約・確認のチェック欄": {"selector": "#confirm_policy"},
    "ログイン・送信ボタン": {"selector": "#submit_btn"},
    "担当ロール選択リスト": {"selector": "#role_select"},
    "画面の下方向（スクロール）": {"selector": None, "scroll_x": 0, "scroll_y": 300},
    "Excelの日報シート（B2セル）": {
        "selector": None,
        "workbook": "daily_report.xlsx",
        "sheet": "Summary",
        "cell": "B2",
    },
}

AI_MODE_CHOICES: Dict[str, AIMode] = {
    "AIを使わない（自分の操作だけ実行・推奨）": AIMode.NO_AI,
    "パソコン内のAIを使う（外部送信なし）": AIMode.LOCAL_AI,
    "社内ネットワークのAIを使う": AIMode.LAN_AI_SERVER,
    "クラウドのAIを使う（外部通信の同意が必要）": AIMode.EXTERNAL_AI,
}

STATUS_BANNER_STYLES: Dict[ExecutionStatus, Tuple[str, str, str]] = {
    ExecutionStatus.SUCCESS: (
        "✅ すべての作業が正しく完了しました！（画面・ファイルの反映も確認済みです）",
        "#dff6dd",
        "#107c10",
    ),
    ExecutionStatus.PARTIAL_SUCCESS: (
        "⚠️ 必須の作業はすべて完了しました（一部の任意の作業をスキップしました）",
        "#fff4ce",
        "#797673",
    ),
    ExecutionStatus.FAILED: (
        "❌ 作業中に問題が発生したため、安全のために自動でストップしました",
        "#fde7e9",
        "#a80000",
    ),
    ExecutionStatus.STOPPED: (
        "⏹ 停止ボタンが押されたため、作業を途中でストップしました",
        "#fff4ce",
        "#8a5700",
    ),
    ExecutionStatus.CANCELLED: (
        "✖ 作業がキャンセルされました",
        "#f3f2f1",
        "#605e5c",
    ),
    ExecutionStatus.TIMEOUT: (
        "⏱ 待ち時間を過ぎても画面が反応しなかったため、安全に停止しました",
        "#fde7e9",
        "#a80000",
    ),
    ExecutionStatus.UNKNOWN: (
        "❓ 作業結果を確認できませんでした（二重処理を防ぐため自動再実行せず停止しました）",
        "#fff4ce",
        "#8a5700",
    ),
}


class FriendlyUIPresenter:
    @staticmethod
    def resolve_target_Label(event: Dict[str, Any]) -> str:
        selector = event.get("selector")
        meta = event.get("metadata", {})
        for label, spec in TARGET_PRESETS.items():
            if selector and spec.get("selector") == selector:
                return label
            if event.get("action_type") == "excel_write" and spec.get("workbook") == meta.get(
                "workbook"
            ):
                return f"Excel ({meta.get('workbook')} / {meta.get('sheet')} / {meta.get('cell')})"
        if event.get("action_type") == "scroll":
            return f"縦方向に {meta.get('scroll_y', 300)}px 移動"
        return str(selector or "画面全体")

    @classmethod
    def describe_event_for_human(cls, event: Dict[str, Any]) -> str:
        act = event.get("action_type", "")
        target = cls.resolve_target_Label(event)
        val = event.get("value")
        if act == "input":
            return f"「{target}」に『{val}』と文字を入力する"
        if act == "click":
            return f"「{target}」のボタンを押す"
        if act == "checkbox":
            state_str = "チェックを入れる" if val else "チェックを外す"
            return f"「{target}」に{state_str}"
        if act == "select":
            return f"「{target}」から『{val}』を選ぶ"
        if act == "scroll":
            return f"画面をスクロールする（{target}）"
        if act == "excel_write":
            return f"{target} に『{val}』と書き込む"
        return f"{act}: {target}"

    @staticmethod
    def build_event_from_friendly_input(
        index: int,
        action_label: str,
        target_label: str,
        raw_value: str,
        is_mandatory: bool,
        is_ai_suggested: bool,
        is_approved: bool,
    ) -> Dict[str, Any]:
        act_type = ACTION_LABELS.get(action_label, "input")
        preset = TARGET_PRESETS.get(target_label, {})
        selector = preset.get("selector")
        if selector is None and act_type not in {"scroll", "excel_write"}:
            selector = target_label.strip() or "#target_element"

        val: Any = raw_value
        if act_type == "checkbox":
            val = str(raw_value).strip().lower() not in {"false", "0", "off", "外す", "いいえ"}
        elif act_type == "click":
            val = None

        meta: Dict[str, Any] = {"human_approved": bool(is_approved)}
        if act_type == "scroll":
            meta.update(
                {
                    "scroll_x": int(preset.get("scroll_x", 0)),
                    "scroll_y": int(preset.get("scroll_y", 300)),
                }
            )
        elif act_type == "excel_write":
            meta.update(
                {
                    "workbook": str(preset.get("workbook", "daily_report.xlsx")),
                    "sheet": str(preset.get("sheet", "Summary")),
                    "cell": str(preset.get("cell", "B2")),
                }
            )

        return {
            "event_id": f"ev_{index:04d}",
            "timestamp_ms": 1000 + index * 250,
            "action_type": act_type,
            "window_id": "win_main",
            "tab_id": "tab_login",
            "origin_url": "https://portal.example.com",
            "selector": selector,
            "value": val,
            "value_origin": "ai_inferred" if is_ai_suggested else "user_recorded",
            "criticality": "mandatory" if is_mandatory else "optional",
            "metadata": meta,
        }

    @staticmethod
    def format_friendly_report(report: MacroExecutionReport) -> str:
        banner_msg, _, _ = STATUS_BANNER_STYLES.get(
            report.final_status,
            ("状態を確認してください", "#f3f2f1", "#323130"),
        )
        lines: List[str] = [
            "============================================================",
            f"  {banner_msg}",
            "============================================================",
            "",
            "【各手順のくわしい実行結果】",
        ]

        cmd_names = {
            CommandType.BROWSER_INPUT.value: "文字の入力",
            CommandType.BROWSER_CLICK.value: "ボタンのクリック",
            CommandType.BROWSER_CHECKBOX.value: "チェック欄の切り替え",
            CommandType.BROWSER_SELECT.value: "リストの選択",
            CommandType.BROWSER_SCROLL.value: "画面のスクロール",
            CommandType.EXCEL_WRITE.value: "Excelへの書き込み",
            CommandType.SCREEN_MATCH_CLICK.value: "画像を見つけてクリック",
            CommandType.RUN_SCRIPT.value: "連携プログラムの実行",
            CommandType.LOOP.value: "繰り返し作業",
        }

        for idx, res in enumerate(report.step_results, start=1):
            cname = cmd_names.get(res.command, res.command)
            if res.status == ExecutionStatus.SUCCESS:
                mark = "✅ 成功"
                detail = f"実際に確認できた結果: {res.observed_value}"
                if res.post_condition_result and res.post_condition_result.verified:
                    detail += " （画面・ファイルに正しく反映されたことを確認済み）"
            elif res.status == ExecutionStatus.SKIPPED:
                mark = "⏭ スキップ（未実行）"
                detail = f"理由: 前の手順で停止したため、安全のため実行しませんでした"
            elif res.status == ExecutionStatus.UNKNOWN:
                mark = "❓ 結果不明"
                detail = "理由: 操作後の状態を確認できなかったため、成功とはみなさず停止しました"
            elif res.status in {ExecutionStatus.STOPPED, ExecutionStatus.CANCELLED}:
                mark = "⏹ 途中停止"
                detail = "理由: ユーザーの操作によって安全に止められました"
            else:
                mark = "❌ 失敗"
                detail = f"原因: {res.error_message or res.error_code}"

            lines.append(f"  手順 {idx} [{cname}] ── {mark}")
            lines.append(f"     └ {detail}")

        lines.extend(
            [
                "",
                "------------------------------------------------------------",
                "🔒 セキュリティ保護: パスワードや秘密キーなどの機密情報は、",
                "   記録時に自動で伏せ字（***REDACTED***）として保護されています。",
            ]
        )
        return "\n".join(lines)

    @staticmethod
    def format_friendly_validation_error(exc: Exception) -> str:
        text = str(exc)
        if "REQ-GEN-004" in text or "REQ-AI-005" in text or "human_approved" in text:
            return (
                "AIが提案した手順で、まだ『承認』されていないものがあります。\n\n"
                "一覧から「⚠️ AI提案（未承認）」の手順を選び、\n"
                "内容を確認してから『✅ 選択したAI提案を承認する』ボタンを押してください。"
            )
        if "REQ-AI-003" in text:
            return (
                "クラウドのAI（外部AI）を使う場合は、\n"
                "画面上部の『外部AIへの通信に同意する』にチェックを入れてください。"
            )
        if "REQ-LOAD-007" in text or "REQ-GEN-008" in text:
            return (
                "元の記録にあった「必須の手順」が抜けています。\n"
                "安全のため、必須手順が欠けた状態では実行できません。"
            )
        return f"実行前の安全チェックで問題が見つかりました:\n\n{text}"


class AIMacroGUIApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("かんたん自動作業アシスタント (AI Macro System)")
        self.root.geometry("1220x800")
        self.root.configure(bg="#f8f9fa")

        self._raw_events: List[Dict[str, Any]] = []
        self._current_ir: Optional[WorkflowIR] = None
        self._current_macro: Optional[ExecutableMacro] = None
        self._active_job: Optional[ExecutionJobContext] = None
        self._ai_controller = AIModeController(AIModeConfig(mode=AIMode.NO_AI))
        self._runtime_bundle = build_default_runtime(WorkspaceRuntimeState())

        self._configure_styles()
        self._build_ui()
        self._load_sample_events()

    def _configure_styles(self) -> None:
        style = ttk.Style()
        style.configure("Big.TButton", font=("Meiryo UI", 11, "bold"), padding=8)
        style.configure("Action.TButton", font=("Meiryo UI", 10), padding=5)
        style.configure("Treeview", font=("Meiryo UI", 10), rowheight=28)
        style.configure("Treeview.Heading", font=("Meiryo UI", 10, "bold"))

    def _build_ui(self) -> None:
        # 上部：ガイド付きヘッダーとAI設定
        header = tk.Frame(self.root, bg="#ffffff", bd=1, relief=tk.SOLID, padx=14, pady=10)
        header.pack(fill=tk.X, padx=12, pady=(10, 6))

        title_lbl = tk.Label(
            header,
            text="🤖 かんたん自動作業アシスタント",
            font=("Meiryo UI", 14, "bold"),
            bg="#ffffff",
            fg="#1f2328",
        )
        title_lbl.pack(anchor=tk.W)
        sub_lbl = tk.Label(
            header,
            text="① やりたい作業を並べる  ➡  ② AI提案があれば確認・承認する  ➡  ③ 「作業をスタート」を押すだけ！（自動で結果まで確認します）",
            font=("Meiryo UI", 10),
            bg="#ffffff",
            fg="#57606a",
        )
        sub_lbl.pack(anchor=tk.W, pady=(2, 8))

        ai_bar = tk.Frame(header, bg="#f0f4f8", padx=10, pady=6)
        ai_bar.pack(fill=tk.X)
        tk.Label(
            ai_bar, text="AIサポート設定:", font=("Meiryo UI", 10, "bold"), bg="#f0f4f8"
        ).pack(side=tk.LEFT, padx=4)

        self._ai_choice_var = tk.StringVar(value=list(AI_MODE_CHOICES.keys())[0])
        ai_combo = ttk.Combobox(
            ai_bar,
            textvariable=self._ai_choice_var,
            values=list(AI_MODE_CHOICES.keys()),
            state="readonly",
            width=42,
        )
        ai_combo.pack(side=tk.LEFT, padx=6)
        ai_combo.bind("<<ComboboxSelected>>", lambda _e: self._apply_ai_mode())

        self._consent_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            ai_bar,
            text="クラウドAI利用時に外部通信へ同意する",
            variable=self._consent_var,
            command=self._apply_ai_mode,
        ).pack(side=tk.LEFT, padx=10)

        self._ai_status_label = tk.Label(
            ai_bar,
            text="🔒 現在：AIを使わず、あなたが決めた手順だけを確実に実行します",
            font=("Meiryo UI", 9, "bold"),
            bg="#f0f4f8",
            fg="#0969da",
        )
        self._ai_status_label.pack(side=tk.RIGHT, padx=6)

        # メイン左右分割ペイン
        paned = ttk.PanedWindow(self.root, orient=tk.HORIZONTAL)
        paned.pack(fill=tk.BOTH, expand=True, padx=12, pady=6)

        left_frame = ttk.LabelFrame(
            paned, text="ステップ 1 & 2：やりたい作業のリスト（追加・並び替え・AI提案の承認）", padding=10
        )
        right_frame = ttk.LabelFrame(
            paned, text="ステップ 3：おまかせ実行と結果の確認", padding=10
        )
        paned.add(left_frame, weight=11)
        paned.add(right_frame, weight=10)

        self._build_left_pane(left_frame)
        self._build_right_pane(right_frame)

    def _build_left_pane(self, parent: ttk.LabelFrame) -> None:
        form = ttk.LabelFrame(parent, text="＋ 新しい作業をリストに追加する", padding=10)
        form.pack(fill=tk.X, pady=(0, 8))

        ttk.Label(form, text="① なにをしますか？:").grid(
            row=0, column=0, sticky=tk.W, padx=4, pady=4
        )
        self._act_label_var = tk.StringVar(value="文字を入力する")
        ttk.Combobox(
            form,
            textvariable=self._act_label_var,
            values=list(ACTION_LABELS.keys()),
            state="readonly",
            width=26,
        ).grid(row=0, column=1, sticky=tk.W, padx=4, pady=4)

        ttk.Label(form, text="② どこの場所に？:").grid(
            row=0, column=2, sticky=tk.W, padx=4, pady=4
        )
        self._target_label_var = tk.StringVar(value="検索キーワード入力欄")
        ttk.Combobox(
            form,
            textvariable=self._target_label_var,
            values=list(TARGET_PRESETS.keys()),
            width=28,
        ).grid(row=0, column=3, sticky=tk.W, padx=4, pady=4)

        ttk.Label(form, text="③ 入力する文字や値:").grid(
            row=1, column=0, sticky=tk.W, padx=4, pady=4
        )
        self._value_var = tk.StringVar(value="2026年10月度 売上データ")
        ttk.Entry(form, textvariable=self._value_var, width=28).grid(
            row=1, column=1, sticky=tk.W, padx=4, pady=4
        )

        self._mandatory_var = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            form,
            text="必須の作業（失敗したらそこで安全に止める）",
            variable=self._mandatory_var,
        ).grid(row=1, column=2, columnspan=2, sticky=tk.W, padx=4, pady=4)

        self._ai_suggested_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            form,
            text="AIの提案として追加する（実行前に人間の確認・承認が必要になります）",
            variable=self._ai_suggested_var,
        ).grid(row=2, column=0, columnspan=3, sticky=tk.W, padx=4, pady=4)

        ttk.Button(
            form,
            text="＋ この作業をリストに追加",
            style="Action.TButton",
            command=self._add_event,
        ).grid(row=2, column=3, sticky=tk.E, padx=4, pady=4)

        # リスト操作ツールバー
        toolbar = ttk.Frame(parent)
        toolbar.pack(fill=tk.X, pady=4)
        ttk.Button(
            toolbar,
            text="✅ 選択したAI提案を承認する",
            style="Action.TButton",
            command=self._approve_selected_event,
        ).pack(side=tk.LEFT, padx=2)
        ttk.Button(toolbar, text="↑ 上へ移動", command=lambda: self._move_event(-1)).pack(
            side=tk.LEFT, padx=2
        )
        ttk.Button(toolbar, text="↓ 下へ移動", command=lambda: self._move_event(1)).pack(
            side=tk.LEFT, padx=2
        )
        ttk.Button(toolbar, text="🗑 選択を削除", command=self._remove_selected_event).pack(
            side=tk.LEFT, padx=2
        )
        ttk.Button(
            toolbar, text="🔄 お手本（サンプル）に戻す", command=self._load_sample_events
        ).pack(side=tk.RIGHT, padx=2)

        cols = ("step_no", "description", "importance", "approval")
        self._tree = ttk.Treeview(parent, columns=cols, show="headings", height=14)
        self._tree.heading("step_no", text="順番")
        self._tree.heading("description", text="やること（作業内容）")
        self._tree.heading("importance", text="重要度")
        self._tree.heading("approval", text="確認状態")

        self._tree.column("step_no", width=55, anchor=tk.CENTER)
        self._tree.column("description", width=330, anchor=tk.W)
        self._tree.column("importance", width=90, anchor=tk.CENTER)
        self._tree.column("approval", width=150, anchor=tk.CENTER)
        self._tree.pack(fill=tk.BOTH, expand=True, pady=4)

    def _build_right_pane(self, parent: ttk.LabelFrame) -> None:
        # 大きな実行コントロールエリア
        exec_box = tk.Frame(parent, bg="#ffffff", bd=1, relief=tk.SOLID, padx=10, pady=10)
        exec_box.pack(fill=tk.X, pady=(0, 8))

        ttk.Button(
            exec_box,
            text="▶ 作業をスタート（安全チェック＆自動実行）",
            style="Big.TButton",
            command=self._run_current_macro,
        ).pack(side=tk.LEFT, padx=4)

        ttk.Button(
            exec_box,
            text="🔍 事前の安全チェックだけ行う",
            style="Action.TButton",
            command=self._validate_only_action,
        ).pack(side=tk.LEFT, padx=4)

        ttk.Button(
            exec_box,
            text="⏹ 緊急ストップ",
            style="Action.TButton",
            command=self._request_stop,
        ).pack(side=tk.RIGHT, padx=4)

        # 信号機カラーのステータスバナー
        self._banner_frame = tk.Frame(
            parent, bg="#e6f2ff", bd=1, relief=tk.SOLID, padx=12, pady=10
        )
        self._banner_frame.pack(fill=tk.X, pady=4)
        self._status_badge = tk.Label(
            self._banner_frame,
            text="💡 準備OK：「▶ 作業をスタート」を押すと、上から順番に作業を実行します",
            font=("Meiryo UI", 11, "bold"),
            bg="#e6f2ff",
            fg="#004578",
            wraplength=520,
            justify=tk.LEFT,
        )
        self._status_badge.pack(anchor=tk.W)

        # タブ切り替え（わかりやすい日本語レポート vs サポート用詳細データ）
        notebook = ttk.Notebook(parent)
        notebook.pack(fill=tk.BOTH, expand=True, pady=6)

        report_tab = ttk.Frame(notebook, padding=6)
        tech_tab = ttk.Frame(notebook, padding=6)
        notebook.add(report_tab, text="📋 わかりやすい実行結果レポート")
        notebook.add(tech_tab, text="🔧 詳細データ（ファイル保存・サポート用）")

        self._friendly_report_text = scrolledtext.ScrolledText(
            report_tab, wrap=tk.WORD, font=("Meiryo UI", 10), bg="#ffffff"
        )
        self._friendly_report_text.pack(fill=tk.BOTH, expand=True)

        tech_btn_bar = ttk.Frame(tech_tab)
        tech_btn_bar.pack(fill=tk.X, pady=(0, 4))
        ttk.Button(
            tech_btn_bar, text="💾 作業ファイル(JSON)を保存", command=self._save_macro_json
        ).pack(side=tk.LEFT, padx=4)
        ttk.Button(
            tech_btn_bar, text="📂 作業ファイル(JSON)を読込", command=self._load_macro_json
        ).pack(side=tk.LEFT, padx=4)

        self._macro_text = scrolledtext.ScrolledText(
            tech_tab, wrap=tk.NONE, font=("Consolas", 9)
        )
        self._macro_text.pack(fill=tk.BOTH, expand=True)

    def _set_banner(self, text: str, bg_color: str, fg_color: str) -> None:
        self._banner_frame.configure(bg=bg_color)
        self._status_badge.configure(text=text, bg=bg_color, fg=fg_color)

    def _apply_ai_mode(self) -> None:
        label = self._ai_choice_var.get()
        mode = AI_MODE_CHOICES.get(label, AIMode.NO_AI)
        endpoint_map = {
            AIMode.NO_AI: None,
            AIMode.LOCAL_AI: "http://127.0.0.1:11434",
            AIMode.LAN_AI_SERVER: "http://192.168.1.100:8080",
            AIMode.EXTERNAL_AI: "https://api.example.com/v1",
        }
        try:
            cfg = AIModeConfig(
                mode=mode,
                endpoint_url=endpoint_map[mode],
                explicit_external_consent=bool(self._consent_var.get()),
            )
            self._ai_controller = AIModeController(cfg)
            self._ai_status_label.config(
                text=f"✅ 設定完了: {label}", fg="#107c10"
            )
        except (PermissionError, ValueError) as exc:
            self._ai_choice_var.set(list(AI_MODE_CHOICES.keys())[0])
            self._ai_controller = AIModeController(AIModeConfig(mode=AIMode.NO_AI))
            self._ai_status_label.config(
                text="🔒 外部AIの同意がないため「AIを使わない」に戻しました", fg="#a80000"
            )
            messagebox.showwarning(
                "AI設定の確認", FriendlyUIPresenter.format_friendly_validation_error(exc)
            )

    def _load_sample_events(self) -> None:
        self._raw_events = get_sample_raw_events()
        self._refresh_event_tree()
        self._sync_macro_preview(silent=True)
        self._friendly_report_text.delete("1.0", tk.END)
        self._friendly_report_text.insert(
            tk.END,
            "ここに作業の実行結果がわかりやすい日本語で表示されます。\n\n"
            "【現在入っているお手本（サンプル）の作業内容】\n"
            "  1. ログインID入力欄に『operator_01』と入力します（途中の入力ミスは自動でまとめられます）\n"
            "  2. 利用規約・確認のチェック欄にチェックを入れます\n"
            "  3. ログイン・送信ボタンを押します\n"
            "  4. Excelの日報シート（B2セル）に『COMPLETED』と書き込みます\n\n"
            "上の「▶ 作業をスタート」ボタンを押して試してみてください！",
        )

    def _refresh_event_tree(self) -> None:
        for item in self._tree.get_children():
            self._tree.delete(item)
        for idx, ev in enumerate(self._raw_events, start=1):
            origin = ev.get("value_origin", "user_recorded")
            approved = origin == "user_recorded" or ev.get("metadata", {}).get(
                "human_approved", False
            )
            if origin == "user_recorded":
                approval_str = "✅ 自分の操作（確認済）"
            elif approved:
                approval_str = "✅ AI提案（承認済）"
            else:
                approval_str = "⚠️ AI提案（未承認）"

            importance_str = (
                "必須" if ev.get("criticality", "mandatory") == "mandatory" else "任意"
            )
            desc = FriendlyUIPresenter.describe_event_for_human(ev)
            self._tree.insert(
                "",
                tk.END,
                iid=str(idx - 1),
                values=(f"手順 {idx}", desc, importance_str, approval_str),
            )

    def _add_event(self) -> None:
        is_ai = bool(self._ai_suggested_var.get())
        ev = FriendlyUIPresenter.build_event_from_friendly_input(
            index=len(self._raw_events) + 1,
            action_label=self._act_label_var.get(),
            target_label=self._target_label_var.get(),
            raw_value=self._value_var.get(),
            is_mandatory=bool(self._mandatory_var.get()),
            is_ai_suggested=is_ai,
            is_approved=not is_ai,
        )
        self._raw_events.append(ev)
        self._refresh_event_tree()
        if not is_ai:
            self._sync_macro_preview(silent=True)
        else:
            self._set_banner(
                "⚠️ AI提案の手順を追加しました。実行する前に、一覧で選んで「✅ 選択したAI提案を承認する」を押してください。",
                "#fff4ce",
                "#8a5700",
            )

    def _approve_selected_event(self) -> None:
        selected = self._tree.selection()
        if not selected:
            messagebox.showinfo("承認の案内", "承認したい「⚠️ AI提案（未承認）」の手順を一覧からクリックして選んでください。")
            return
        for item_id in selected:
            idx = int(item_id)
            if 0 <= idx < len(self._raw_events):
                meta = dict(self._raw_events[idx].get("metadata", {}))
                meta["human_approved"] = True
                self._raw_events[idx]["metadata"] = meta
        self._refresh_event_tree()
        self._sync_macro_preview(silent=True)
        self._set_banner(
            "✅ AIが提案した手順を承認しました！これで安全に実行できます。",
            "#dff6dd",
            "#107c10",
        )

    def _move_event(self, direction: int) -> None:
        selected = self._tree.selection()
        if not selected:
            return
        idx = int(selected[0])
        new_idx = idx + direction
        if 0 <= new_idx < len(self._raw_events):
            self._raw_events[idx], self._raw_events[new_idx] = (
                self._raw_events[new_idx],
                self._raw_events[idx],
            )
            self._refresh_event_tree()
            self._tree.selection_set(str(new_idx))
            self._sync_macro_preview(silent=True)

    def _remove_selected_event(self) -> None:
        selected = self._tree.selection()
        if not selected:
            return
        indices = sorted((int(i) for i in selected), reverse=True)
        for idx in indices:
            if 0 <= idx < len(self._raw_events):
                self._raw_events.pop(idx)
        self._refresh_event_tree()
        if self._raw_events:
            self._sync_macro_preview(silent=True)

    def _sync_macro_preview(self, silent: bool = False) -> bool:
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
            return True
        except MacroValidationError as exc:
            self._current_macro = None
            if not silent:
                self._set_banner(
                    "⚠️ 実行前の安全チェックでストップしました（メッセージを確認してください）",
                    "#fde7e9",
                    "#a80000",
                )
                messagebox.showwarning(
                    "実行前の安全チェック",
                    FriendlyUIPresenter.format_friendly_validation_error(exc),
                )
            return False

    def _validate_only_action(self) -> None:
        if self._sync_macro_preview(silent=False):
            self._set_banner(
                "✅ 事前の安全チェック完了：すべての手順が正しく設定されています！安心して実行できます。",
                "#dff6dd",
                "#107c10",
            )
            messagebox.showinfo(
                "安全チェック完了",
                "すべての手順のチェックに合格しました！\n「▶ 作業をスタート」を押すと実行できます。",
            )

    def _run_current_macro(self) -> None:
        if not self._sync_macro_preview(silent=False) or self._current_macro is None:
            return
        self._active_job = ExecutionJobContext(job_id="gui_job_001")
        report = self._runtime_bundle.runner.run(
            self._current_macro, self._active_job, self._current_ir
        )

        text, bg, fg = STATUS_BANNER_STYLES.get(
            report.final_status,
            (f"状態: {report.final_status.value}", "#f3f2f1", "#323130"),
        )
        self._set_banner(text, bg, fg)

        friendly_summary = FriendlyUIPresenter.format_friendly_report(report)
        self._friendly_report_text.delete("1.0", tk.END)
        self._friendly_report_text.insert(tk.END, friendly_summary)

        self._macro_text.delete("1.0", tk.END)
        self._macro_text.insert(
            tk.END,
            json.dumps(
                {
                    "executable_macro": self._current_macro.to_dict(),
                    "workflow_ir": self._current_ir.to_dict() if self._current_ir else None,
                    "audit_logs": report.audit_logs,
                },
                ensure_ascii=False,
                indent=2,
            ),
        )

    def _request_stop(self) -> None:
        if self._active_job is not None:
            self._active_job.request_stop()
        text, bg, fg = STATUS_BANNER_STYLES[ExecutionStatus.STOPPED]
        self._set_banner(text, bg, fg)

    def _save_macro_json(self) -> None:
        if not self._sync_macro_preview(silent=False):
            return
        file_path = filedialog.asksaveasfilename(
            defaultextension=".json",
            filetypes=[("自動作業ファイル (*.json)", "*.json")],
            initialfile="executable_macro.json",
        )
        if not file_path:
            return
        Path(file_path).write_text(
            self._macro_text.get("1.0", tk.END), encoding="utf-8"
        )
        messagebox.showinfo("保存完了", "作業ファイルを保存しました。")

    def _load_macro_json(self) -> None:
        file_path = filedialog.askopenfilename(
            filetypes=[("自動作業ファイル (*.json)", "*.json")]
        )
        if not file_path:
            return
        try:
            raw_text = Path(file_path).read_text(encoding="utf-8")
            content = json.loads(raw_text)
            macro_dict = content.get("executable_macro", content)
            ir_dict = content.get("workflow_ir")
            wf_ir = WorkflowIR.from_dict(ir_dict) if isinstance(ir_dict, dict) else None
            self._current_macro = MacroValidator.parse_and_validate_json(
                macro_dict, self._runtime_bundle.runner.registered_commands, wf_ir
            )
            self._current_ir = wf_ir
            self._macro_text.delete("1.0", tk.END)
            self._macro_text.insert(tk.END, raw_text)
            self._set_banner(
                "✅ 作業ファイルを読み込み、安全チェックに合格しました！",
                "#dff6dd",
                "#107c10",
            )
        except (json.JSONDecodeError, MacroValidationError) as exc:
            messagebox.showerror(
                "ファイルの読み込みエラー",
                FriendlyUIPresenter.format_friendly_validation_error(exc),
            )


def launch_gui() -> None:
    root = tk.Tk()
    AIMacroGUIApp(root)
    root.mainloop()
