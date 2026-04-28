"""
Abstract LLM runner interface.

Every provider (Claude, Gemini, OpenAI, Mock) implements this contract.
Activities and the role registry only ever see this interface — they
never know which concrete provider they're talking to.
"""

import json
import re
from abc import ABC, abstractmethod
from typing import Any, Type, TypeVar
from pydantic import BaseModel, ValidationError

T = TypeVar("T", bound=BaseModel)


class LLMRunner(ABC):
    """Common interface for all LLM providers."""

    # Concrete subclasses set this for diagnostics / logging
    provider_name: str = "abstract"
    model_name: str = "unknown"

    @abstractmethod
    async def prompt(self, prompt_text: str, cached_prefix: str | None = None) -> str:
        """Send a prompt, return the raw text response.

        cached_prefix: optional stable text content to be cached by providers
        that support it (e.g. Anthropic prompt caching). Providers without
        cache support concatenate it with the prompt and ignore the marker.
        """
        ...

    async def prompt_json(
        self,
        prompt_text: str,
        schema: Type[T],
        cached_prefix: str | None = None,
    ) -> T:
        """
        Default JSON implementation built on top of `prompt`.
        Subclasses with native structured-output support (Gemini, OpenAI)
        should override for better reliability.
        """
        raw = await self.prompt(
            prompt_text
            + "\n\nReturn ONLY valid JSON. No markdown fences, "
            + "no backticks, no explanation.",
            cached_prefix=cached_prefix,
        )
        return _parse_json_loose(raw, schema)

    @abstractmethod
    async def health_check(self) -> bool:
        """Cheap probe: returns True if the provider is reachable and authenticated."""
        ...


def _parse_json_loose(raw: str, schema: Type[T]) -> T:
    """
    Forgiving JSON parser. Handles markdown fences, leading prose,
    and trailing commentary that LLMs sometimes emit despite instructions.
    """
    clean = raw.strip()

    # Strip markdown fences if present
    if "```" in clean:
        match = re.search(r"```(?:json)?\s*([\s\S]*?)```", clean)
        if match:
            clean = match.group(1).strip()

    # If still no luck, find the first { or [
    if not clean.startswith(("{", "[")):
        candidates = [clean.find(c) for c in "{[" if c in clean]
        candidates = [c for c in candidates if c != -1]
        if candidates:
            clean = clean[min(candidates):]

    try:
        return schema.model_validate(json.loads(clean))
    except (json.JSONDecodeError, ValidationError) as e:
        raise RuntimeError(
            f"LLM returned invalid structured output: {e}\nRaw:\n{raw[:2000]}"
        )
