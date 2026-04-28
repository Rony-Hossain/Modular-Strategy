"""
Claude Code CLI runner — implements the LLMRunner interface.

Note: the Claude Code CLI does NOT accept --model or --effort flags. Those
kwargs are kept on the constructor for API compatibility (so role configs
can pass them safely) but are ignored when building the command. To control
which model the CLI uses, run `claude /config` interactively or set the
appropriate env var before starting the worker.
"""

import asyncio
import contextlib
from typing import Any
from temporalio import activity as temporal_activity

from app.config import Settings
from app.base_runner import LLMRunner


class ClaudeRunner(LLMRunner):
    provider_name = "claude"

    def __init__(
        self,
        timeout_seconds: int = 240,
        heartbeat_interval_seconds: int = 15,
        model: str | None = None,    # accepted for compat; not passed to CLI
        effort: str | None = None,   # accepted for compat; not passed to CLI
        **_unused: Any,              # tolerate extra kwargs from roles.yaml
    ):
        self.timeout_seconds = timeout_seconds
        self.heartbeat_interval_seconds = heartbeat_interval_seconds
        self.model = model
        self.effort = effort
        self.model_name = model or "default"

    async def _heartbeat_loop(self) -> None:
        while True:
            await asyncio.sleep(self.heartbeat_interval_seconds)
            try:
                temporal_activity.heartbeat()
            except Exception:
                # No active activity context; nothing to heartbeat.
                return

    async def health_check(self) -> bool:
        try:
            proc = await asyncio.create_subprocess_exec(
                "cmd.exe", "/c", Settings.CLAUDE_CMD, "-p",
                "Reply with exactly: OK",
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
            stdout, _ = await asyncio.wait_for(proc.communicate(), timeout=30)
            return (
                proc.returncode == 0
                and "OK" in stdout.decode("utf-8", errors="ignore").upper()
            )
        except Exception:
            return False

    def _build_cmd(self) -> list[str]:
        return ["cmd.exe", "/c", Settings.CLAUDE_CMD, "-p", "-"]

    async def prompt(self, prompt_text: str) -> str:
        cmd = self._build_cmd()
        proc = await asyncio.create_subprocess_exec(
            *cmd,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )
        heartbeat_task = asyncio.create_task(self._heartbeat_loop())
        try:
            stdout, stderr = await asyncio.wait_for(
                proc.communicate(input=prompt_text.encode("utf-8")),
                timeout=self.timeout_seconds,
            )
        finally:
            heartbeat_task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await heartbeat_task

        if proc.returncode != 0:
            stderr_text = stderr.decode("utf-8", errors="ignore").strip()
            stdout_text = stdout.decode("utf-8", errors="ignore").strip()
            raise RuntimeError(
                f"Claude CLI exited with code {proc.returncode}.\n"
                f"  cmd: {' '.join(cmd)}\n"
                f"  stderr: {stderr_text or '(empty)'}\n"
                f"  stdout: {stdout_text[:500] or '(empty)'}"
            )
        return stdout.decode("utf-8", errors="ignore").strip()
