"""Module: @role: マクロ生成・事前検証・実行・サンプル出力・GUI起動を提供するCLIエントリポイント"""

import argparse
import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, TextIO

from ai_macro.generator import AIModeConfig, AIModeController, MacroGenerationPipeline
from ai_macro.models import AIMode, ExecutionStatus, WorkflowIR
from ai_macro.runner import ExecutionJobContext
from ai_macro.runtime import WorkspaceRuntimeState, build_default_runtime
from ai_macro.validator import MacroValidationError, MacroValidator

EXIT_SUCCESS: int = 0
EXIT_PARTIAL_OR_STOPPED: int = 2
EXIT_VALIDATION_ERROR: int = 3
EXIT_EXECUTION_FAILED: int = 4


def get_sample_raw_events() -> List[Dict[str, Any]]:
    return [
        {
            "event_id": "ev_0001",
            "timestamp_ms": 1000,
            "action_type": "input",
            "window_id": "win_main",
            "tab_id": "tab_login",
            "origin_url": "https://portal.example.com",
            "selector": "#username",
            "value": "op_user",
            "value_origin": "user_recorded",
            "criticality": "mandatory",
        },
        {
            "event_id": "ev_0002",
            "timestamp_ms": 1200,
            "action_type": "input",
            "window_id": "win_main",
            "tab_id": "tab_login",
            "origin_url": "https://portal.example.com",
            "selector": "#username",
            "value": "operator_01",
            "value_origin": "user_recorded",
            "criticality": "mandatory",
        },
        {
            "event_id": "ev_0003",
            "timestamp_ms": 1500,
            "action_type": "checkbox",
            "window_id": "win_main",
            "tab_id": "tab_login",
            "origin_url": "https://portal.example.com",
            "selector": "#confirm_policy",
            "value": True,
            "value_origin": "user_recorded",
            "criticality": "mandatory",
        },
        {
            "event_id": "ev_0004",
            "timestamp_ms": 1800,
            "action_type": "click",
            "window_id": "win_main",
            "tab_id": "tab_login",
            "origin_url": "https://portal.example.com",
            "selector": "#submit_btn",
            "value": None,
            "value_origin": "user_recorded",
            "criticality": "mandatory",
        },
        {
            "event_id": "ev_0005",
            "timestamp_ms": 2200,
            "action_type": "excel_write",
            "window_id": "win_main",
            "tab_id": "tab_login",
            "origin_url": "https://portal.example.com",
            "value": "COMPLETED",
            "value_origin": "user_recorded",
            "criticality": "mandatory",
            "metadata": {
                "workbook": "daily_report.xlsx",
                "sheet": "Summary",
                "cell": "B2",
            },
        },
    ]


def create_sample_files(output_dir: Path) -> Dict[str, Path]:
    output_dir.mkdir(parents=True, exist_ok=True)
    raw_events = get_sample_raw_events()
    events_path = output_dir / "events.json"
    workflow_path = output_dir / "workflow.json"
    macro_path = output_dir / "executable_macro.json"

    events_path.write_text(
        json.dumps(raw_events, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    pipeline = MacroGenerationPipeline()
    normalized = pipeline.normalize_raw_events(raw_events)
    ir = pipeline.optimize_workflow_ir(
        pipeline.build_workflow_ir("wf_sample_001", normalized)
    )
    macro = pipeline.generate_executable_macro("macro_sample_001", ir)

    workflow_path.write_text(
        json.dumps(ir.to_dict(), ensure_ascii=False, indent=2), encoding="utf-8"
    )
    macro_path.write_text(
        json.dumps(macro.to_dict(), ensure_ascii=False, indent=2), encoding="utf-8"
    )
    return {
        "events": events_path,
        "workflow": workflow_path,
        "macro": macro_path,
    }


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="ai_macro",
        description="AI Macro System - 高信頼マクロ記録・検証・実行パイプライン",
    )
    sub = parser.add_subparsers(dest="subcommand")

    p_init = sub.add_parser("init-sample", help="サンプルイベント・IR・実行可能マクロを生成")
    p_init.add_argument(
        "--out-dir",
        type=Path,
        default=Path("workspace_sample"),
        help="サンプルJSONファイルの出力先ディレクトリ",
    )

    p_gen = sub.add_parser("generate", help="操作記録JSONからWorkflow IRと実行可能マクロを生成")
    p_gen.add_argument("--events", type=Path, required=True, help="入力events.jsonのパス")
    p_gen.add_argument("--workflow-out", type=Path, required=True, help="出力workflow.jsonのパス")
    p_gen.add_argument(
        "--macro-out", type=Path, required=True, help="出力executable_macro.jsonのパス"
    )
    p_gen.add_argument("--workflow-id", type=str, default="wf_generated", help="Workflow ID")
    p_gen.add_argument("--macro-id", type=str, default="macro_generated", help="Macro ID")

    p_val = sub.add_parser("validate", help="executable_macro.jsonの実行前一括検証")
    p_val.add_argument("--macro", type=Path, required=True, help="検証対象executable_macro.json")
    p_val.add_argument(
        "--workflow", type=Path, default=None, help="整合性照合用のworkflow.json（任意）"
    )

    p_run = sub.add_parser("run", help="マクロの事前検証・実行・事後条件確認・監査ログ出力")
    p_run.add_argument("--macro", type=Path, required=True, help="実行対象executable_macro.json")
    p_run.add_argument(
        "--workflow", type=Path, default=None, help="整合性照合用のworkflow.json（任意）"
    )
    p_run.add_argument(
        "--report-out", type=Path, default=None, help="実行レポート・監査ログJSONの出力先"
    )
    p_run.add_argument(
        "--job-id", type=str, default="cli_job_001", help="実行ジョブ識別子"
    )
    p_run.add_argument(
        "--ai-mode",
        type=str,
        choices=[m.value for m in AIMode],
        default=AIMode.NO_AI.value,
        help="AI動作モード (no_ai / local_ai / lan_ai_server / external_ai)",
    )
    p_run.add_argument(
        "--ai-endpoint", type=str, default=None, help="AIサーバーのエンドポイントURL"
    )
    p_run.add_argument(
        "--external-consent",
        action="store_true",
        help="External AIへの通信に対するユーザーの明示的同意フラグ",
    )

    sub.add_parser("gui", help="デスクトップGUIアプリケーションを起動")
    return parser


def run_cli(
    argv: Optional[List[str]] = None,
    stdout: Optional[TextIO] = None,
    stderr: Optional[TextIO] = None,
) -> int:
    out = stdout or sys.stdout
    err = stderr or sys.stderr
    parser = _build_parser()
    args = parser.parse_args(argv)

    if args.subcommand in (None, "gui"):
        from ai_macro.gui import launch_gui

        launch_gui()
        return EXIT_SUCCESS

    try:
        if args.subcommand == "init-sample":
            paths = create_sample_files(args.out_dir)
            out.write(
                json.dumps(
                    {
                        "status": "sample_created",
                        "files": {k: str(v) for k, v in paths.items()},
                    },
                    ensure_ascii=False,
                    indent=2,
                )
                + "\n"
            )
            return EXIT_SUCCESS

        if args.subcommand == "generate":
            raw_data = json.loads(args.events.read_text(encoding="utf-8"))
            pipeline = MacroGenerationPipeline()
            events = pipeline.normalize_raw_events(raw_data)
            ir = pipeline.optimize_workflow_ir(
                pipeline.build_workflow_ir(args.workflow_id, events)
            )
            macro = pipeline.generate_executable_macro(args.macro_id, ir)
            args.workflow_out.parent.mkdir(parents=True, exist_ok=True)
            args.macro_out.parent.mkdir(parents=True, exist_ok=True)
            args.workflow_out.write_text(
                json.dumps(ir.to_dict(), ensure_ascii=False, indent=2), encoding="utf-8"
            )
            args.macro_out.write_text(
                json.dumps(macro.to_dict(), ensure_ascii=False, indent=2), encoding="utf-8"
            )
            out.write(
                json.dumps(
                    {
                        "status": "generated",
                        "workflow_id": ir.workflow_id,
                        "macro_id": macro.macro_id,
                        "steps_count": len(macro.steps),
                    },
                    ensure_ascii=False,
                    indent=2,
                )
                + "\n"
            )
            return EXIT_SUCCESS

        bundle = build_default_runtime(WorkspaceRuntimeState())
        wf_ir: Optional[WorkflowIR] = None
        if getattr(args, "workflow", None) is not None:
            wf_raw = json.loads(args.workflow.read_text(encoding="utf-8"))
            wf_ir = WorkflowIR.from_dict(wf_raw)

        macro_raw = json.loads(args.macro.read_text(encoding="utf-8"))
        macro = MacroValidator.parse_and_validate_json(
            macro_raw, bundle.runner.registered_commands, wf_ir
        )

        if args.subcommand == "validate":
            out.write(
                json.dumps(
                    {
                        "status": "valid",
                        "macro_id": macro.macro_id,
                        "workflow_id": macro.workflow_id,
                        "validated_steps": len(macro.steps),
                    },
                    ensure_ascii=False,
                    indent=2,
                )
                + "\n"
            )
            return EXIT_SUCCESS

        if args.subcommand == "run":
            ai_ctrl = AIModeController(
                AIModeConfig(
                    mode=AIMode(args.ai_mode),
                    endpoint_url=args.ai_endpoint,
                    explicit_external_consent=bool(args.external_consent),
                )
            )
            ctx = ExecutionJobContext(job_id=args.job_id)
            report = bundle.runner.run(macro, ctx, wf_ir)
            summary = {
                "job_id": report.job_id,
                "macro_id": report.macro_id,
                "ai_mode": ai_ctrl.mode.value,
                "final_status": report.final_status.value,
                "reason": report.reason,
                "step_statuses": [
                    {
                        "step_id": r.operation_id,
                        "command": r.command,
                        "status": r.status.value,
                        "observed_value": r.observed_value,
                        "error_code": r.error_code,
                    }
                    for r in report.step_results
                ],
                "audit_logs": report.audit_logs,
            }
            formatted = json.dumps(summary, ensure_ascii=False, indent=2)
            if args.report_out is not None:
                args.report_out.parent.mkdir(parents=True, exist_ok=True)
                args.report_out.write_text(formatted, encoding="utf-8")
            out.write(formatted + "\n")

            if report.final_status == ExecutionStatus.SUCCESS:
                return EXIT_SUCCESS
            if report.final_status in {
                ExecutionStatus.PARTIAL_SUCCESS,
                ExecutionStatus.STOPPED,
                ExecutionStatus.CANCELLED,
            }:
                return EXIT_PARTIAL_OR_STOPPED
            return EXIT_EXECUTION_FAILED

    except (MacroValidationError, PermissionError, ValueError, KeyError) as exc:
        err.write(
            json.dumps(
                {"status": "validation_or_policy_error", "error": str(exc)},
                ensure_ascii=False,
            )
            + "\n"
        )
        return EXIT_VALIDATION_ERROR

    return EXIT_SUCCESS
