"""Unit tests for etl.transform module."""

from datetime import datetime, timezone
import pytest
from etl.transform import cosmos_doc_to_row, is_warm


class TestCosmosDocToRow:
    def test_unix_timestamp_int(self):
        doc = {"_ts": 1700000000, "point_id": "p1", "building": "B1", "value": "23.5"}
        row = cosmos_doc_to_row(doc)
        assert row is not None
        assert row["time"] == datetime.fromtimestamp(1700000000, tz=timezone.utc)
        assert row["value"] == pytest.approx(23.5)

    def test_unix_timestamp_float(self):
        doc = {"_ts": 1700000000.5, "point_id": "p1"}
        row = cosmos_doc_to_row(doc)
        assert row is not None
        assert row["time"].tzinfo == timezone.utc

    def test_iso_datetime_with_tz(self):
        doc = {"datetime": "2024-01-15T10:30:00+00:00", "point_id": "p1"}
        row = cosmos_doc_to_row(doc)
        assert row is not None
        assert row["time"].year == 2024
        assert row["time"].tzinfo is not None

    def test_iso_datetime_with_z(self):
        doc = {"datetime": "2024-01-15T10:30:00Z", "point_id": "p1"}
        row = cosmos_doc_to_row(doc)
        assert row is not None
        assert row["time"].year == 2024

    def test_iso_datetime_with_fractional_seconds(self):
        doc = {"datetime": "2024-01-15T10:30:00.123456+00:00", "point_id": "p1"}
        row = cosmos_doc_to_row(doc)
        assert row is not None
        assert row["time"].microsecond == 123456

    def test_iso_datetime_without_tz_defaults_utc(self):
        doc = {"datetime": "2024-01-15T10:30:00", "point_id": "p1"}
        row = cosmos_doc_to_row(doc)
        assert row is not None
        assert row["time"].tzinfo == timezone.utc

    def test_datetime_field_takes_precedence_over_ts(self):
        doc = {"datetime": "2024-01-15T10:30:00Z", "_ts": 1700000000, "point_id": "p1"}
        row = cosmos_doc_to_row(doc)
        assert row is not None
        assert row["time"].year == 2024

    def test_missing_time_returns_none(self):
        doc = {"point_id": "p1", "value": 10}
        assert cosmos_doc_to_row(doc) is None

    def test_unparseable_datetime_returns_none(self):
        doc = {"datetime": "not-a-date", "point_id": "p1"}
        assert cosmos_doc_to_row(doc) is None

    def test_point_id_camel_case_fallback(self):
        doc = {"_ts": 1700000000, "pointId": "camelP1"}
        row = cosmos_doc_to_row(doc)
        assert row["point_id"] == "camelP1"

    def test_point_id_falls_back_to_id(self):
        doc = {"_ts": 1700000000, "id": "fallback-id"}
        row = cosmos_doc_to_row(doc)
        assert row["point_id"] == "fallback-id"

    def test_device_id_snake_case(self):
        doc = {"_ts": 1700000000, "device_id": "dev1"}
        row = cosmos_doc_to_row(doc)
        assert row["device_id"] == "dev1"

    def test_device_id_camel_case_fallback(self):
        doc = {"_ts": 1700000000, "deviceId": "dev2"}
        row = cosmos_doc_to_row(doc)
        assert row["device_id"] == "dev2"

    def test_value_numeric_string_converted(self):
        doc = {"_ts": 1700000000, "value": "42.7"}
        row = cosmos_doc_to_row(doc)
        assert row["value"] == pytest.approx(42.7)

    def test_value_none_when_not_numeric(self):
        doc = {"_ts": 1700000000, "value": "n/a"}
        row = cosmos_doc_to_row(doc)
        assert row["value"] is None

    def test_value_none_when_absent(self):
        doc = {"_ts": 1700000000}
        row = cosmos_doc_to_row(doc)
        assert row["value"] is None

    def test_data_string_parsed_as_json(self):
        doc = {"_ts": 1700000000, "data": '{"temp": 25}'}
        row = cosmos_doc_to_row(doc)
        assert row["data"] == {"temp": 25}

    def test_data_invalid_json_string_wrapped(self):
        doc = {"_ts": 1700000000, "data": "raw-value"}
        row = cosmos_doc_to_row(doc)
        assert row["data"] == {"raw": "raw-value"}

    def test_data_dict_preserved(self):
        doc = {"_ts": 1700000000, "data": {"key": "val"}}
        row = cosmos_doc_to_row(doc)
        assert row["data"] == {"key": "val"}

    def test_data_none_when_absent(self):
        doc = {"_ts": 1700000000}
        row = cosmos_doc_to_row(doc)
        assert row["data"] is None

    def test_building_field(self):
        doc = {"_ts": 1700000000, "building": "ENG2"}
        row = cosmos_doc_to_row(doc)
        assert row["building"] == "ENG2"

    def test_name_field(self):
        doc = {"_ts": 1700000000, "name": "temperature"}
        row = cosmos_doc_to_row(doc)
        assert row["name"] == "temperature"

    def test_id_field_preserved(self):
        doc = {"_ts": 1700000000, "id": "cosmos-doc-id"}
        row = cosmos_doc_to_row(doc)
        assert row["id"] == "cosmos-doc-id"

    def test_full_document(self):
        doc = {
            "id": "doc-123",
            "datetime": "2024-03-01T08:00:00Z",
            "point_id": "pt-001",
            "building": "ENG2",
            "device_id": "dev-xyz",
            "name": "indoor_temp",
            "value": 21.3,
            "data": {"unit": "celsius"},
        }
        row = cosmos_doc_to_row(doc)
        assert row is not None
        assert row["point_id"] == "pt-001"
        assert row["building"] == "ENG2"
        assert row["device_id"] == "dev-xyz"
        assert row["name"] == "indoor_temp"
        assert row["value"] == pytest.approx(21.3)
        assert row["data"] == {"unit": "celsius"}
        assert row["id"] == "doc-123"


class TestIsWarm:
    _CUTOFF = datetime(2024, 1, 1, tzinfo=timezone.utc)

    def test_ts_after_cutoff_is_warm(self):
        ts = datetime(2024, 6, 1, tzinfo=timezone.utc)
        assert is_warm(ts, self._CUTOFF) is True

    def test_ts_equal_cutoff_is_warm(self):
        assert is_warm(self._CUTOFF, self._CUTOFF) is True

    def test_ts_before_cutoff_is_cold(self):
        ts = datetime(2023, 12, 31, tzinfo=timezone.utc)
        assert is_warm(ts, self._CUTOFF) is False

    def test_naive_ts_treated_as_utc(self):
        ts = datetime(2024, 6, 1)  # no tzinfo
        assert is_warm(ts, self._CUTOFF) is True

    def test_naive_cutoff_treated_as_utc(self):
        ts = datetime(2024, 6, 1, tzinfo=timezone.utc)
        cutoff = datetime(2024, 1, 1)  # no tzinfo
        assert is_warm(ts, cutoff) is True
