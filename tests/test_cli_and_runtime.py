"""Module: @role: CLIサブコマンド・ローカルランタイムドライバー・エンドツーエンド実行の自動テスト"""

import io
import json
import tempfile
import unittest
from pathlib import Path

from ai_macro.cli import (
    EXIT_SUCCESS,
    EXIT_VALIDATION_ERROR,
    run_cli,
)


class TestCLIAndRuntime(unittest.TestCase):
    def test_end_to_end_cli_init_generate_validate_and_run(self) -> None:
        with tempfile.TemporaryDirectory() as tmp_dir:
            base = Path(tmp_dir)
            out_buf = io.StringIO()
            err_buf = io.StringIO()

            # 1. サンプルファイルの出力
            code = run_cli(
                ["init-sample", "--out-dir", str(base)], stdout=out_buf, stderr=err_buf
            )
            self.assertEqual(code, EXIT_SUCCESS)
            events_file = base / "events.json"
            wf_file = base / "workflow.json"
            macro_file = base / "executable_macro.json"
            self.assertTrue(events_file.exists())
            self.assertTrue(wf_file.exists())
            self.assertTrue(macro_file.exists())

            # 2. イベント記録からの再生成と最適化
            gen_wf = base / "gen_workflow.json"
            gen_macro = base / "gen_macro.json"
            code = run_cli(
                [
                    "generate",
                    "--events",
                    str(events_file),
                    "--workflow-out",
                    str(gen_wf),
                    "--macro-out",
                    str(gen_macro),
                ],
                stdout=out_buf,
                stderr=err_buf,
            )
            self.assertEqual(code, EXIT_SUCCESS)

            # 3. 実行前一括検証
            code = run_cli(
                ["validate", "--macro", str(gen_macro), "--workflow", str(gen_wf)],
                stdout=out_buf,
                stderr=err_buf,
            )
            self.assertEqual(code, EXIT_SUCCESS)

            # 4. ローカルランタイムでの本番実行と監査レポート出力
            report_file = base / "audit_report.json"
            code = run_cli(
                [
                    "run",
                    "--macro",
                    str(gen_macro),
                    "--workflow",
                    str(gen_wf),
                    "--report-out",
                    str(report_file),
                ],
                stdout=out_buf,
                stderr=err_buf,
            )
            self.assertEqual(code, EXIT_SUCCESS)
            report_data = json.loads(report_file.read_text(encoding="utf-8"))
            self.assertEqual(report_data["final_status"], "success")
            self.assertEqual(len(report_data["step_statuses"]), 4)

    def test_cli_blocks_external_ai_without_explicit_consent(self) -> None:
        with tempfile.TemporaryDirectory() as tmp_dir:
            base = Path(tmp_dir)
            out_buf = io.StringIO()
            err_buf = io.StringIO()
            run_cli(["init-sample", "--out-dir", str(base)], stdout=out_buf, stderr=err_buf)

            code = run_cli(
                [
                    "run",
                    "--macro",
                    str(base / "executable_macro.json"),
                    "--ai-mode",
                    "external_ai",
                    "--ai-endpoint",
                    "https://api.example.com/v1",
                ],
                stdout=out_buf,
                stderr=err_buf,
            )
            self.assertEqual(code, EXIT_VALIDATION_ERROR)
            self.assertIn("REQ-AI-003", err_buf.getvalue())


if __name__ == "__main__":
    unittest.main()
