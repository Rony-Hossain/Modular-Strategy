"""
Autonomous ML Pipeline Workflow — Stage 1 refactor.

Architecture changes vs. the previous linear version:

1. PRIORITY QUEUE instead of a static for-loop.
   - Pending phases live in a dict keyed by phase_id (insertion-ordered, deterministic).
   - Each iteration picks ready phases (deps satisfied), groups by parallel_group,
     and dispatches one group at a time.

2. PARALLEL DISPATCH via asyncio.gather(workflow.execute_activity(...)).
   - Phases with the same parallel_group (e.g. bug_hunt: 25, 26, 27) run concurrently.
   - Ungrouped phases run solo. Temporal handles determinism.

3. MICRO-PHASE INSERTION.
   - When Claude's evaluation returns proposed_micro_phase, we build a spec,
     insert it into pending, and amend the target phase's dependencies so it
     blocks until the micro-phase completes.

4. CONFIDENCE GATE.
   - Before any phase with requires_confidence_gate=True runs, we execute
     audit_phase_confidence. Low-confidence audits don't block the phase
     (the role doc says "flag as LOW_CONFIDENCE" not "stop") but the result
     flows into the proposal.

5. STRUCTURED PROPOSAL OUTPUT.
   - After Phase 17 (or any confidence-gated phase), we build an MLProposal
     via Claude and write it to disk as JSON.
"""

import asyncio
from datetime import timedelta
from typing import Dict, List, Set

from temporalio import workflow
from temporalio.common import RetryPolicy
from temporalio.exceptions import ApplicationError

with workflow.unsafe.imports_passed_through():
    from app.ml_models import (
        ConfidenceAudit,
        ConfidenceLevel,
        GeminiScriptRequest,
        MicroPhaseProposal,
        MLPhaseContext,
        MLPhaseSpec,
        MLProposal,
        PhaseEvaluation,
        PhaseResult,
        PhaseStatus,
        ScriptRunResult,
        ScriptReviewDecision,
        ML_PHASES,
    )
    from app.ml_activities import (
        audit_phase_confidence,
        build_ml_proposal,
        claude_evaluate_ml_output,
        claude_plan_ml_phase,
        claude_review_ml_script,
        gemini_write_ml_script,
        list_artifacts,
        list_scripts,
        read_plan_document,
        read_schema,
        read_strategy_config_snippet,
        save_and_run_ml_script,
        write_proposal_to_disk,
    )
    from app.activities import claude_health_check


CLAUDE_POLICY = RetryPolicy(
    maximum_attempts=2,
    non_retryable_error_types=["RuntimeError"],
)
GEMINI_POLICY = RetryPolicy(maximum_attempts=3)

MAX_REVIEW_ATTEMPTS = 3
MAX_RUN_RETRIES = 2


@workflow.defn
class MLPipelineWorkflow:
    """
    Autonomous ML pipeline. Reads plan docs, dispatches Gemini workers to
    write analysis scripts, reviews + runs + evaluates them, accumulates
    insights, and produces a structured MLProposal at the end.
    """

    @workflow.run
    async def run(self, payload: dict) -> dict:
        repo_root = payload["repo_root"]
        plan_dir = payload["plan_dir"]
        phase_ids = payload.get("phase_ids")
        skip_existing = payload.get("skip_existing", True)
        proposal_version = payload.get("proposal_version", "v1")
        proposal_output = payload.get(
            "proposal_output_path",
            f"{repo_root}/Analysis/artifacts/config_proposal.json",
        )

        # --- sanity check ---
        claude_ok = await workflow.execute_activity(
            claude_health_check,
            start_to_close_timeout=timedelta(seconds=30),
        )
        if not claude_ok:
            raise ApplicationError("Claude CLI unavailable", non_retryable=True)

        # --- resolve phases to run ---
        registry = {p.phase_id: p for p in ML_PHASES}
        if phase_ids:
            initial_phases = [registry[pid] for pid in phase_ids if pid in registry]
        else:
            initial_phases = list(ML_PHASES)

        # Pending = working set (insertion-ordered dict, deterministic)
        pending: Dict[str, MLPhaseSpec] = {p.phase_id: p for p in initial_phases}
        completed: Set[str] = set()
        failed: Set[str] = set()
        phase_results: List[PhaseResult] = []
        history: List[dict] = []
        accumulated_insights: List[str] = []
        proposal_written_path = None

        # --- shared context ---
        schema_content, config_snippet, existing_artifacts = await self._load_context(
            repo_root
        )

        # --- main scheduling loop ---
        iteration = 0
        max_iterations = 50  # hard safety cap to prevent infinite loops

        while pending and iteration < max_iterations:
            iteration += 1

            # 1. Find phases whose dependencies are all satisfied
            ready = self._find_ready(pending, completed)

            if not ready:
                # Either a dependency points to something not in pending/completed
                # (broken registry) or all remaining phases depend on failed ones.
                history.append({
                    "event": "no_ready_phases",
                    "pending": list(pending.keys()),
                    "completed": sorted(completed),
                    "failed": sorted(failed),
                })
                break

            # 2. Skip-existing check (the bug-fixed version — now uses list_scripts)
            if skip_existing:
                scripts_on_disk = await workflow.execute_activity(
                    list_scripts,
                    f"{repo_root}/Analysis/scripts",
                    start_to_close_timeout=timedelta(seconds=10),
                )
                to_skip = [p for p in ready if p.script_name in scripts_on_disk]
                for phase in to_skip:
                    completed.add(phase.phase_id)
                    pending.pop(phase.phase_id, None)
                    history.append({
                        "phase_id": phase.phase_id,
                        "status": "skipped_already_exists",
                    })
                ready = [p for p in ready if p.script_name not in scripts_on_disk]

            if not ready:
                continue  # all ready were skipped; re-scan

            # 3. Group by parallel_group
            group = self._pick_group(ready)

            # 4. Confidence-gated phases: run the audit first
            gated = [p for p in group if p.requires_confidence_gate]
            gate_audit = None
            if gated:
                gate_audit = await workflow.execute_activity(
                    audit_phase_confidence,
                    phase_results,
                    start_to_close_timeout=timedelta(seconds=30),
                )
                history.append({
                    "event": "confidence_audit",
                    "result": gate_audit.model_dump(),
                })

            # 5. Build phases (parallel if group has multiple members)
            accumulated_insights_text = (
                "\n".join(accumulated_insights) if accumulated_insights else None
            )

            if len(group) == 1:
                results = [await self._build_phase(
                    group[0], repo_root, plan_dir,
                    schema_content, config_snippet,
                    existing_artifacts, phase_results,
                    accumulated_insights_text,
                )]
            else:
                # Parallel dispatch via asyncio.gather — Temporal handles this
                # deterministically as long as activity calls are deterministic.
                workflow.logger.info(
                    f"Parallel dispatch: {[p.phase_id for p in group]}"
                )
                coros = [
                    self._build_phase(
                        p, repo_root, plan_dir,
                        schema_content, config_snippet,
                        existing_artifacts, phase_results,
                        accumulated_insights_text,
                    )
                    for p in group
                ]
                results = await asyncio.gather(*coros)

            # 6. Apply results: update completed/failed, insert micro-phases
            stop_requested = False
            for phase, result in zip(group, results):
                history.append(result)
                pending.pop(phase.phase_id, None)

                if result["status"] == "completed":
                    completed.add(phase.phase_id)
                    phase_results.append(self._result_to_structured(phase, result))

                    # Accumulate insights
                    for insight in result.get("key_insights", []):
                        accumulated_insights.append(f"[Phase {phase.phase_id}] {insight}")
                    for fu in result.get("suggested_follow_ups", []):
                        accumulated_insights.append(
                            f"[Phase {phase.phase_id} FOLLOW-UP] {fu}"
                        )

                    # Handle micro-phase insertion
                    micro = result.get("proposed_micro_phase")
                    if micro:
                        self._insert_micro_phase(pending, micro, phase.phase_id)
                        history.append({
                            "event": "micro_phase_inserted",
                            "triggered_by": phase.phase_id,
                            "micro_phase_id": micro["phase_id"],
                            "rationale": micro.get("rationale", ""),
                        })

                    # Refresh artifact list for downstream phases
                    existing_artifacts = await workflow.execute_activity(
                        list_artifacts,
                        f"{repo_root}/Analysis/artifacts",
                        start_to_close_timeout=timedelta(seconds=10),
                    )
                else:
                    failed.add(phase.phase_id)
                    if result.get("next_action") == "STOP":
                        stop_requested = True

            if stop_requested:
                break

        # --- Proposal stage: if any confidence-gated phase (e.g. Phase 17)
        # completed, build and write the MLProposal ---
        gated_phase_completed = any(
            registry[pid].requires_confidence_gate
            for pid in completed if pid in registry
        )

        if gated_phase_completed and phase_results:
            try:
                final_audit = await workflow.execute_activity(
                    audit_phase_confidence,
                    phase_results,
                    start_to_close_timeout=timedelta(seconds=30),
                )
                proposal = await workflow.execute_activity(
                    build_ml_proposal,
                    args=[phase_results, final_audit, proposal_version],
                    start_to_close_timeout=timedelta(minutes=4),
                    heartbeat_timeout=timedelta(seconds=30),
                    retry_policy=CLAUDE_POLICY,
                )
                proposal_written_path = await workflow.execute_activity(
                    write_proposal_to_disk,
                    args=[proposal, proposal_output],
                    start_to_close_timeout=timedelta(seconds=30),
                )
                history.append({
                    "event": "proposal_generated",
                    "path": proposal_written_path,
                    "confidence_level": proposal.confidence_level.value,
                    "weight_changes": len(proposal.weight_changes),
                    "veto_changes": len(proposal.veto_changes),
                    "bugs_to_fix": len(proposal.bugs_to_fix),
                })
            except Exception as e:
                history.append({
                    "event": "proposal_failed",
                    "error": str(e),
                })

        return {
            "completed_phases": sorted(completed),
            "failed_phases": sorted(failed),
            "history": history,
            "accumulated_insights": accumulated_insights,
            "proposal_path": proposal_written_path,
        }

    # -----------------------------------------------------------------------
    # Helpers
    # -----------------------------------------------------------------------

    def _find_ready(
        self, pending: Dict[str, MLPhaseSpec], completed: Set[str]
    ) -> List[MLPhaseSpec]:
        """Phases whose dependencies are all in completed."""
        return [
            p for p in pending.values()
            if all(dep in completed for dep in p.dependencies)
        ]

    def _pick_group(self, ready: List[MLPhaseSpec]) -> List[MLPhaseSpec]:
        """
        Pick one group to dispatch this iteration.
        - Phases with a parallel_group run together (all ready members of that group).
        - Ungrouped phases run solo.
        Preference: ungrouped phases first (so blocking foundations go through),
        then parallel groups in deterministic order.
        """
        solo = [p for p in ready if p.parallel_group is None]
        if solo:
            return [solo[0]]

        # All remaining are in parallel groups — pick the first group (deterministic)
        first_group = ready[0].parallel_group
        return [p for p in ready if p.parallel_group == first_group]

    def _insert_micro_phase(
        self,
        pending: Dict[str, MLPhaseSpec],
        micro_dict: dict,
        triggering_phase: str,
    ) -> None:
        """
        Insert a Claude-proposed micro-phase into pending, and amend the
        'insert_before' target's dependencies so it blocks until the
        micro-phase completes.
        """
        # micro_dict comes from Pydantic-serialized MicroPhaseProposal
        deps = micro_dict.get("dependencies") or [triggering_phase]
        insert_before = micro_dict.get("insert_before")

        spec = MLPhaseSpec(
            phase_id=micro_dict["phase_id"],
            script_name=micro_dict["script_name"],
            plan_file="",  # inline prompt instead
            dependencies=deps,
            is_micro_phase=True,
            inline_prompt=micro_dict["inline_prompt"],
        )
        pending[spec.phase_id] = spec

        # Amend the target phase's deps to include the micro-phase
        if insert_before and insert_before in pending:
            target = pending[insert_before]
            if spec.phase_id not in target.dependencies:
                new_deps = list(target.dependencies) + [spec.phase_id]
                pending[insert_before] = target.model_copy(
                    update={"dependencies": new_deps}
                )

    def _result_to_structured(
        self, phase: MLPhaseSpec, result: dict
    ) -> PhaseResult:
        """Convert a history dict into a structured PhaseResult for the audit."""
        confidence_str = result.get("confidence_level", "MEDIUM")
        try:
            confidence = ConfidenceLevel(confidence_str)
        except ValueError:
            confidence = ConfidenceLevel.MEDIUM

        return PhaseResult(
            phase_id=phase.phase_id,
            status=PhaseStatus.COMPLETED,
            script_name=phase.script_name,
            artifacts=result.get("artifacts", []),
            evaluation_summary=result.get("evaluation_summary", ""),
            key_insights=result.get("key_insights", []),
            suggested_follow_ups=result.get("suggested_follow_ups", []),
            confidence_level=confidence,
            confidence_rationale=result.get("confidence_rationale", ""),
        )

    async def _load_context(self, repo_root: str):
        schema = await workflow.execute_activity(
            read_schema,
            f"{repo_root}/Analysis/artifacts/schema.md",
            start_to_close_timeout=timedelta(seconds=10),
        )
        config = await workflow.execute_activity(
            read_strategy_config_snippet,
            f"{repo_root}/Analysis/strategy_config.py",
            start_to_close_timeout=timedelta(seconds=10),
        )
        artifacts = await workflow.execute_activity(
            list_artifacts,
            f"{repo_root}/Analysis/artifacts",
            start_to_close_timeout=timedelta(seconds=10),
        )
        return schema, config, artifacts

    # -----------------------------------------------------------------------
    # The per-phase build pipeline: plan → write → review → run → evaluate
    # -----------------------------------------------------------------------

    async def _build_phase(
        self,
        phase: MLPhaseSpec,
        repo_root: str,
        plan_dir: str,
        schema_content: str,
        config_snippet: str,
        existing_artifacts: list,
        phase_results: List[PhaseResult],
        accumulated_insights: str | None,
    ) -> dict:
        # Read plan document (unless this is an inline-prompt micro-phase)
        if phase.is_micro_phase and phase.inline_prompt:
            plan_content = phase.inline_prompt
        else:
            plan_content = await workflow.execute_activity(
                read_plan_document,
                f"{plan_dir}/{phase.plan_file}",
                start_to_close_timeout=timedelta(seconds=10),
            )

        prev_summary = (
            phase_results[-1].evaluation_summary if phase_results else None
        )

        retry_error = None

        for run_attempt in range(MAX_RUN_RETRIES + 1):
            # 1. Claude plans
            context = MLPhaseContext(
                phase_id=phase.phase_id,
                script_name=phase.script_name,
                plan_prompt=plan_content,
                schema_content=schema_content,
                existing_artifacts=existing_artifacts,
                strategy_config_snippet=config_snippet,
                repo_root=repo_root,
                previous_phase_summary=prev_summary,
                retry_error=retry_error,
                accumulated_insights=accumulated_insights,
                min_sample_size=phase.min_sample_size,
                is_micro_phase=phase.is_micro_phase,
            )

            request: GeminiScriptRequest = await workflow.execute_activity(
                claude_plan_ml_phase,
                context,
                start_to_close_timeout=timedelta(minutes=3),
                heartbeat_timeout=timedelta(seconds=30),
                retry_policy=CLAUDE_POLICY,
            )

            # 2. Gemini + Claude review (inner loop)
            script_content = None
            review = None
            feedback = None

            for _ in range(MAX_REVIEW_ATTEMPTS):
                script_content = await workflow.execute_activity(
                    gemini_write_ml_script,
                    args=[request, feedback],
                    start_to_close_timeout=timedelta(minutes=5),
                    retry_policy=GEMINI_POLICY,
                )

                review = await workflow.execute_activity(
                    claude_review_ml_script,
                    args=[request, script_content],
                    start_to_close_timeout=timedelta(minutes=3),
                    heartbeat_timeout=timedelta(seconds=30),
                    retry_policy=CLAUDE_POLICY,
                )

                if review.approved:
                    break

                feedback = (
                    f"{review.verdict} Flags: {', '.join(review.risk_flags)}. "
                    f"{review.notes}"
                )

            if not review.approved:
                return {
                    "phase_id": phase.phase_id,
                    "status": "rejected_by_review",
                    "review": review.model_dump(),
                    "next_action": "STOP",
                }

            # 3. Save + run
            run_result: ScriptRunResult = await workflow.execute_activity(
                save_and_run_ml_script,
                args=[phase.script_name, script_content, repo_root],
                start_to_close_timeout=timedelta(minutes=12),
                heartbeat_timeout=timedelta(seconds=30),
            )

            # 4. Claude evaluates
            evaluation: PhaseEvaluation = await workflow.execute_activity(
                claude_evaluate_ml_output,
                args=[
                    phase.phase_id,
                    phase.script_name,
                    run_result,
                    f"Phase {phase.phase_id} should produce analysis artifacts in Analysis/artifacts/",
                ],
                start_to_close_timeout=timedelta(minutes=3),
                heartbeat_timeout=timedelta(seconds=30),
                retry_policy=CLAUDE_POLICY,
            )

            if evaluation.passed:
                return {
                    "phase_id": phase.phase_id,
                    "status": "completed",
                    "artifacts": run_result.artifacts_produced,
                    "evaluation_summary": evaluation.summary,
                    "key_insights": evaluation.key_insights,
                    "suggested_follow_ups": evaluation.suggested_follow_ups,
                    "confidence_level": evaluation.confidence_level.value,
                    "confidence_rationale": evaluation.confidence_rationale,
                    "proposed_micro_phase": (
                        evaluation.proposed_micro_phase.model_dump()
                        if evaluation.proposed_micro_phase else None
                    ),
                    "next_action": "CONTINUE",
                }

            if evaluation.next_action == "STOP":
                return {
                    "phase_id": phase.phase_id,
                    "status": "failed_hard",
                    "issues": evaluation.issues,
                    "stdout": run_result.stdout[:1000],
                    "stderr": run_result.stderr[:1000],
                    "next_action": "STOP",
                }

            retry_error = run_result.stderr or "\n".join(evaluation.issues)

        return {
            "phase_id": phase.phase_id,
            "status": "failed_after_retries",
            "next_action": "STOP",
        }
