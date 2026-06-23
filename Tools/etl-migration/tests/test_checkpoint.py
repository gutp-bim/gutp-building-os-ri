"""Unit tests for etl.checkpoint module."""

import json
from pathlib import Path
import pytest
from etl.checkpoint import Checkpoint


class TestCheckpoint:
    def test_load_returns_empty_when_file_missing(self, tmp_path):
        cp = Checkpoint(tmp_path / "checkpoint.json")
        assert cp.load() == {}

    def test_save_and_load_roundtrip(self, tmp_path):
        cp = Checkpoint(tmp_path / "checkpoint.json")
        cp.save({"continuation_token": "tok123", "last_ts": "2024-01-01"})
        state = cp.load()
        assert state["continuation_token"] == "tok123"
        assert state["last_ts"] == "2024-01-01"

    def test_get_continuation_token_none_when_missing(self, tmp_path):
        cp = Checkpoint(tmp_path / "cp.json")
        assert cp.get_continuation_token() is None

    def test_get_continuation_token_returns_value(self, tmp_path):
        cp = Checkpoint(tmp_path / "cp.json")
        cp.save({"continuation_token": "abc"})
        assert cp.get_continuation_token() == "abc"

    def test_get_last_ts_returns_value(self, tmp_path):
        cp = Checkpoint(tmp_path / "cp.json")
        cp.save({"last_ts": "2024-06-01"})
        assert cp.get_last_ts() == "2024-06-01"

    def test_update_merges_state(self, tmp_path):
        cp = Checkpoint(tmp_path / "cp.json")
        cp.save({"continuation_token": "old", "rows_migrated": 100})
        cp.update("new-tok", "2024-06-01", 250)
        state = cp.load()
        assert state["continuation_token"] == "new-tok"
        assert state["last_ts"] == "2024-06-01"
        assert state["rows_migrated"] == 250

    def test_update_preserves_existing_keys(self, tmp_path):
        cp = Checkpoint(tmp_path / "cp.json")
        cp.save({"extra": "keep-me"})
        cp.update("tok", None, 0)
        assert cp.load()["extra"] == "keep-me"

    def test_save_creates_parent_dirs(self, tmp_path):
        nested = tmp_path / "a" / "b" / "cp.json"
        cp = Checkpoint(nested)
        cp.save({"x": 1})
        assert nested.exists()

    def test_corrupted_file_returns_empty(self, tmp_path):
        f = tmp_path / "cp.json"
        f.write_text("NOT JSON", encoding="utf-8")
        cp = Checkpoint(f)
        assert cp.load() == {}

    def test_save_is_atomic(self, tmp_path):
        """tmp file should be renamed to final path (no .tmp leftover)."""
        cp = Checkpoint(tmp_path / "cp.json")
        cp.save({"k": "v"})
        tmp_files = list(tmp_path.glob("*.tmp"))
        assert len(tmp_files) == 0
