"""
Trigger the ML Pipeline Builder workflow.

Claude reads the plan documents, sends Gemini the prompts to write each
analysis script, reviews the code, runs it, validates the output, and
moves to the next phase — fully autonomous.

Usage:
    python run_ml_pipeline.py                    # build all missing phases
    python run_ml_pipeline.py --phases 11 12 13  # build specific phases
    python run_ml_pipeline.py --force            # rebuild even if scripts exist
"""

import asyncio
import argparse
import os
import uuid
from temporalio.client import Client
from temporalio.contrib.pydantic import pydantic_data_converter
from app.config import Settings
from app.ml_workflow import MLPipelineWorkflow


async def main():
    Settings.validate()

    parser = argparse.ArgumentParser(description="ML Pipeline Builder")
    parser.add_argument(
        "--phases", nargs="*", default=None,
        help="Specific phase IDs to build (e.g. 11 12 13). Default: all.",
    )
    parser.add_argument(
        "--force", action="store_true",
        help="Rebuild scripts even if they already exist.",
    )
    args = parser.parse_args()

    client = await Client.connect(
        "localhost:7233",
        data_converter=pydantic_data_converter,
    )

    repo_root = os.getenv(
        "STRATEGY_REPO_ROOT",
        os.path.abspath(os.path.join(os.path.dirname(__file__), "..")),
    )
    plan_dir = os.path.join(repo_root, "Backtest_Analysis_ML_plan")

    payload = {
        "repo_root": repo_root,
        "plan_dir": plan_dir,
        "phase_ids": args.phases,
        "skip_existing": not args.force,
    }

    print(f"Repo root:  {repo_root}")
    print(f"Plan dir:   {plan_dir}")
    print(f"Phases:     {args.phases or 'all'}")
    print(f"Skip existing: {not args.force}")
    print()

    result = await client.execute_workflow(
        MLPipelineWorkflow.run,
        payload,
        id=f"ml-pipeline-{uuid.uuid4()}",
        task_queue=Settings.TEMPORAL_TASK_QUEUE,
    )

    print("\n=== ML Pipeline Result ===")
    print(f"Completed phases: {result['completed_phases']}")
    print()
    for entry in result["history"]:
        status = entry["status"]
        pid = entry["phase_id"]
        if status == "completed":
            summary = entry.get("evaluation_summary", "")
            print(f"  [PASS] Phase {pid}: {summary}")
            for insight in entry.get("key_insights", []):
                print(f"         INSIGHT: {insight}")
            for fu in entry.get("suggested_follow_ups", []):
                print(f"         FOLLOW-UP: {fu}")
        elif status == "skipped_already_exists":
            print(f"  [SKIP] Phase {pid}: script already exists")
        elif status == "skipped_unmet_dependencies":
            print(f"  [SKIP] Phase {pid}: unmet deps {entry['unmet']}")
        else:
            print(f"  [FAIL] Phase {pid}: {status}")
            if "issues" in entry:
                for issue in entry["issues"]:
                    print(f"         - {issue}")

    # Print accumulated insights summary
    insights = result.get("accumulated_insights", [])
    if insights:
        print(f"\n=== Accumulated Insights ({len(insights)} total) ===")
        for insight in insights:
            print(f"  {insight}")


if __name__ == "__main__":
    asyncio.run(main())
