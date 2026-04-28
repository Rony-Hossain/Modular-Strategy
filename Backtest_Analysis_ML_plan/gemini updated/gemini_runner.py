"""
Gemini runner — implements the LLMRunner interface.

Uses the google-genai SDK with native structured-output support (response_schema)
when prompt_json is called. Health check is a tiny 'say OK' probe.
"""

import json
from typing import Any, Type, TypeVar
from pydantic import BaseModel, ValidationError
from google import genai
from google.genai import types as genai_types

from app.base_runner import LLMRunner, _parse_json_loose

T = TypeVar("T", bound=BaseModel)


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

    async def prompt(self, prompt_text: str) -> str:
        cfg = self._build_config()
        try:
            response = await self.client.aio.models.generate_content(
                model=self.model_name,
                contents=prompt_text,
                config=cfg if cfg else None,
            )
        except Exception as e:
            raise RuntimeError(
                f"Gemini API call failed (model={self.model_name}): {e}"
            ) from e
        return (response.text or "").strip()

    async def prompt_json(self, prompt_text: str, schema: Type[T]) -> T:
        """
        Native structured output via response_schema. Falls back to loose
        text parsing if the SDK returns nothing in `parsed`.
        """
        cfg = self._build_config(response_schema=schema)
        try:
            response = await self.client.aio.models.generate_content(
                model=self.model_name,
                contents=prompt_text,
                config=cfg,
            )
        except Exception as e:
            raise RuntimeError(
                f"Gemini API call failed (model={self.model_name}): {e}"
            ) from e

        # Path 1: SDK already validated against the schema
        parsed = getattr(response, "parsed", None)
        if parsed is not None:
            # Pydantic class returned by SDK might already be the right type
            if isinstance(parsed, schema):
                return parsed
            # Otherwise it's a dict — validate manually
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
