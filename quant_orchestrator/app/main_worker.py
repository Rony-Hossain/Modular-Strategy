"""
Temporal worker — full ML Pipeline + Quant Repair (v3).

v3 changes:
- Registers fast_plan_ml_phase (skip-planning fast path)
- Registers validate_generated_script (local validator gate)
- Uses provider_health_check (multi-provider) instead of claude_health_check
- Sandbox stays disabled (PipelineLogger does file I/O)
- pydantic_data_converter for Pydantic v2 types
"""

import asyncio
from temporalio.client import Client
from temporalio.worker import Worker, UnsandboxedWorkflowRunner
from temporalio.contrib.pydantic import pydantic_data_converter
from app.config import Settings

# --- Quant Repair workflow (unchanged) ---
from app.activities import (
    claude_health_check,
    provider_health_check,
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
from app.workflows import QuantRepairWorkflowV2

# --- ML Pipeline workflow (v3) ---
from app.ml_activities import (
    # File system
    list_files,
    list_artifacts,
    list_scripts,
    read_plan_document,
    read_schema,
    read_strategy_config_snippet,
    # Intelligence sampling
    sample_artifact_contents,
    sample_dependent_scripts,
    # Planning (two paths in v3)
    claude_plan_ml_phase,
    fast_plan_ml_phase,
    # Writer + local validator + reviewer + runner + evaluator
    gemini_write_ml_script,
    validate_generated_script,
    claude_review_ml_script,
    save_and_run_ml_script,
    claude_evaluate_ml_output,
    # Confidence gate + proposal
    audit_phase_confidence,
    build_ml_proposal,
    write_proposal_to_disk,
)
from app.ml_workflow import MLPipelineWorkflow


async def main():
    Settings.validate()

    client = await Client.connect(
        "localhost:7233",
        data_converter=pydantic_data_converter,
    )

    worker = Worker(
        client,
        task_queue=Settings.TEMPORAL_TASK_QUEUE,
        workflows=[
            QuantRepairWorkflowV2,
            MLPipelineWorkflow,
        ],
        activities=[
            # Shared health checks
            claude_health_check,
            provider_health_check,

            # Quant Repair
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

            # ML Pipeline — file system
            list_files,
            list_artifacts,
            list_scripts,
            read_plan_document,
            read_schema,
            read_strategy_config_snippet,

            # ML Pipeline — intelligence sampling
            sample_artifact_contents,
            sample_dependent_scripts,

            # ML Pipeline — planning (Claude-edits OR fast path)
            claude_plan_ml_phase,
            fast_plan_ml_phase,

            # ML Pipeline — write → validate → review → run → evaluate
            gemini_write_ml_script,
            validate_generated_script,
            claude_review_ml_script,
            save_and_run_ml_script,
            claude_evaluate_ml_output,

            # ML Pipeline — finalization
            audit_phase_confidence,
            build_ml_proposal,
            write_proposal_to_disk,
        ],
        # Sandbox MUST be disabled — PipelineLogger uses open() for file I/O
        # which the workflow sandbox forbids. Acceptable for a local single-
        # worker pipeline where replay safety isn't a production concern.
        workflow_runner=UnsandboxedWorkflowRunner(),
    )

    print("=" * 60)
    print("ML Pipeline Worker — starting (v3)")
    print(f"  Task queue:        {Settings.TEMPORAL_TASK_QUEUE}")
    print(f"  Sandbox:           DISABLED (UnsandboxedWorkflowRunner)")
    print(f"  Data converter:    pydantic_data_converter")
    print(f"  Health check:      provider_health_check (multi-provider)")
    print(f"  Planner mode:      Gemini drafts → Claude edits")
    print(f"  Local validator:   ENABLED (script_validator)")
    print("=" * 60)
    print("Waiting for workflows... (Ctrl+C to stop)")
    print()

    await worker.run()


if __name__ == "__main__":
    asyncio.run(main())
