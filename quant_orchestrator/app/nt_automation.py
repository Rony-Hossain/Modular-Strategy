"""
NinjaTrader Strategy Analyzer automation via pywinauto.

Prerequisite: NinjaTrader 8 must be open with the Strategy Analyzer window
already configured (strategy loaded, instrument/timeframe/date range set).
This module only clicks the "Run" button and waits for completion.
"""

import time
import logging
from typing import Optional

logger = logging.getLogger(__name__)

try:
    from pywinauto import Application, timings
    from pywinauto.findwindows import ElementNotFoundError
    HAS_PYWINAUTO = True
except ImportError:
    HAS_PYWINAUTO = False


class NTAutomationError(Exception):
    pass


def _find_nt_app() -> "Application":
    """Attach to the running NinjaTrader 8 process."""
    if not HAS_PYWINAUTO:
        raise NTAutomationError(
            "pywinauto is not installed. Run: pip install pywinauto"
        )
    try:
        return Application(backend="uia").connect(
            path="NinjaTrader.exe", timeout=10
        )
    except Exception:
        raise NTAutomationError(
            "Could not connect to NinjaTrader 8. Is it running?"
        )


def run_backtest(
    timeout_seconds: int = 300,
    poll_interval: float = 2.0,
) -> bool:
    """
    Click the Run button in NinjaTrader's Strategy Analyzer and wait
    for the backtest to complete.

    Returns True if the backtest completed, False on timeout.

    Requirements:
    - NinjaTrader 8 must be running
    - Strategy Analyzer window must be open with strategy already configured
    - The "Run" button must be visible (not greyed out)
    """
    app = _find_nt_app()

    # Find the Strategy Analyzer window
    try:
        sa_window = app.window(title_re=".*Strategy Analyzer.*", timeout=10)
    except Exception:
        raise NTAutomationError(
            "Strategy Analyzer window not found. Open it in NinjaTrader first."
        )

    # Find and click the Run button
    try:
        run_btn = sa_window.child_window(title="Run", control_type="Button")
        if not run_btn.exists(timeout=5):
            raise NTAutomationError("Run button not found in Strategy Analyzer.")

        run_btn.click_input()
        logger.info("Clicked 'Run' in Strategy Analyzer")
    except NTAutomationError:
        raise
    except Exception as e:
        raise NTAutomationError(f"Failed to click Run button: {e}")

    # Wait for the backtest to finish.
    # When running, the Run button text changes or becomes disabled.
    # When done, it becomes enabled again.
    logger.info("Waiting for backtest to complete...")
    deadline = time.time() + timeout_seconds

    # Give NT a moment to start the backtest
    time.sleep(3)

    while time.time() < deadline:
        try:
            # Check if Run button is enabled again (backtest finished)
            if run_btn.is_enabled():
                logger.info("Backtest completed")
                return True
        except Exception:
            pass
        time.sleep(poll_interval)

    logger.warning("Backtest timed out after %d seconds", timeout_seconds)
    return False
