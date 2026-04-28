"""
Local pre-validation for Gemini-generated analysis scripts.

Runs BEFORE Claude reviews the script. Catches the predictable failure
modes that don't need a $0.10 Claude call to detect:

- Syntax errors (the famous "[INPUT] Zone Hygiene Audit Initiated" bug)
- Missing print() sections that the prompt requires
- Missing minimum sample size enforcement
- Reading files that aren't in the available artifacts list
- TODOs, placeholders, or `pass` in live code paths

If a script fails any of these, we return early with a structured
ValidationFailure. The workflow uses this to:
  1. Skip the Claude review (saves a token-heavy call)
  2. Hand the script back to Gemini with concrete feedback
  3. Avoid accumulating bad scripts on disk

This is the highest-leverage layer in the pipeline. Most of the bugs
in last night's run would have been caught here.
"""

import ast
import re
from dataclasses import dataclass, field
from typing import List


@dataclass
class ValidationResult:
    passed: bool
    issues: List[str] = field(default_factory=list)
    warnings: List[str] = field(default_factory=list)

    @property
    def feedback_for_gemini(self) -> str:
        lines = []
        if self.issues:
            lines.append("MUST FIX (script will not run):")
            for issue in self.issues:
                lines.append(f"  - {issue}")
        if self.warnings:
            lines.append("SHOULD FIX (quality concerns):")
            for w in self.warnings:
                lines.append(f"  - {w}")
        return "\n".join(lines)


# ---------------------------------------------------------------------------
# Individual checks — each returns (issue_or_None, warning_or_None)
# ---------------------------------------------------------------------------

def check_syntax(script: str) -> tuple[str | None, str | None]:
    """Hard fail if the script isn't valid Python."""
    try:
        ast.parse(script)
        return None, None
    except SyntaxError as e:
        line_no = e.lineno or 0
        offending_line = ""
        if line_no:
            lines = script.splitlines()
            if 0 < line_no <= len(lines):
                offending_line = lines[line_no - 1].strip()
        return (
            f"SyntaxError at line {line_no}: {e.msg}. "
            f"Offending line: {offending_line!r}",
            None,
        )


def check_first_line_is_python(script: str) -> tuple[str | None, str | None]:
    """
    Catches the '[INPUT] ...' on line 1 bug specifically.
    The first non-empty, non-fence line must be valid Python.
    """
    valid_starters = (
        "#", "import ", "from ", "\"\"\"", "'''", "def ", "class ",
        "if ", "try:", "async ", "@", "_", "print(",
    )
    for line in script.splitlines():
        stripped = line.strip()
        if not stripped:
            continue
        if stripped.startswith("```"):
            continue
        # Found first real line
        if not stripped.startswith(valid_starters):
            # Could still be valid Python (e.g. `x = 5`) — let AST decide
            try:
                ast.parse(line)
                return None, None
            except SyntaxError:
                return (
                    f"First real line is not valid Python: {stripped[:120]!r}. "
                    f"Output must be raw Python source, not prose or a [TAG] header.",
                    None,
                )
        return None, None
    return ("Script appears empty or whitespace-only", None)


def check_required_print_sections(script: str) -> tuple[str | None, str | None]:
    """
    The prompt mandates print('[INPUT] ...'), print('[CHECK] ...'),
    print('[RESULT] ...'), print('[SAVED] ...').
    """
    required = ["[INPUT]", "[CHECK]", "[RESULT]", "[SAVED]"]
    missing = []
    for tag in required:
        # Look for the tag inside any print() call
        pattern = rf"print\s*\(\s*['\"f].*?{re.escape(tag)}"
        if not re.search(pattern, script):
            missing.append(tag)
    if missing:
        return (
            f"Missing required print sections: {missing}. "
            f"Each phase script must call print() with these tags.",
            None,
        )
    return None, None


def check_min_sample_size_enforcement(
    script: str, min_n: int = 30,
) -> tuple[str | None, str | None]:
    """
    Look for explicit n-threshold enforcement. Either a constant matching
    the threshold, or an inline comparison like 'if len(...) < 30'.

    This is a warning rather than a hard issue because some phases
    legitimately don't have a single n-gate (e.g. the proposal builder).
    But for analysis phases it's load-bearing.
    """
    patterns = [
        rf"MIN_N\s*=\s*\d+",
        rf"MIN_SAMPLE_SIZE\s*=\s*\d+",
        rf"n_min\s*=\s*\d+",
        rf"<\s*{min_n}\b",            # inline `if x < 30`
        rf">=\s*{min_n}\b",           # inline `if x >= 30`
        rf"INSUFFICIENT",             # the prompt asks scripts to flag this
        rf"sample_size",
    ]
    for p in patterns:
        if re.search(p, script):
            return None, None
    return (
        None,
        f"No minimum-sample-size enforcement found (expected n>={min_n} "
        f"check or 'INSUFFICIENT' flag). Findings on tiny samples may be noise.",
    )


def check_no_placeholders(script: str) -> tuple[str | None, str | None]:
    """No TODOs or empty `pass` blocks in code (allowed in docstrings)."""
    try:
        tree = ast.parse(script)
    except SyntaxError:
        # Already caught upstream; skip
        return None, None

    # Look for top-level `pass` (suspicious) or TODO/FIXME comments
    issues = []
    for line_no, line in enumerate(script.splitlines(), 1):
        s = line.strip()
        # Skip if inside a docstring — we approximate by looking for triple-quote on the line
        if s.startswith("#") and re.search(r"\b(TODO|FIXME|XXX)\b", s):
            issues.append(f"line {line_no}: {s[:80]}")

    # Find `pass` statements that are the entire body of a function
    for node in ast.walk(tree):
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
            if (len(node.body) == 1 and isinstance(node.body[0], ast.Pass)
                    and node.name not in {"__init__"}):
                issues.append(
                    f"function `{node.name}` has only `pass` (placeholder)"
                )

    if issues:
        return (
            f"Placeholder/TODO content found: {'; '.join(issues[:3])}. "
            f"All code paths must be implemented.",
            None,
        )
    return None, None


def check_input_files_exist(
    script: str, available_artifacts: List[str],
) -> tuple[str | None, str | None]:
    """
    Hard-reject if the script tries to read a parquet/csv/md/json that
    isn't in the available artifacts list. Catches Gemini hallucinations
    like reading `Log.csv` when the plan said only certain artifacts.
    """
    if not available_artifacts:
        return None, None

    # Find quoted file references like "foo.parquet" or 'bar.csv'
    pattern = r"['\"]([\w\-\./\\]+\.(?:parquet|csv|md|json))['\"]"
    referenced = set(re.findall(pattern, script))

    available_set = {a.lower() for a in available_artifacts}
    # Permit common output file names (the script writes these)
    output_pattern = re.compile(r"_(report|bugs|summary|output|result)\.")

    missing = []
    for ref in referenced:
        # Take just the basename
        base = ref.replace("\\", "/").split("/")[-1].lower()
        if base in available_set:
            continue
        if output_pattern.search(base):
            continue  # Likely an output file
        # Schema is fine to reference even if not in artifacts dir
        if base == "schema.md":
            continue
        # Strategy config files always allowed
        if "strategy_config" in base or "config." in base:
            continue
        missing.append(ref)

    if missing:
        return (
            None,  # Warning, not error — could be false positive
            f"Script references files not in available artifacts: {missing[:3]}. "
            f"Verify these are intentional outputs, not hallucinated inputs.",
        )
    return None, None


def check_output_to_artifacts_dir(script: str) -> tuple[str | None, str | None]:
    """The prompt requires saving outputs to Analysis/artifacts/."""
    if "artifacts" in script.lower():
        return None, None
    return (
        "No reference to 'artifacts' directory. Outputs must be saved "
        "to Analysis/artifacts/ per the pipeline spec.",
        None,
    )


def check_seeded_randomness(script: str) -> tuple[str | None, str | None]:
    """np.random.seed(42) wherever randomness appears."""
    uses_random = bool(re.search(
        r"np\.random|numpy\.random|random\.|\.sample\(|\.shuffle\(|RandomState",
        script,
    ))
    if not uses_random:
        return None, None
    if "seed(42)" in script or "np.random.seed" in script:
        return None, None
    return (
        None,
        "Script uses randomness but doesn't appear to seed it. "
        "Add `np.random.seed(42)` for reproducibility.",
    )


# ---------------------------------------------------------------------------
# Orchestrator — runs every check, returns aggregate result
# ---------------------------------------------------------------------------

def validate_script(
    script: str,
    available_artifacts: List[str] | None = None,
    min_sample_size: int = 30,
) -> ValidationResult:
    """Run every check. Hard issues block the run; warnings inform Claude review."""
    issues: List[str] = []
    warnings: List[str] = []

    checks = [
        ("syntax", lambda: check_syntax(script)),
        ("first_line", lambda: check_first_line_is_python(script)),
        ("print_sections", lambda: check_required_print_sections(script)),
        ("placeholders", lambda: check_no_placeholders(script)),
        ("output_dir", lambda: check_output_to_artifacts_dir(script)),
        ("min_n", lambda: check_min_sample_size_enforcement(script, min_sample_size)),
        ("randomness", lambda: check_seeded_randomness(script)),
        ("inputs", lambda: check_input_files_exist(script, available_artifacts or [])),
    ]

    for name, fn in checks:
        try:
            issue, warning = fn()
        except Exception as e:
            warnings.append(f"validation check {name} crashed: {e}")
            continue
        if issue:
            issues.append(issue)
        if warning:
            warnings.append(warning)

    return ValidationResult(
        passed=len(issues) == 0,
        issues=issues,
        warnings=warnings,
    )
