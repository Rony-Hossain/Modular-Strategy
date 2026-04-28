import subprocess
from app.models import BuildResult


MAX_OUTPUT_LINES = 200


def _truncate(text: str, max_lines: int = MAX_OUTPUT_LINES) -> str:
    lines = text.splitlines()
    if len(lines) <= max_lines:
        return text
    return "\n".join(
        [f"... ({len(lines) - max_lines} lines truncated) ..."] + lines[-max_lines:]
    )


def build_project(build_cmd: list[str], cwd: str) -> BuildResult:
    try:
        result = subprocess.run(
            build_cmd,
            cwd=cwd,
            capture_output=True,
            text=True,
            timeout=600,
        )
        return BuildResult(
            success=result.returncode == 0,
            stdout=_truncate(result.stdout),
            stderr=_truncate(result.stderr),
        )
    except Exception as e:
        return BuildResult(
            success=False,
            stdout="",
            stderr=str(e),
        )
