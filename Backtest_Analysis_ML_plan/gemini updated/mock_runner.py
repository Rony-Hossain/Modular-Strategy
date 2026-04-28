"""
Mock runner — returns canned responses without calling any API.
Useful for testing the role registry, scheduler, and activity wiring
without burning tokens.
"""

from typing import Any, Type, TypeVar
from pydantic import BaseModel

from app.base_runner import LLMRunner

T = TypeVar("T", bound=BaseModel)


class MockRunner(LLMRunner):
    provider_name = "mock"

    def __init__(
        self,
        canned_text: str = '{"prompt": "stub", "phase_id": "stub", "script_name": "stub.py"}',
        **_unused: Any,
    ):
        self.canned_text = canned_text
        self.model_name = "mock-canned"

    async def prompt(self, prompt_text: str) -> str:
        return self.canned_text

    async def prompt_json(self, prompt_text: str, schema: Type[T]) -> T:
        # Try to construct a minimal valid instance from the schema's defaults
        try:
            return schema()  # works if all fields have defaults
        except Exception:
            # Otherwise parse the canned_text
            from app.base_runner import _parse_json_loose
            return _parse_json_loose(self.canned_text, schema)

    async def health_check(self) -> bool:
        return True
