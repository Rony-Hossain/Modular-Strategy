"""
Claude API runner — uses anthropic SDK with prompt caching and JSON-tool
structured output. Replaces the legacy CLI-subprocess runner.

Why this matters:
- API has separate billing from your Pro/Max subscription, so you can run
  the pipeline without burning subscription rate limits.
- Native prompt caching cuts input cost by ~90% on cached portions.
- Real exceptions (RateLimitError, APIError) instead of "Claude CLI call failed".
- Better latency: cached prompts process much faster.

Caching strategy:
- Caller can mark a prefix of the prompt as cacheable by passing
  `cached_prefix=...` to prompt() / prompt_json(). The runner sends it
  as a separate content block with cache_control={"type": "ephemeral"}.
- Cache lives 5 minutes by default (long enough to span a full pipeline run).
- Caller passes only what's STABLE across calls in the same run as the
  prefix: schema, role doc, strategy config. The dynamic parts (the
  specific phase prompt, accumulated insights for THIS phase) go in
  the regular prompt.
"""

import asyncio
import json
import os
from typing import Any, Optional, Type, TypeVar

from pydantic import BaseModel, ValidationError
from temporalio import activity as temporal_activity

import anthropic
from anthropic import AsyncAnthropic

from app.base_runner import LLMRunner

T = TypeVar("T", bound=BaseModel)


# Default model selection. Roles.yaml can override per-role.
DEFAULT_MODEL = "claude-sonnet-4-6"

# Minimum prompt size before caching is worthwhile. Anthropic charges a
# write-cost on the first call to populate the cache; below this the
# overhead exceeds the savings.
MIN_CACHE_TOKENS = 1024  # ~4kb of text


def _parse_json_loose(raw: str, schema: Type[T]) -> T:
    """
    Best-effort JSON parser for fallback when Claude returned text instead
    of a tool call. Strips markdown fences, finds the first JSON object,
    validates against the schema. Self-contained to avoid base_runner dep.
    """
    import re
    clean = (raw or "").strip()
    if "```" in clean:
        m = re.search(r"```(?:json)?\s*([\s\S]*?)```", clean)
        if m:
            clean = m.group(1).strip()
    if not clean.startswith(("{", "[")):
        idx = -1
        for ch in ("{", "["):
            i = clean.find(ch)
            if i != -1 and (idx == -1 or i < idx):
                idx = i
        if idx != -1:
            clean = clean[idx:]
    return schema.model_validate(json.loads(clean))


class ClaudeRunner(LLMRunner):
    provider_name = "claude"

    def __init__(
        self,
        timeout_seconds: int = 300,
        heartbeat_interval_seconds: int = 15,
        model: Optional[str] = None,
        api_key: Optional[str] = None,
        max_tokens: int = 4096,
        temperature: float = 1.0,
        thinking: bool = False,
        thinking_budget_tokens: int = 8000,
        **_unused: Any,
    ):
        self.model_name = model or os.getenv("ANTHROPIC_MODEL") or DEFAULT_MODEL
        self.timeout_seconds = timeout_seconds
        self.heartbeat_interval_seconds = heartbeat_interval_seconds
        self.max_tokens = max_tokens
        self.temperature = temperature
        self.thinking = thinking
        self.thinking_budget_tokens = thinking_budget_tokens

        key = api_key or os.getenv("ANTHROPIC_API_KEY")
        if not key:
            raise RuntimeError(
                "ANTHROPIC_API_KEY not set. Generate one at console.anthropic.com "
                "and add it to your .env file."
            )
        self.client = AsyncAnthropic(api_key=key, timeout=float(timeout_seconds))

    async def _heartbeat_loop(self) -> None:
        while True:
            await asyncio.sleep(self.heartbeat_interval_seconds)
            try:
                temporal_activity.heartbeat()
            except Exception:
                return

    async def health_check(self) -> bool:
        try:
            resp = await self.client.messages.create(
                model=self.model_name,
                max_tokens=16,
                messages=[{"role": "user", "content": "Reply with exactly: OK"}],
            )
            text = "".join(
                b.text for b in resp.content if hasattr(b, "text")
            ).upper()
            return "OK" in text
        except Exception:
            return False

    # -----------------------------------------------------------------------
    # Internal: build the message payload with optional caching
    # -----------------------------------------------------------------------

    def _build_messages(
        self,
        prompt: str,
        cached_prefix: Optional[str] = None,
    ) -> list[dict]:
        """
        Build the messages list. If cached_prefix is set, send it as a
        separate content block with cache_control marker.
        """
        if cached_prefix and len(cached_prefix) >= MIN_CACHE_TOKENS * 4:
            return [{
                "role": "user",
                "content": [
                    {
                        "type": "text",
                        "text": cached_prefix,
                        "cache_control": {"type": "ephemeral"},
                    },
                    {
                        "type": "text",
                        "text": prompt,
                    },
                ],
            }]
        # Caching not worthwhile — single text block
        full = (cached_prefix + "\n\n" + prompt) if cached_prefix else prompt
        return [{"role": "user", "content": full}]

    def _build_call_kwargs(self) -> dict:
        kwargs: dict[str, Any] = {
            "model": self.model_name,
            "max_tokens": self.max_tokens,
            "temperature": self.temperature,
        }
        if self.thinking:
            kwargs["thinking"] = {
                "type": "enabled",
                "budget_tokens": self.thinking_budget_tokens,
            }
            # Thinking requires temperature=1
            kwargs["temperature"] = 1.0
        return kwargs

    # -----------------------------------------------------------------------
    # Public: prompt() — text in, text out
    # -----------------------------------------------------------------------

    async def prompt(
        self,
        prompt_text: str,
        cached_prefix: Optional[str] = None,
    ) -> str:
        heartbeat_task = asyncio.create_task(self._heartbeat_loop())
        try:
            response = await self._call_with_retry(
                messages=self._build_messages(prompt_text, cached_prefix),
                **self._build_call_kwargs(),
            )
        finally:
            heartbeat_task.cancel()
            try:
                await heartbeat_task
            except asyncio.CancelledError:
                pass

        text = "".join(b.text for b in response.content if hasattr(b, "text"))
        return text.strip()

    # -----------------------------------------------------------------------
    # Public: prompt_json() — uses the "tool use" trick for clean JSON
    # -----------------------------------------------------------------------

    async def prompt_json(
        self,
        prompt_text: str,
        schema: Type[T],
        cached_prefix: Optional[str] = None,
    ) -> T:
        """
        Force structured output by asking Claude to call a tool whose
        input_schema is the target Pydantic schema. This is more reliable
        than prompt-engineering "return JSON" because the API guarantees
        validity at the schema level.
        """
        # Convert Pydantic schema → JSON Schema for the tool
        tool_schema = schema.model_json_schema()
        # Anthropic tools want "object" at the top
        if tool_schema.get("type") != "object":
            tool_schema = {
                "type": "object",
                "properties": tool_schema.get("properties", {}),
                "required": tool_schema.get("required", []),
            }

        tool_name = f"emit_{schema.__name__.lower()}"
        tool = {
            "name": tool_name,
            "description": f"Emit a structured {schema.__name__} result.",
            "input_schema": tool_schema,
        }

        kwargs = self._build_call_kwargs()
        kwargs["tools"] = [tool]
        kwargs["tool_choice"] = {"type": "tool", "name": tool_name}

        # Thinking + tool_choice forced isn't supported on all models; if
        # thinking is enabled we drop the forced choice and let the model
        # pick (it will still pick the only tool available).
        if self.thinking:
            kwargs["tool_choice"] = {"type": "auto"}

        heartbeat_task = asyncio.create_task(self._heartbeat_loop())
        try:
            response = await self._call_with_retry(
                messages=self._build_messages(prompt_text, cached_prefix),
                **kwargs,
            )
        finally:
            heartbeat_task.cancel()
            try:
                await heartbeat_task
            except asyncio.CancelledError:
                pass

        # Find the tool_use block
        for block in response.content:
            if getattr(block, "type", None) == "tool_use":
                try:
                    return schema.model_validate(block.input)
                except ValidationError as e:
                    raise RuntimeError(
                        f"Claude tool output failed schema validation: {e}\n"
                        f"Raw input: {json.dumps(block.input)[:1000]}"
                    )

        # Fallback: no tool was called, try to parse text content
        text = "".join(b.text for b in response.content if hasattr(b, "text"))
        try:
            return _parse_json_loose(text, schema)
        except Exception as e:
            raise RuntimeError(
                f"Claude did not emit tool call AND text parse failed: {e}\n"
                f"Stop reason: {response.stop_reason}\nText: {text[:500]}"
            )

    # -----------------------------------------------------------------------
    # Internal: API call with retry on transient errors
    # -----------------------------------------------------------------------

    async def _call_with_retry(self, **kwargs):
        """
        Retry up to 3 times on RateLimitError / APIStatusError 5xx.
        Exponential backoff. Other exceptions propagate immediately.
        """
        last_exc = None
        for attempt in range(3):
            try:
                return await self.client.messages.create(**kwargs)
            except anthropic.RateLimitError as e:
                last_exc = e
                wait = 5 * (2 ** attempt)
                print(f"[claude_runner] RateLimitError on attempt {attempt+1}, "
                      f"sleeping {wait}s before retry")
                await asyncio.sleep(wait)
            except anthropic.APIStatusError as e:
                if 500 <= e.status_code < 600:
                    last_exc = e
                    wait = 3 * (2 ** attempt)
                    print(f"[claude_runner] APIStatusError {e.status_code} "
                          f"on attempt {attempt+1}, sleeping {wait}s")
                    await asyncio.sleep(wait)
                else:
                    raise
        # Exhausted retries
        raise RuntimeError(
            f"Claude API call failed after 3 retries: {last_exc}"
        ) from last_exc
