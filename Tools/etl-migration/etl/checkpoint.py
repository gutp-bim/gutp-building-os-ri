"""
Checkpoint: persists the last successfully processed CosmosDB continuation token
so that a restarted migration can resume without re-scanning from the beginning.
"""

from __future__ import annotations
import json
import os
from pathlib import Path
from typing import Any


class Checkpoint:
    """File-backed checkpoint for ETL continuation token."""

    def __init__(self, path: str | Path) -> None:
        self._path = Path(path)

    def load(self) -> dict[str, Any]:
        if not self._path.exists():
            return {}
        try:
            return json.loads(self._path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            return {}

    def save(self, state: dict[str, Any]) -> None:
        self._path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self._path.with_suffix(".tmp")
        tmp.write_text(json.dumps(state, default=str), encoding="utf-8")
        tmp.replace(self._path)  # atomic rename

    def get_continuation_token(self) -> str | None:
        return self.load().get("continuation_token")

    def get_last_ts(self) -> str | None:
        return self.load().get("last_ts")

    def update(self, continuation_token: str | None, last_ts: str | None, rows_migrated: int) -> None:
        state = self.load()
        if continuation_token is not None:
            state["continuation_token"] = continuation_token
        if last_ts is not None:
            state["last_ts"] = last_ts
        state["rows_migrated"] = rows_migrated
        self.save(state)
