"""
Gemini runner — implements the LLMRunner interface.

Uses the google-genai SDK with native structured-output support (response_schema)
when prompt_json is called. Health check is a tiny 'say OK' probe.

v3.1 changes:
- 503 UNAVAILABLE retry with exponential backoff (20s, 40s) before escalating
  to Temporal's retry layer. Prevents burning all 3 Temporal retries on a
  transient Gemini load spike.
"""

import asyncio
import json
from typing import Any, Type, TypeVar
from pydantic import BaseModel, ValidationError
from google import genai
from google.genai import types as genai_types
from google.genai.errors import ServerError

from app.base_runner import LLMRunner, _parse_json_loose

T = TypeVar("T", bound=BaseModel)

# How many times to retry a 503 before giving up and letting Temporal retry
_MAX_503_RETRIES = 2
# Base wait in seconds — doubles each attempt: 20s, 40s
_RETRY_BASE_WAIT = 20


class GeminiRunner(LLMRunner):
    provider_name = "gemini"

    DEFAULT_MODEL = "gemini-2.5-flash"

    def __init__(
        self,
        api_key: str,
        model: str | None = None,
        timeout_seconds: int = 300,
        thinking: bool = False,
        temperature: float | None = None,
        **_unused: Any,  # tolerate extra kwargs from roles.yaml
    ):
        if not api_key:
            raise RuntimeError("GeminiRunner requires an api_key")
        self.client = genai.Client(api_key=api_key)
        self.model_name = model or self.DEFAULT_MODEL
        self.timeout_seconds = timeout_seconds
        self.thinking = thinking
        self.temperature = temperature

    # -----------------------------------------------------------------------
    # Config builder
    # -----------------------------------------------------------------------

    def _build_config(self, response_schema: Any | None = None) -> dict:
        """Build a generation_config dict for this call."""
        cfg: dict[str, Any] = {}
        if self.temperature is not None:
            cfg["temperature"] = self.temperature
        if response_schema is not None:
            cfg["response_mime_type"] = "application/json"
            cfg["response_schema"] = response_schema
        # Thinking config — Gemini 3.x uses `thinking_level` (low/high),
        # Gemini 2.5 used `thinking_budget`. We pick based on model name.
        if self.thinking:
            try:
                if "gemini-3" in (self.model_name or ""):
                    cfg["thinking_config"] = genai_types.ThinkingConfig(
                        thinking_level="high",
                    )
                else:
                    cfg["thinking_config"] = genai_types.ThinkingConfig(
                        thinking_budget=-1,  # dynamic
                    )
            except Exception:
                # Old SDK without ThinkingConfig — skip silently.
                pass
        return cfg

    # -----------------------------------------------------------------------
    # Internal: call Gemini with 503 backoff
    # -----------------------------------------------------------------------

    async def _generate_with_backoff(
        self,
        contents: str,
        cfg: dict,
    ) -> Any:
        """
        Call generate_content with automatic 503 retry + backoff.
        Retries _MAX_503_RETRIES times before raising RuntimeError.
        All other errors raise immediately.
        """
        last_exc: Exception | None = None
        for attempt in range(_MAX_503_RETRIES + 1):
            try:
                response = await self.client.aio.models.generate_content(
                    model=self.model_name,
                    contents=contents,
                    config=cfg if cfg else None,
                )
                return response
            except ServerError as e:
                if e.status_code == 503 and attempt < _MAX_503_RETRIES:
                    wait = _RETRY_BASE_WAIT * (attempt + 1)
                    print(
                        f"[GEMINI 503] model={self.model_name} attempt={attempt + 1}/"
                        f"{_MAX_503_RETRIES} — retrying in {wait}s "
                        f"(Gemini high demand, transient)"
                    )
                    await asyncio.sleep(wait)
                    last_exc = e
                    continue
                # Non-503 ServerError or out of retries — escalate
                raise RuntimeError(
                    f"Gemini API call failed (model={self.model_name}): {e}"
                ) from e
            except Exception as e:
                raise RuntimeError(
                    f"Gemini API call failed (model={self.model_name}): {e}"
                ) from e

        # Exhausted retries — escalate so Temporal can retry the activity
        raise RuntimeError(
            f"Gemini 503 after {_MAX_503_RETRIES} retries "
            f"(model={self.model_name}): {last_exc}"
        ) from last_exc

    # -----------------------------------------------------------------------
    # LLMRunner interface
    # -----------------------------------------------------------------------

    async def health_check(self) -> bool:
        try:
            response = await self.client.aio.models.generate_content(
                model=self.model_name,
                contents="Reply with exactly: OK",
            )
            text = (response.text or "").upper()
            return "OK" in text
        except Exception:
            return False

    async def prompt(self, prompt_text: str, cached_prefix: str | None = None) -> str:
        # Gemini API doesn't have ephemeral prompt caching like Anthropic;
        # we just concatenate. (Vertex AI's context caching is a different
        # SDK path and not worth the complexity for a local pipeline.)
        full = (cached_prefix + "\n\n" + prompt_text) if cached_prefix else prompt_text
        cfg = self._build_config()
        response = await self._generate_with_backoff(full, cfg)
        return (response.text or "").strip()

    async def prompt_json(
        self,
        prompt_text: str,
        schema: Type[T],
        cached_prefix: str | None = None,
    ) -> T:
        """
        Native structured output via response_schema. Falls back to loose
        text parsing if the SDK returns nothing in `parsed`.
        """
        full = (cached_prefix + "\n\n" + prompt_text) if cached_prefix else prompt_text
        cfg = self._build_config(response_schema=schema)
        response = await self._generate_with_backoff(full, cfg)

        # Path 1: SDK already validated against the schema
        parsed = getattr(response, "parsed", None)
        if parsed is not None:
            if isinstance(parsed, schema):
                return parsed
            try:
                if isinstance(parsed, dict):
                    return schema.model_validate(parsed)
            except ValidationError:
                pass  # fall through to loose parsing

        # Path 2: parse the raw text
        raw = (response.text or "").strip()
        if not raw:
            raise RuntimeError(
                f"Gemini returned empty response (model={self.model_name})"
            )
        return _parse_json_loose(raw, schema)