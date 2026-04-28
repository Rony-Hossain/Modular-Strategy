from datetime import timedelta
from temporalio import workflow
from temporalio.common import RetryPolicy
from temporalio.exceptions import ApplicationError

with workflow.unsafe.imports_passed_through():
    from app.models import (
        QuantReport,
        DiagnosisAndInstructions,
        CodePatch,
        ReviewDecision,
        PatchApplyResult,
        BuildResult,
        BacktestResult,
        ComparisonDecision,
    )
    from app.activities import (
        claude_health_check,
        claude_diagnose_and_instruct,
        gemini_execute_patch,
        claude_review_patch,
        apply_code_patch,
        rollback_code_patch,
        compile_strategy,
        load_new_backtest_report,
        claude_compare_and_decide,
        read_source_file,
        trigger_backtest_and_load,
    )


# Claude calls are short prompts — tight timeout, no retry on RuntimeError
CLAUDE_POLICY = RetryPolicy(
    maximum_attempts=2,
    non_retryable_error_types=["RuntimeError"],
)

# Gemini does the heavy work — allow more retries
GEMINI_POLICY = RetryPolicy(
    maximum_attempts=3,
)

MAX_REVIEW_ATTEMPTS = 3


@workflow.defn
class QuantRepairWorkflowV2:
    @workflow.run
    async def run(self, payload: dict) -> dict:
        initial_report   = QuantReport.model_validate(payload["initial_report"])
        repo_root        = payload["repo_root"]
        build_cmd        = payload["build_cmd"]
        backtest_path    = payload["new_backtest_report_path"]
        max_iterations   = payload.get("max_iterations", 2)

        # --- sanity check: is Claude alive? ---
        claude_ok = await workflow.execute_activity(
            claude_health_check,
            start_to_close_timeout=timedelta(seconds=30),
        )
        if not claude_ok:
            raise ApplicationError(
                "Claude CLI unavailable or not authenticated",
                non_retryable=True,
            )

        current_report = initial_report
        history = []

        for iteration in range(1, max_iterations + 1):

            # ------------------------------------------------------------------
            # CLAUDE TOUCH 1 — diagnose + write Gemini's instructions
            # Now receives history so it won't repeat failed approaches.
            # ------------------------------------------------------------------
            instructions: DiagnosisAndInstructions = await workflow.execute_activity(
                claude_diagnose_and_instruct,
                args=[current_report, history],
                start_to_close_timeout=timedelta(minutes=2),
                heartbeat_timeout=timedelta(seconds=30),
                retry_policy=CLAUDE_POLICY,
            )

            # ------------------------------------------------------------------
            # Read the actual source file so Gemini sees real code
            # ------------------------------------------------------------------
            source_code: str = await workflow.execute_activity(
                read_source_file,
                args=[repo_root, instructions.target_file],
                start_to_close_timeout=timedelta(seconds=30),
            )

            # ------------------------------------------------------------------
            # GEMINI + CLAUDE REVIEW LOOP (up to 3 attempts)
            # If Claude rejects, feedback goes back to Gemini for a retry.
            # ------------------------------------------------------------------
            patch = None
            review = None
            feedback = None

            for attempt in range(1, MAX_REVIEW_ATTEMPTS + 1):
                patch: CodePatch = await workflow.execute_activity(
                    gemini_execute_patch,
                    args=[current_report, instructions, source_code, feedback],
                    start_to_close_timeout=timedelta(minutes=3),
                    retry_policy=GEMINI_POLICY,
                )

                review: ReviewDecision = await workflow.execute_activity(
                    claude_review_patch,
                    args=[instructions, patch],
                    start_to_close_timeout=timedelta(minutes=2),
                    heartbeat_timeout=timedelta(seconds=30),
                    retry_policy=CLAUDE_POLICY,
                )

                if review.approved:
                    break

                # Pass rejection feedback to Gemini for the next attempt
                feedback = f"{review.verdict} Risk flags: {', '.join(review.risk_flags)}. {review.notes}"

            if not review.approved:
                history.append({
                    "iteration": iteration,
                    "status": "rejected_by_claude_after_retries",
                    "instructions": instructions.model_dump(),
                    "patch": patch.model_dump(),
                    "review": review.model_dump(),
                    "attempts": MAX_REVIEW_ATTEMPTS,
                })
                continue  # let Claude try a different diagnosis

            # ------------------------------------------------------------------
            # Apply patch
            # ------------------------------------------------------------------
            patch_result: PatchApplyResult = await workflow.execute_activity(
                apply_code_patch,
                args=[patch, repo_root],
                start_to_close_timeout=timedelta(minutes=1),
            )

            if not patch_result.success:
                history.append({
                    "iteration": iteration,
                    "status": "patch_apply_failed",
                    "patch": patch.model_dump(),
                    "patch_result": patch_result.model_dump(),
                })
                continue

            # ------------------------------------------------------------------
            # Build
            # ------------------------------------------------------------------
            build_result: BuildResult = await workflow.execute_activity(
                compile_strategy,
                args=[repo_root, build_cmd],
                start_to_close_timeout=timedelta(minutes=10),
                retry_policy=RetryPolicy(maximum_attempts=2),
            )

            if not build_result.success:
                await self._rollback_if_possible(patch_result)
                history.append({
                    "iteration": iteration,
                    "status": "build_failed",
                    "patch": patch.model_dump(),
                    "build_result": build_result.model_dump(),
                })
                continue

            # ------------------------------------------------------------------
            # Trigger backtest in NinjaTrader and load the result
            # ------------------------------------------------------------------
            new_backtest: BacktestResult = await workflow.execute_activity(
                trigger_backtest_and_load,
                backtest_path,
                start_to_close_timeout=timedelta(minutes=10),
                heartbeat_timeout=timedelta(seconds=30),
            )

            if not new_backtest.success or new_backtest.report is None:
                await self._rollback_if_possible(patch_result)
                history.append({
                    "iteration": iteration,
                    "status": "backtest_load_failed",
                    "backtest_notes": new_backtest.notes,
                })
                continue

            # Hard guardrail — no AI needed, check trade count
            max_allowed = max(1, int(current_report.total_trades * (
                1 + instructions.max_trade_increase_pct / 100
            )))
            if new_backtest.report.total_trades > max_allowed:
                await self._rollback_if_possible(patch_result)
                history.append({
                    "iteration": iteration,
                    "status": "hard_reject_trade_frequency",
                    "before_trades": current_report.total_trades,
                    "after_trades": new_backtest.report.total_trades,
                    "max_allowed": max_allowed,
                })
                continue

            # ------------------------------------------------------------------
            # CLAUDE TOUCH 3 — compare before/after and decide
            # ------------------------------------------------------------------
            decision: ComparisonDecision = await workflow.execute_activity(
                claude_compare_and_decide,
                args=[current_report, new_backtest.report, instructions],
                start_to_close_timeout=timedelta(minutes=2),
                heartbeat_timeout=timedelta(seconds=30),
                retry_policy=CLAUDE_POLICY,
            )

            history.append({
                "iteration": iteration,
                "status": "completed",
                "instructions": instructions.model_dump(),
                "patch": patch.model_dump(),
                "review": review.model_dump(),
                "build": build_result.model_dump(),
                "decision": decision.model_dump(),
            })

            if decision.next_action == "ACCEPT":
                return {
                    "accepted": True,
                    "final_report": new_backtest.report.model_dump(),
                    "history": history,
                }

            if decision.next_action == "STOP":
                return {
                    "accepted": False,
                    "final_report": new_backtest.report.model_dump(),
                    "history": history,
                }

            current_report = new_backtest.report

        return {
            "accepted": False,
            "final_report": current_report.model_dump(),
            "history": history,
        }

    async def _rollback_if_possible(self, patch_result: PatchApplyResult) -> None:
        if patch_result.backup_path:
            await workflow.execute_activity(
                rollback_code_patch,
                args=[patch_result.file_path, patch_result.backup_path],
                start_to_close_timeout=timedelta(minutes=1),
            )
