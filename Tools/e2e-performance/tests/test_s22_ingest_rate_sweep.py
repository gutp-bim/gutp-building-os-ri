import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import pytest  # noqa: E402

import s22_ingest_rate_sweep as s22  # noqa: E402


def test_parse_rates():
    assert s22.parse_rates("500, 1000,2000") == [500, 1000, 2000]
    with pytest.raises(ValueError):
        s22.parse_rates("0,100")
    with pytest.raises(ValueError):
        s22.parse_rates("")


def test_plan_and_split():
    assert s22.plan_streams(500, 3) == 1
    assert s22.plan_streams(12000, 3) == 3
    assert s22.plan_streams(100000, 3) == 3
    parts = s22.split_rate(10, 3)
    assert parts == [4, 3, 3] and sum(parts) == 10


def test_assess_pass():
    r = s22.assess_step(1000, 10, 10000, 10.0, 10000, 0, 100)
    assert r["pass"] and r["achieved_rate"] == 1000.0


def test_assess_each_failure():
    assert "achieved" in s22.assess_step(1000, 10, 6000, 10.0, 6000, 0, 0)["fail_reasons"][0]
    r = s22.assess_step(1000, 10, 10000, 10.0, 9000, 0, 0)
    assert any("loss" in x for x in r["fail_reasons"])
    r = s22.assess_step(1000, 10, 10000, 10.0, 10000, 100, 0)
    assert any("duplicates" in x for x in r["fail_reasons"])
    r = s22.assess_step(1000, 10, 10000, 10.0, 10000, 0, 6000)
    assert any("lag" in x for x in r["fail_reasons"])
    assert not s22.assess_step(1000, 10, 0, 10.0, 0, 0, 0)["pass"]


def test_percentile_and_proc_stat():
    assert s22.percentile([], .5) is None
    assert s22.percentile([1, 2, 3, 4], 1.0) == 4
    line = "123 (dotnet host) S 1 2 3 4 5 6 7 8 9 10 111 222 0 0 20"
    assert s22.parse_proc_stat(line) == 333
