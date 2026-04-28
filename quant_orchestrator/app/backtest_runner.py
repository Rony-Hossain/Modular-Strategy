import json
import os
import time
import logging
from pathlib import Path
from app.models import BacktestResult, QuantReport
from app.nt_automation import run_backtest, NTAutomationError

logger = logging.getLogger(__name__)

# Default export path matches BacktestExporter.cs
DEFAULT_EXPORT_PATH = os.path.join(
    os.path.expanduser("~"), "Documents",
    "NinjaTrader 8", "exports", "backtest_report.json",
)


def load_backtest_report(report_json_path: str) -> BacktestResult:
    try:
        data = json.loads(Path(report_json_path).read_text(encoding="utf-8"))
        report = QuantReport.model_validate(data)

        return BacktestResult(
            success=True,
            report=report,
            raw_output_path=report_json_path,
            notes="Backtest report loaded successfully",
        )
    except Exception as e:
        return BacktestResult(
            success=False,
            report=None,
            raw_output_path=report_json_path,
            notes=str(e),
        )


def trigger_and_wait_for_backtest(
    report_json_path: str,
    nt_timeout_seconds: int = 300,
    file_wait_seconds: int = 30,
) -> BacktestResult:
    """
    End-to-end: click Run in Strategy Analyzer, wait for the export JSON
    to be updated, then load and return it.

    Steps:
    1. Record the current modified-time of the export file (if it exists)
    2. Click Run in NinjaTrader's Strategy Analyzer via pywinauto
    3. Wait for the file's modified-time to change (NinjaScript writes it on Terminated)
    4. Load and return the report
    """
    path = Path(report_json_path)

    # Step 1: snapshot the current modified time
    old_mtime = path.stat().st_mtime if path.exists() else 0

    # Step 2: click Run
    try:
        completed = run_backtest(timeout_seconds=nt_timeout_seconds)
        if not completed:
            return BacktestResult(
                success=False,
                report=None,
                raw_output_path=report_json_path,
                notes="NinjaTrader backtest timed out",
            )
    except NTAutomationError as e:
        return BacktestResult(
            success=False,
            report=None,
            raw_output_path=report_json_path,
            notes=f"NT automation failed: {e}",
        )

    # Step 3: wait for the file to be updated by BacktestExporter.cs
    deadline = time.time() + file_wait_seconds
    while time.time() < deadline:
        if path.exists() and path.stat().st_mtime > old_mtime:
            logger.info("Export file updated: %s", report_json_path)
            # Small delay to ensure the file write is fully flushed
            time.sleep(0.5)
            return load_backtest_report(report_json_path)
        time.sleep(1)

    # File never updated — backtest may have had zero trades
    if path.exists() and path.stat().st_mtime == old_mtime:
        return BacktestResult(
            success=False,
            report=None,
            raw_output_path=report_json_path,
            notes="Backtest finished but export file was not updated (zero trades?)",
        )

    return BacktestResult(
        success=False,
        report=None,
        raw_output_path=report_json_path,
        notes="Export file not found after backtest",
    )
