"""Unit tests for etl.loader_minio module."""

from datetime import datetime, timezone
import pytest
from etl.loader_minio import MinioLoader, _to_pa_ts


class TestToPaTs:
    def test_aware_datetime(self):
        dt = datetime(2024, 1, 1, 0, 0, 0, tzinfo=timezone.utc)
        result = _to_pa_ts(dt)
        assert result == int(dt.timestamp() * 1_000_000)

    def test_naive_datetime_treated_as_utc(self):
        dt_naive = datetime(2024, 1, 1, 0, 0, 0)
        dt_aware = datetime(2024, 1, 1, 0, 0, 0, tzinfo=timezone.utc)
        assert _to_pa_ts(dt_naive) == _to_pa_ts(dt_aware)

    def test_none_returns_none(self):
        assert _to_pa_ts(None) is None


class TestMinioLoaderWriteBatch:
    def test_write_batch_empty_raises(self, tmp_path):
        loader = MinioLoader("localhost:9000", "key", "secret", "bucket")
        with pytest.raises(ValueError):
            loader.write_batch([], "batch-001")

    def test_object_key_partition(self, mocker):
        """Key should contain year/month partition from first row's timestamp."""
        mock_client = mocker.MagicMock()
        mock_client.bucket_exists.return_value = True

        loader = MinioLoader.__new__(MinioLoader)
        loader._client = mock_client
        loader._bucket = "cold"
        loader._prefix = "telemetry"

        rows = [
            {
                "time": datetime(2023, 5, 10, tzinfo=timezone.utc),
                "point_id": "p1",
                "building": "B1",
                "device_id": None,
                "name": "temp",
                "value": 22.0,
                "data": None,
                "id": "doc1",
            }
        ]
        key = loader.write_batch(rows, "b001")
        assert "year=2023" in key
        assert "month=05" in key
        assert key.endswith("b001.parquet")

    def test_write_batch_calls_put_object(self, mocker):
        mock_client = mocker.MagicMock()
        mock_client.bucket_exists.return_value = True

        loader = MinioLoader.__new__(MinioLoader)
        loader._client = mock_client
        loader._bucket = "cold"
        loader._prefix = "telemetry"

        rows = [
            {
                "time": datetime(2024, 3, 1, tzinfo=timezone.utc),
                "point_id": "p1",
                "building": None,
                "device_id": None,
                "name": None,
                "value": None,
                "data": {"x": 1},
                "id": "id1",
            }
        ]
        loader.write_batch(rows, "batch-xyz")
        mock_client.put_object.assert_called_once()
        call_args = mock_client.put_object.call_args
        assert call_args[0][1].endswith("batch-xyz.parquet")
