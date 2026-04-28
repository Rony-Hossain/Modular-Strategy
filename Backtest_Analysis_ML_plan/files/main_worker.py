"""
Temporal worker — Stage 1 update.

Registers the new activities added in the Stage 1 refactor:
- list_files (generic file lister)
- list_scripts (the one the workflow was actually trying to call)
- audit_phase_confidence (confidence gate)
- build_ml_proposal (structured Phase 17 output)
- write_proposal_to_disk (deterministic JSON writer)
"""

import asyncio
from temporalio.client import Client
from temporalio.worker import Worker

from app.config import Settings

# --- Quant Repair workflow (unchanged) ---
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
from app.workflows import QuantRepairWorkflowV2

# --- ML Pipeline workflow (Stage 1 activities added) ---
from app.ml_activities import (
    # file system
    list_files,
    list_artifacts,
    list_scripts,
    read_plan_document,
    read_schema,
    read_strategy_config_snippet,
    # Claude + Gemini
    claude_plan_ml_phase,
    gemini_write_ml_script,
    claude_review_ml_script,
    save_and_run_ml_script,
    claude_evaluate_ml_output,
    # Stage 1 additions
    audit_phase_confidence,
    build_ml_proposal,
    write_proposal_to_disk,
)
from app.ml_workflow import MLPipelineWorkflow


async def main():
    Settings.validate()

    client = await Client.connect("localhost:7233")

    worker = Worker(
        client,
        task_queue=Settings.TEMPORAL_TASK_QUEUE,
        workflows=[
            QuantRepairWorkflowV2,
            MLPipelineWorkflow,
        ],
        activities=[
            # Shared
            claude_health_check,

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

            # ML Pipeline — core loop
            claude_plan_ml_phase,
            gemini_write_ml_script,
            claude_review_ml_script,
            save_and_run_ml_script,
            claude_evaluate_ml_output,

            # ML Pipeline — Stage 1 additions
            audit_phase_confidence,
            build_ml_proposal,
            write_proposal_to_disk,
        ],
    )

    await worker.run()


if __name__ == "__main__":
    asyncio.run(main())
