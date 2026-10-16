"""Module: @role: コマンドディスパッチ・ループ/再試行制御・ジョブ単位の停止分離・最終状態判定・機密保護監査ログ"""

import re
from dataclasses import replace
from typing import Any, Callable, Dict, List, Optional, Set, Tuple
from ai_macro.models import (
    CommandType,
    Criticality,
    ExecutableMacro,
    ExecutionStatus,
    FailurePolicy,
    MacroExecutionReport,
    MacroStep,
    OperationResult,
    PostConditionResult,
    PostConditionSpec,
    SideEffectLevel,
    WorkflowIR,
)
from ai_macro.validator import MacroValidator


class ExecutionJobContext:
    def __init__(self, job_id: str) -> None:
        self.job_id: str = job_id
        self._stop_requested: bool = False
        self._cancel_requested: bool = False

    def request_stop(self) -> None:
        self._stop_requested = True

    def request_cancel(self) -> None:
        self._cancel_requested = True

    @property
    def stop_requested(self) -> bool:
        return self._stop_requested

    @property
    def cancel_requested(self) -> bool:
        return self._cancel_requested


class AuditLogger:
    _SENSITIVE_KEY_PATTERN = re.compile(
        r"(password|passwd|secret|token|api_key|credential|authorization|ssn|credit_card)",
        re.IGNORECASE,
    )
    _SENSITIVE_VALUE_PATTERN = re.compile(
        r"(Bearer\s+[A-Za-z0-9\-._~+/]+=*|sk-[A-Za-z0-9]{16,})",
        re.IGNORECASE,
    )

    def __init__(self) -> None:
        self._entries: List[Dict[str, Any]] = []

    @property
    def entries(self) -> List[Dict[str, Any]]:
        return list(self._entries)

    def record_step(self, step: MacroStep, result: OperationResult) -> None:
        self._entries.append(
            {
                "event": "step_execution",
                "step_id": step.step_id,
                "ir_node_id": step.ir_node_id,
                "source_event_ids": list(step.source_event_ids),
                "command": step.command,
                "status": result.status.value,
                "started_at_ms": result.started_at_ms,
                "ended_at_ms": result.ended_at_ms,
                "requested_params": self._sanitize(result.requested_params),
                "executed_params": self._sanitize(result.executed_params),
                "observed_value": self._sanitize(result.observed_value),
                "post_condition": (
                    {
                        "verified": result.post_condition_result.verified,
                        "status": result.post_condition_result.status.value,
                        "reason": result.post_condition_result.reason,
                    }
                    if result.post_condition_result
                    else None
                ),
                "error_code": result.error_code,
                "error_message": result.error_message,
                "skipped_reason": result.skipped_reason,
                "retry_count": result.retry_count,
                "retry_reasons": list(result.retry_reasons),
            }
        )

    def record_final(self, status: ExecutionStatus, reason: str) -> None:
        self._entries.append(
            {
                "event": "macro_finalized",
                "final_status": status.value,
                "reason": reason,
            }
        )

    @classmethod
    def _sanitize(cls, data: Any) -> Any:
        if isinstance(data, dict):
            sanitized: Dict[str, Any] = {}
            for k, v in data.items():
                if cls._SENSITIVE_KEY_PATTERN.search(str(k)):
                    sanitized[k] = "***REDACTED***"
                else:
                    sanitized[k] = cls._sanitize(v)
            return sanitized
        if isinstance(data, list):
            return [cls._sanitize(item) for item in data]
        if isinstance(data, str) and cls._SENSITIVE_VALUE_PATTERN.search(data):
            return "***REDACTED***"
        return data


class MacroRunner:
    def __init__(
        self,
        handlers: Dict[str, Callable[[MacroStep], OperationResult]],
        clock_ms: Callable[[], int],
        state_probe: Optional[Callable[[MacroStep], Optional[bool]]] = None,
        post_condition_evaluator: Optional[
            Callable[[PostConditionSpec], PostConditionResult]
        ] = None,
        loop_condition_evaluator: Optional[Callable[[str, int], bool]] = None,
    ) -> None:
        self._handlers = dict(handlers)
        self._clock = clock_ms
        self._state_probe = state_probe
        self._post_condition_evaluator = post_condition_evaluator
        self._loop_condition_evaluator = loop_condition_evaluator

    @property
    def registered_commands(self) -> Set[str]:
        return set(self._handlers.keys()) | {CommandType.LOOP.value}

    def run(
        self,
        macro: ExecutableMacro,
        job_context: ExecutionJobContext,
        workflow_ir: Optional[WorkflowIR] = None,
    ) -> MacroExecutionReport:
        MacroValidator.validate_macro(macro, self.registered_commands, workflow_ir)
        logger = AuditLogger()
        step_map = {s.step_id: s for s in macro.steps}
        loop_child_ids: Set[str] = set()
        for s in macro.steps:
            if s.command == CommandType.LOOP.value:
                loop_child_ids.update(s.loop_step_ids)

        results: List[OperationResult] = []
        halt_reason: Optional[str] = None
        halt_status: Optional[ExecutionStatus] = None

        for step in macro.steps:
            if step.step_id in loop_child_ids:
                continue

            interrupted = self._check_interruption(job_context)
            if interrupted is not None:
                halt_status, halt_reason = interrupted
                self._mark_remaining_unexecuted(
                    macro.steps, results, loop_child_ids, halt_reason, logger
                )
                break

            if halt_status is not None:
                skip_res = self._make_skipped_result(
                    step, f"Skipped due to prior halt: {halt_reason}"
                )
                results.append(skip_res)
                logger.record_step(step, skip_res)
                continue

            if step.command == CommandType.LOOP.value:
                res = self._execute_loop(step, step_map, job_context, logger)
            else:
                res = self._execute_with_retry(step, job_context)

            results.append(res)
            logger.record_step(step, res)

            if res.status in {ExecutionStatus.STOPPED, ExecutionStatus.CANCELLED, ExecutionStatus.TIMEOUT}:
                halt_status = res.status
                halt_reason = res.error_message or f"Execution terminated with {res.status.value}"
            elif res.status in {ExecutionStatus.FAILED, ExecutionStatus.UNKNOWN}:
                if step.criticality == Criticality.MANDATORY and not step.safe_to_continue_verified:
                    halt_status = res.status
                    halt_reason = (
                        f"REQ-DSP-004: Mandatory step '{step.step_id}' ended with {res.status.value}"
                    )
                elif step.on_failure == FailurePolicy.ABORT:
                    halt_status = res.status
                    halt_reason = f"Step '{step.step_id}' policy ABORT triggered"

        final_pc_results: List[PostConditionResult] = []
        if halt_status is None:
            for pc in macro.final_post_conditions:
                if self._post_condition_evaluator is None:
                    final_pc_results.append(
                        PostConditionResult(
                            verified=False,
                            status=ExecutionStatus.UNKNOWN,
                            expected_value=pc.expected_value,
                            observed_value=None,
                            reason="REQ-FIN-005: No post_condition_evaluator available",
                        )
                    )
                else:
                    final_pc_results.append(self._post_condition_evaluator(pc))

        final_status, final_reason = self._aggregate_final_status(
            macro, results, loop_child_ids, final_pc_results, halt_status, halt_reason
        )
        logger.record_final(final_status, final_reason)
        return MacroExecutionReport(
            job_id=job_context.job_id,
            macro_id=macro.macro_id,
            final_status=final_status,
            reason=final_reason,
            step_results=results,
            final_post_condition_results=final_pc_results,
            audit_logs=logger.entries,
        )

    def _check_interruption(
        self, ctx: ExecutionJobContext
    ) -> Optional[Tuple[ExecutionStatus, str]]:
        if ctx.cancel_requested:
            return ExecutionStatus.CANCELLED, "REQ-DSP-006: Execution cancelled by caller"
        if ctx.stop_requested:
            return ExecutionStatus.STOPPED, "REQ-DSP-006: Execution stopped by user request"
        return None

    def _execute_with_retry(
        self, step: MacroStep, ctx: ExecutionJobContext
    ) -> OperationResult:
        handler = self._handlers.get(step.command)
        if handler is None:
            now = self._clock()
            return OperationResult(
                operation_id=step.step_id,
                command=step.command,
                status=ExecutionStatus.FAILED,
                started_at_ms=now,
                ended_at_ms=now,
                requested_params=dict(step.params),
                executed_params={},
                error_code="UNIMPLEMENTED_COMMAND",
                error_message=f"REQ-DSP-002: No handler for '{step.command}'",
                criticality=step.criticality,
            )

        max_attempts = max(1, step.retry_policy.max_attempts)
        retry_reasons: List[str] = []
        last_result: Optional[OperationResult] = None

        for attempt in range(1, max_attempts + 1):
            interrupted = self._check_interruption(ctx)
            if interrupted is not None:
                st, msg = interrupted
                now = self._clock()
                return OperationResult(
                    operation_id=step.step_id,
                    command=step.command,
                    status=st,
                    started_at_ms=now,
                    ended_at_ms=now,
                    requested_params=dict(step.params),
                    executed_params={},
                    error_code=st.value.upper(),
                    error_message=msg,
                    retry_count=attempt - 1,
                    retry_reasons=retry_reasons,
                    criticality=step.criticality,
                )

            res = handler(step)
            last_result = replace(
                res, retry_count=attempt - 1, retry_reasons=list(retry_reasons)
            )
            if last_result.status == ExecutionStatus.SUCCESS:
                return last_result

            if attempt >= max_attempts:
                break

            # 結果不明や非冪等操作は状態プローブで未実行が確認できない限り再試行を禁止する
            can_retry, block_reason = self._can_safely_retry(step, last_result)
            if not can_retry:
                retry_reasons.append(f"Retry blocked: {block_reason}")
                return replace(last_result, retry_reasons=list(retry_reasons))

            retry_reasons.append(
                f"Attempt {attempt} ended with {last_result.status.value} ({last_result.error_code})"
            )

        assert last_result is not None
        return last_result

    def _can_safely_retry(
        self, step: MacroStep, result: OperationResult
    ) -> Tuple[bool, str]:
        if result.status in {ExecutionStatus.STOPPED, ExecutionStatus.CANCELLED}:
            return False, "Stopped or cancelled operation cannot be retried"
        is_non_idempotent = step.side_effect in {
            SideEffectLevel.NON_IDEMPOTENT_MUTATION,
            SideEffectLevel.EXTERNAL_TRANSMISSION,
        }
        if result.status == ExecutionStatus.UNKNOWN:
            if not step.retry_policy.require_state_probe_on_unknown or self._state_probe is None:
                return False, "REQ-LOOP-003: Cannot retry UNKNOWN status without state probe"
            already_applied = self._state_probe(step)
            if already_applied is None or already_applied is True:
                return False, "REQ-LOOP-003: State probe could not confirm operation was unexecuted"
        if is_non_idempotent and not step.retry_policy.allow_non_idempotent_retry:
            if self._state_probe is None or self._state_probe(step) is not False:
                return (
                    False,
                    "REQ-LOOP-004: Non-idempotent operation requires state probe confirming unexecuted",
                )
        return True, "Safe to retry"

    def _execute_loop(
        self,
        loop_step: MacroStep,
        step_map: Dict[str, MacroStep],
        ctx: ExecutionJobContext,
        logger: AuditLogger,
    ) -> OperationResult:
        start = self._clock()
        max_iter = loop_step.max_iterations or 1
        cond_key = str(loop_step.params["condition_key"])
        iter_results: List[OperationResult] = []
        allowed_errors = set(loop_step.allowed_loop_error_codes)

        for idx in range(max_iter):
            interrupted = self._check_interruption(ctx)
            if interrupted is not None:
                st, msg = interrupted
                return OperationResult(
                    operation_id=loop_step.step_id,
                    command=loop_step.command,
                    status=st,
                    started_at_ms=start,
                    ended_at_ms=self._clock(),
                    requested_params=dict(loop_step.params),
                    executed_params={"completed_iterations": idx},
                    error_code=st.value.upper(),
                    error_message=msg,
                    iteration_results=iter_results,
                    criticality=loop_step.criticality,
                )

            if self._loop_condition_evaluator is not None:
                if not self._loop_condition_evaluator(cond_key, idx):
                    break

            for child_id in loop_step.loop_step_ids:
                child_step = step_map[child_id]
                child_res = self._execute_with_retry(child_step, ctx)
                iter_results.append(child_res)
                logger.record_step(child_step, child_res)

                if child_res.status != ExecutionStatus.SUCCESS:
                    err_allowed = (
                        child_res.error_code in allowed_errors
                        and (
                            child_step.criticality == Criticality.OPTIONAL
                            or child_step.safe_to_continue_verified
                        )
                    )
                    if not err_allowed:
                        return OperationResult(
                            operation_id=loop_step.step_id,
                            command=loop_step.command,
                            status=child_res.status,
                            started_at_ms=start,
                            ended_at_ms=self._clock(),
                            requested_params=dict(loop_step.params),
                            executed_params={"failed_at_iteration": idx, "failed_step": child_id},
                            error_code=child_res.error_code or "LOOP_ITERATION_FAILED",
                            error_message=child_res.error_message,
                            iteration_results=iter_results,
                            criticality=loop_step.criticality,
                        )

        loop_status = ExecutionStatus.SUCCESS
        if any(r.status != ExecutionStatus.SUCCESS for r in iter_results):
            loop_status = ExecutionStatus.PARTIAL_SUCCESS
        return OperationResult(
            operation_id=loop_step.step_id,
            command=loop_step.command,
            status=loop_status,
            started_at_ms=start,
            ended_at_ms=self._clock(),
            requested_params=dict(loop_step.params),
            executed_params={"iterations_run": len(iter_results)},
            observed_value={"status": loop_status.value},
            iteration_results=iter_results,
            criticality=loop_step.criticality,
        )

    def _make_skipped_result(self, step: MacroStep, reason: str) -> OperationResult:
        now = self._clock()
        return OperationResult(
            operation_id=step.step_id,
            command=step.command,
            status=ExecutionStatus.SKIPPED,
            started_at_ms=now,
            ended_at_ms=now,
            requested_params=dict(step.params),
            executed_params={},
            skipped_reason=reason,
            criticality=step.criticality,
            downstream_impact="Mandatory step not executed"
            if step.criticality == Criticality.MANDATORY
            else "Optional step skipped",
        )

    def _mark_remaining_unexecuted(
        self,
        all_steps: List[MacroStep],
        results: List[OperationResult],
        loop_child_ids: Set[str],
        reason: str,
        logger: AuditLogger,
    ) -> None:
        done_ids = {r.operation_id for r in results}
        for step in all_steps:
            if step.step_id in loop_child_ids or step.step_id in done_ids:
                continue
            sk = self._make_skipped_result(step, reason)
            results.append(sk)
            logger.record_step(step, sk)

    @staticmethod
    def _aggregate_final_status(
        macro: ExecutableMacro,
        results: List[OperationResult],
        loop_child_ids: Set[str],
        final_pc_results: List[PostConditionResult],
        halt_status: Optional[ExecutionStatus],
        halt_reason: Optional[str],
    ) -> Tuple[ExecutionStatus, str]:
        if halt_status in {
            ExecutionStatus.CANCELLED,
            ExecutionStatus.STOPPED,
            ExecutionStatus.TIMEOUT,
        }:
            return halt_status, halt_reason or f"Terminated with {halt_status.value}"

        result_by_id = {r.operation_id: r for r in results}
        for step in macro.steps:
            if step.step_id in loop_child_ids:
                continue
            r = result_by_id.get(step.step_id)
            if r is None:
                return (
                    ExecutionStatus.FAILED,
                    f"REQ-FIN-002: Step '{step.step_id}' was never executed",
                )
            if step.criticality == Criticality.MANDATORY:
                if r.status == ExecutionStatus.UNKNOWN:
                    return (
                        ExecutionStatus.UNKNOWN,
                        f"REQ-FIN-005: Mandatory step '{step.step_id}' ended in UNKNOWN state",
                    )
                if r.status not in {ExecutionStatus.SUCCESS, ExecutionStatus.PARTIAL_SUCCESS}:
                    return (
                        ExecutionStatus.FAILED,
                        r.error_message
                        or f"REQ-FIN-002: Mandatory step '{step.step_id}' did not succeed ({r.status.value})",
                    )

        for pc in final_pc_results:
            if pc.status == ExecutionStatus.UNKNOWN:
                return ExecutionStatus.UNKNOWN, pc.reason
            if not pc.verified:
                return ExecutionStatus.FAILED, f"REQ-FIN-003: Final post-condition failed: {pc.reason}"

        if any(
            r.status in {
                ExecutionStatus.FAILED,
                ExecutionStatus.SKIPPED,
                ExecutionStatus.UNKNOWN,
                ExecutionStatus.PARTIAL_SUCCESS,
            }
            for r in results
        ):
            return (
                ExecutionStatus.PARTIAL_SUCCESS,
                "REQ-FIN-004: All mandatory steps succeeded; one or more optional steps did not fully succeed",
            )

        return ExecutionStatus.SUCCESS, "REQ-FIN-002: All mandatory steps and post-conditions verified"
