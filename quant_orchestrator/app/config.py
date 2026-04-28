import os
from dotenv import load_dotenv

load_dotenv()


class Settings:
    GEMINI_KEYS = {
        "DATA_MINER": os.getenv("GEMINI_KEY_1"),
        "COMPILER": os.getenv("GEMINI_KEY_2"),
        "FALLBACK": os.getenv("GEMINI_KEY_3"),
    }

    CLAUDE_CMD = os.getenv(
        "CLAUDE_CMD",
        r"C:\Users\moham\AppData\Local\Volta\bin\claude.cmd",
    )

    TEMPORAL_TASK_QUEUE = os.getenv("TEMPORAL_TASK_QUEUE", "quant-task-queue")

    @staticmethod
    def validate() -> None:
        missing = [k for k, v in Settings.GEMINI_KEYS.items() if not v]
        if missing:
            raise RuntimeError(f"Missing Gemini keys: {missing}")
