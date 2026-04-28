import asyncio
import os
import uuid
from temporalio.client import Client

from app.config import Settings
from app.models import QuantReport
from app.backtest_runner import DEFAULT_EXPORT_PATH, load_backtest_report
from app.workflows import QuantRepairWorkflowV2


async def main():
    Settings.validate()

    client = await Client.connect("localhost:7233")

    # Try to load the latest backtest report from NinjaTrader's export.
    # If it doesn't exist yet, use a seed report so the pipeline has
    # something to diagnose on the first iteration.
    report_path = os.getenv("BACKTEST_REPORT_PATH", DEFAULT_EXPORT_PATH)
    result = load_backtest_report(report_path)

    if result.success and result.report is not None:
        initial_report = result.report
        print(f"Loaded existing report: PF={initial_report.profit_factor}, "
              f"trades={initial_report.total_trades}")
    else:
        print(f"No existing report at {report_path}, using seed values")
        initial_report = QuantReport(
            strategy_id="ModularStrategy",
            net_profit=-1850.0,
            profit_factor=0.82,
            sharpe_ratio=-0.31,
            max_drawdown=4200.0,
            win_rate=31.5,
            total_trades=19,
            session_perf_map={
                "LONDON": -300.0,
                "NY_OPEN": -1400.0,
                "NY_MIDDAY": -150.0,
            },
        )

    # repo_root points to where the .cs files live
    repo_root = os.getenv(
        "STRATEGY_REPO_ROOT",
        os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "ModularStrategy")),
    )

    payload = {
        "initial_report": initial_report.model_dump(),
        "repo_root": repo_root,
        "build_cmd": ["dotnet", "build", "-c", "Release"],
        "new_backtest_report_path": report_path,
        "max_iterations": 3,
    }

    result = await client.execute_workflow(
        QuantRepairWorkflowV2.run,
        payload,
        id=f"quant-repair-v2-{uuid.uuid4()}",
        task_queue=Settings.TEMPORAL_TASK_QUEUE,
    )

    print(result)


if __name__ == "__main__":
    asyncio.run(main())
