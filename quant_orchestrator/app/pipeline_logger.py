"""
Structured pipeline logger for the ML Pipeline.

Logs to both console (human-readable) and a JSON-lines file
(machine-parseable) so you can see exactly what Claude and Gemini
are doing at each step.

Usage inside activities:
    from app.pipeline_logger import PipelineLogger
    log = PipelineLogger.get()
    log.step("PLAN", phase_id, "Sending prompt to Claude", prompt_snippet="...")
    log.step_done("PLAN", phase_id, "Got GeminiScriptRequest", duration_s=12.3)
"""

import json
import logging
import os
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Optional


# ---------------------------------------------------------------------------
# Singleton logger — shared across all activities in the worker process
# ---------------------------------------------------------------------------

class PipelineLogger:
    _instance: Optional["PipelineLogger"] = None

    @classmethod
    def get(cls, log_dir: str | None = None) -> "PipelineLogger":
        if cls._instance is None:
            cls._instance = cls(log_dir or ".")
        return cls._instance

    @classmethod
    def init(cls, log_dir: str) -> "PipelineLogger":
        """(Re-)initialize with a specific log directory. Call once at startup."""
        cls._instance = cls(log_dir)
        return cls._instance

    def __init__(self, log_dir: str):
        self.log_dir = Path(log_dir)
        self.log_dir.mkdir(parents=True, exist_ok=True)

        ts = datetime.now().strftime("%Y-%m-%d_%H%M%S")
        self.jsonl_path = self.log_dir / f"pipeline_{ts}.jsonl"
        self.txt_path = self.log_dir / f"pipeline_{ts}.log"

        # Python logger for console output
        self._logger = logging.getLogger("ml_pipeline")
        self._logger.setLevel(logging.DEBUG)
        self._logger.propagate = False

        # Remove old handlers to avoid duplicates on re-init
        self._logger.handlers.clear()

        # Console handler — human-readable
        ch = logging.StreamHandler(sys.stdout)
        ch.setLevel(logging.INFO)
        ch.setFormatter(_ConsoleFormatter())
        self._logger.addHandler(ch)

        # File handler — detailed plain text
        fh = logging.FileHandler(str(self.txt_path), encoding="utf-8")
        fh.setLevel(logging.DEBUG)
        fh.setFormatter(logging.Formatter(
            "%(asctime)s | %(levelname)-5s | %(message)s",
            datefmt="%H:%M:%S",
        ))
        self._logger.addHandler(fh)

        self._timers: dict[str, float] = {}

        self._logger.info(f"Pipeline log: {self.txt_path}")
        self._logger.info(f"Pipeline JSONL: {self.jsonl_path}")

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    def phase_start(self, phase_id: str, script_name: str, is_micro: bool = False):
        tag = "MICRO-PHASE" if is_micro else "PHASE"
        self._logger.info(f"{'='*60}")
        self._logger.info(f"[{tag} {phase_id}] START — {script_name}")
        self._logger.info(f"{'='*60}")
        self._emit("phase_start", phase_id=phase_id, script_name=script_name,
                    is_micro=is_micro)
        self._timers[f"phase_{phase_id}"] = time.monotonic()

    def phase_end(self, phase_id: str, status: str, summary: str = ""):
        elapsed = self._elapsed(f"phase_{phase_id}")
        self._logger.info(
            f"[PHASE {phase_id}] {status} ({elapsed:.1f}s) — {summary[:120]}"
        )
        self._emit("phase_end", phase_id=phase_id, status=status,
                    duration_s=elapsed, summary=summary)

    def step(
        self,
        step_name: str,
        phase_id: str,
        message: str,
        prompt_snippet: str = "",
        extra: dict[str, Any] | None = None,
    ):
        """Log the START of a step (PLAN, WRITE, REVIEW, RUN, EVALUATE)."""
        self._logger.info(f"  [{step_name}] {phase_id} — {message}")
        if prompt_snippet:
            # Show first 200 chars of prompt in console, full in file
            self._logger.debug(f"    prompt: {prompt_snippet[:500]}")
        self._timers[f"{step_name}_{phase_id}"] = time.monotonic()
        self._emit("step_start", step=step_name, phase_id=phase_id,
                    message=message,
                    prompt_snippet=prompt_snippet[:1000] if prompt_snippet else "",
                    **(extra or {}))

    def step_done(
        self,
        step_name: str,
        phase_id: str,
        message: str,
        response_snippet: str = "",
        extra: dict[str, Any] | None = None,
    ):
        """Log the END of a step with timing."""
        elapsed = self._elapsed(f"{step_name}_{phase_id}")
        self._logger.info(
            f"  [{step_name}] {phase_id} — DONE ({elapsed:.1f}s) — {message[:150]}"
        )
        if response_snippet:
            self._logger.debug(f"    response: {response_snippet[:500]}")
        self._emit("step_done", step=step_name, phase_id=phase_id,
                    message=message, duration_s=elapsed,
                    response_snippet=response_snippet[:1000] if response_snippet else "",
                    **(extra or {}))

    def insight(self, phase_id: str, text: str):
        self._logger.info(f"  [INSIGHT] {phase_id} — {text[:200]}")
        self._emit("insight", phase_id=phase_id, text=text)

    def warning(self, phase_id: str, message: str):
        self._logger.warning(f"  [WARN] {phase_id} — {message}")
        self._emit("warning", phase_id=phase_id, message=message)

    def error(self, phase_id: str, message: str):
        self._logger.error(f"  [ERROR] {phase_id} — {message}")
        self._emit("error", phase_id=phase_id, message=message)

    def event(self, event_name: str, **kwargs):
        """Log a generic pipeline event (confidence audit, micro-phase insertion, etc.)."""
        detail = " | ".join(f"{k}={v}" for k, v in kwargs.items() if k != "event")
        self._logger.info(f"  [{event_name}] {detail[:200]}")
        self._emit(event_name, **kwargs)

    # ------------------------------------------------------------------
    # Internals
    # ------------------------------------------------------------------

    def _elapsed(self, key: str) -> float:
        start = self._timers.pop(key, None)
        if start is None:
            return 0.0
        return time.monotonic() - start

    def _emit(self, event: str, **data):
        """Append one JSON line to the JSONL log."""
        record = {
            "ts": datetime.now(timezone.utc).isoformat(),
            "event": event,
            **data,
        }
        try:
            with open(self.jsonl_path, "a", encoding="utf-8") as f:
                f.write(json.dumps(record, default=str) + "\n")
        except Exception:
            pass  # logging must never crash the pipeline


class _ConsoleFormatter(logging.Formatter):
    """Color-coded console output for readability."""

    COLORS = {
        "DEBUG": "\033[90m",     # gray
        "INFO": "\033[97m",      # white
        "WARNING": "\033[93m",   # yellow
        "ERROR": "\033[91m",     # red
    }
    RESET = "\033[0m"

    def format(self, record: logging.LogRecord) -> str:
        color = self.COLORS.get(record.levelname, "")
        ts = datetime.now().strftime("%H:%M:%S")
        return f"{color}{ts} {record.getMessage()}{self.RESET}"
