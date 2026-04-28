"""
Role registry — maps pipeline roles (planner, reviewer, evaluator, etc.)
to concrete LLM runners based on roles.yaml.

Activities call `get_runner('planner')` and don't care which provider
they got. To swap providers, edit roles.yaml and restart the worker.

YAML schema:

    planner:
      provider: gemini             # one of: gemini, claude, mock
      model: gemini-3.1-pro-preview
      thinking: true               # optional, gemini only
      temperature: 0.3             # optional
      timeout_seconds: 420         # optional
      enabled: true                # optional, defaults true
      fallback:                    # optional — used if primary fails
        provider: gemini
        model: gemini-2.5-flash

    reviewer:
      provider: gemini
      model: gemini-2.5-flash
      timeout_seconds: 300

    health_check:
      providers: [gemini]          # which providers to ping at startup
"""

import os
from pathlib import Path
from typing import Any, Optional

import yaml

from app.config import Settings
from app.base_runner import LLMRunner


# ---------------------------------------------------------------------------
# Config loading
# ---------------------------------------------------------------------------

DEFAULT_ROLES_PATH = Path(__file__).parent / "roles.yaml"

_config_cache: Optional[dict] = None


def load_roles_config(path: str | Path | None = None) -> dict:
    """Load and cache the roles.yaml file. First call locks the path."""
    global _config_cache
    if _config_cache is not None:
        return _config_cache

    p = Path(path) if path else Path(
        os.getenv("ROLES_CONFIG_PATH", DEFAULT_ROLES_PATH)
    )
    if not p.exists():
        raise RuntimeError(
            f"roles.yaml not found at {p}. "
            f"Create it or set ROLES_CONFIG_PATH env var."
        )

    with open(p, "r", encoding="utf-8") as f:
        _config_cache = yaml.safe_load(f) or {}
    return _config_cache


def reset_config_cache() -> None:
    """For tests."""
    global _config_cache, _runner_cache
    _config_cache = None
    _runner_cache = {}


# ---------------------------------------------------------------------------
# Runner factory
# ---------------------------------------------------------------------------

_runner_cache: dict[str, LLMRunner] = {}


def _build_runner(spec: dict) -> LLMRunner:
    """
    Build a single runner from a role spec dict.
    Recognized keys: provider, model, timeout_seconds, thinking, temperature,
    api_key_env, plus any extras passed through as kwargs.
    """
    provider = spec.get("provider", "").lower()
    if not provider:
        raise RuntimeError(f"role spec missing 'provider': {spec}")

    # Filter out registry-internal keys before passing kwargs to constructor
    kwargs = {k: v for k, v in spec.items()
              if k not in {"provider", "fallback", "enabled", "api_key_env"}}

    if provider == "gemini":
        # Resolve API key — either from spec, env var named in spec, or
        # the legacy COMPILER/FALLBACK roles in Settings.GEMINI_KEYS
        api_key = (
            kwargs.pop("api_key", None)
            or os.getenv(spec.get("api_key_env", "GEMINI_API_KEY"))
            or Settings.GEMINI_KEYS.get("COMPILER")
        )
        from app.gemini_runner import GeminiRunner
        return GeminiRunner(api_key=api_key, **kwargs)

    if provider == "claude":
        from app.claude_runner import ClaudeRunner
        return ClaudeRunner(**kwargs)

    if provider == "mock":
        from app.mock_runner import MockRunner
        return MockRunner(**kwargs)

    raise RuntimeError(f"Unknown provider in role spec: {provider}")


def get_runner(role: str) -> LLMRunner:
    """
    Return the configured runner for a role. Cached per worker process.
    Raises if the role is missing, disabled, or unknown.
    """
    if role in _runner_cache:
        return _runner_cache[role]

    config = load_roles_config()
    if role not in config:
        raise RuntimeError(
            f"Role '{role}' not defined in roles.yaml. "
            f"Defined roles: {sorted(config.keys())}"
        )

    spec = config[role]
    if spec.get("enabled", True) is False:
        raise RuntimeError(f"Role '{role}' is disabled in roles.yaml")

    primary = _build_runner(spec)

    fallback_spec = spec.get("fallback")
    if fallback_spec:
        fallback = _build_runner(fallback_spec)
        runner = _FallbackWrapper(primary, fallback, role=role)
    else:
        runner = primary

    _runner_cache[role] = runner
    return runner


def get_role_config(role: str) -> dict:
    """Read a role's raw config (for diagnostics, logging, etc)."""
    return load_roles_config().get(role, {})


def list_active_providers() -> list[str]:
    """
    All distinct provider names across enabled roles. Used by the
    startup health check so we ping each provider at most once.
    """
    config = load_roles_config()
    providers: set[str] = set()
    for role, spec in config.items():
        if not isinstance(spec, dict):
            continue
        if spec.get("enabled", True) is False:
            continue
        if "provider" in spec:
            providers.add(spec["provider"].lower())
        # Fallback may use a different provider
        fb = spec.get("fallback") or {}
        if "provider" in fb:
            providers.add(fb["provider"].lower())
    return sorted(providers)


# ---------------------------------------------------------------------------
# Fallback wrapper
# ---------------------------------------------------------------------------

class _FallbackWrapper(LLMRunner):
    """
    Tries primary first; on RuntimeError (any failure) falls back to secondary.
    The wrapper exposes the LLMRunner interface so callers don't know it's
    wrapped.
    """

    def __init__(self, primary: LLMRunner, fallback: LLMRunner, role: str):
        self.primary = primary
        self.fallback = fallback
        self.role = role
        self.provider_name = f"{primary.provider_name}->fallback({fallback.provider_name})"
        self.model_name = primary.model_name

    async def prompt(self, prompt_text: str, cached_prefix: str | None = None) -> str:
        try:
            return await self.primary.prompt(prompt_text, cached_prefix=cached_prefix)
        except Exception as e:
            # Print so it's visible in worker logs
            print(
                f"[FALLBACK] role={self.role} primary={self.primary.provider_name}"
                f" failed: {type(e).__name__}: {str(e)[:200]} — trying fallback"
            )
            return await self.fallback.prompt(prompt_text, cached_prefix=cached_prefix)

    async def prompt_json(self, prompt_text, schema, cached_prefix: str | None = None):
        try:
            return await self.primary.prompt_json(
                prompt_text, schema, cached_prefix=cached_prefix,
            )
        except Exception as e:
            print(
                f"[FALLBACK] role={self.role} primary={self.primary.provider_name}"
                f" failed: {type(e).__name__}: {str(e)[:200]} — trying fallback"
            )
            return await self.fallback.prompt_json(
                prompt_text, schema, cached_prefix=cached_prefix,
            )

    async def health_check(self) -> bool:
        # Healthy if either backend is reachable
        try:
            if await self.primary.health_check():
                return True
        except Exception:
            pass
        try:
            return await self.fallback.health_check()
        except Exception:
            return False
