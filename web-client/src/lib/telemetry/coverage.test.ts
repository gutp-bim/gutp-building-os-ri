import { describe, expect, it } from "vitest";
import {
  bucketCoverage,
  bucketsFromCounts,
  COVERAGE_BUCKET_COUNT,
  coverageLevel,
  planCoverageFetch,
  summarizeCoverage,
} from "./coverage";

const END = new Date("2026-09-10T12:00:00Z");
const MIN = 60 * 1000;
const at = (minutesBeforeEnd: number) =>
  new Date(END.getTime() - minutesBeforeEnd * MIN).toISOString();

/** Samples every `intervalMin` minutes across the whole 24h window, minus the excluded ranges. */
function steady(
  intervalMin: number,
  skip: (minutesBeforeEnd: number) => boolean = () => false,
): string[] {
  const out: string[] = [];
  for (let m = intervalMin / 2; m < 24 * 60; m += intervalMin) {
    if (!skip(m)) out.push(at(m));
  }
  return out;
}

describe("coverageLevel", () => {
  it("maps a ratio onto three levels", () => {
    expect(coverageLevel(1)).toBe("full");
    expect(coverageLevel(0.9)).toBe("full");
    expect(coverageLevel(0.5)).toBe("partial");
    expect(coverageLevel(0.01)).toBe("partial");
    expect(coverageLevel(0)).toBe("none");
  });
});

describe("bucketCoverage (#457)", () => {
  it("splits 24h into 96 × 15 min buckets, oldest first, ending at windowEnd", () => {
    const buckets = bucketCoverage({
      timestamps: [],
      windowEnd: END,
      intervalSeconds: 60,
    });

    expect(buckets).toHaveLength(COVERAGE_BUCKET_COUNT);
    expect(buckets[0].start).toBe(at(24 * 60));
    expect(buckets[0].end).toBe(at(24 * 60 - 15));
    expect(buckets.at(-1)!.end).toBe(END.toISOString());
  });

  it("computes coverage as received / expected, expected = 900 / interval", () => {
    // 1 分周期 → 期待 15 件 / バケット。全区間きっちり受信。
    const buckets = bucketCoverage({
      timestamps: steady(1),
      windowEnd: END,
      intervalSeconds: 60,
    });

    for (const b of buckets) {
      expect(b.expected).toBe(15);
      expect(b.received).toBe(15);
      expect(b.coverage).toBe(1);
      expect(b.level).toBe("full");
    }
  });

  it("marks a gap as none and a half-filled bucket as partial", () => {
    // 直近 30〜45 分前を丸ごと欠測、45〜60 分前は前半だけ受信。
    const timestamps = steady(
      1,
      (m) => (m >= 30 && m < 45) || (m >= 45 && m < 52),
    );
    const buckets = bucketCoverage({
      timestamps,
      windowEnd: END,
      intervalSeconds: 60,
    });

    const gap = buckets[COVERAGE_BUCKET_COUNT - 3]; // [45, 30) 分前
    expect(gap.received).toBe(0);
    expect(gap.level).toBe("none");

    const half = buckets[COVERAGE_BUCKET_COUNT - 4]; // [60, 45) 分前
    expect(half.received).toBe(8);
    expect(half.level).toBe("partial");

    expect(buckets.at(-1)!.level).toBe("full");
  });

  it("clamps coverage to 1 when the device over-reports", () => {
    const buckets = bucketCoverage({
      timestamps: steady(0.5),
      windowEnd: END,
      intervalSeconds: 60,
    });
    expect(buckets[10].received).toBe(30);
    expect(buckets[10].coverage).toBe(1);
  });

  it("ignores samples outside the window and unparsable timestamps", () => {
    const buckets = bucketCoverage({
      timestamps: [
        at(24 * 60 + 1),
        new Date(END.getTime() + MIN).toISOString(),
        "not-a-date",
      ],
      windowEnd: END,
      intervalSeconds: 60,
    });
    expect(buckets.every((b) => b.received === 0)).toBe(true);
  });

  it("uses the interval as the look-back for points slower than a bucket, so they do not read as gaps", () => {
    // 1 時間周期: 1 バケット 15 分に 1 件も来ないのが正常。直近 1 時間に 1 件あれば満たしている。
    const timestamps = steady(60);
    const buckets = bucketCoverage({
      timestamps,
      windowEnd: END,
      intervalSeconds: 3600,
    });

    // 窓の頭 3 バケットは look-back が窓の外にはみ出すので受信済みとは言えない — 最初の 1 件以降は
    // 全部 full になる。
    const firstSampleBucket = buckets.findIndex((b) => b.received > 0);
    expect(
      buckets.slice(firstSampleBucket).every((b) => b.level === "full"),
    ).toBe(true);
    // expected は「1 周期に 1 件」に正規化されている。
    expect(buckets.at(-1)!.expected).toBe(1);
  });

  it("counts samples before the window start toward the first buckets' look-back", () => {
    const buckets = bucketCoverage({
      timestamps: [at(24 * 60 + 10)],
      windowEnd: END,
      intervalSeconds: 3600,
    });
    expect(buckets[0].level).toBe("full");
    expect(buckets[0].received).toBe(0); // 表示用の件数はバケット内だけ
    expect(buckets[5].level).toBe("none");
  });
});

describe("summarizeCoverage", () => {
  it("counts buckets per level and reports the longest run of missing buckets", () => {
    const timestamps = steady(
      1,
      (m) => (m >= 30 && m < 75) || (m >= 600 && m < 615),
    );
    const summary = summarizeCoverage(
      bucketCoverage({ timestamps, windowEnd: END, intervalSeconds: 60 }),
    );

    expect(summary.none).toBe(4);
    expect(summary.full).toBe(92);
    expect(summary.partial).toBe(0);
    expect(summary.longestGapMinutes).toBe(45);
  });

  it("reports no gap when every bucket is covered", () => {
    const summary = summarizeCoverage(
      bucketCoverage({
        timestamps: steady(1),
        windowEnd: END,
        intervalSeconds: 60,
      }),
    );
    expect(summary.longestGapMinutes).toBe(0);
  });
});

describe("planCoverageFetch", () => {
  it("fetches raw timestamps when the declared interval keeps 24h small", () => {
    const plan = planCoverageFetch({ intervalSeconds: 60, windowEnd: END });
    expect(plan.kind).toBe("raw");
    if (plan.kind !== "raw") return;
    expect(plan.end.toISOString()).toBe(END.toISOString());
    expect(plan.start.toISOString()).toBe(at(24 * 60));
  });

  it("extends the fetch start by the look-back for slow points", () => {
    const plan = planCoverageFetch({ intervalSeconds: 3600, windowEnd: END });
    expect(plan.kind).toBe("raw");
    if (plan.kind !== "raw") return;
    // 先頭バケットの look-back（1h）が窓の 45 分手前まではみ出す。
    expect(plan.start.toISOString()).toBe(at(24 * 60 + 45));
  });

  it("asks the server to count a fast point instead of fetching ~17k raw rows (#551)", () => {
    expect(planCoverageFetch({ intervalSeconds: 5, windowEnd: END })).toEqual({
      kind: "server",
      end: END,
    });
  });

  it("cannot compute coverage without an expected interval", () => {
    expect(
      planCoverageFetch({ intervalSeconds: null, windowEnd: END }),
    ).toEqual({
      kind: "unavailable",
      reason: "no-interval",
    });
    expect(planCoverageFetch({ intervalSeconds: 0, windowEnd: END })).toEqual({
      kind: "unavailable",
      reason: "no-interval",
    });
  });
});

describe("bucketsFromCounts (#551)", () => {
  const windowStart = new Date(END.getTime() - 24 * 60 * MIN);

  it("turns server counts into the same buckets bucketCoverage would draw", () => {
    // 5 s point: 180 expected per 15 min. Bucket 0 full, bucket 1 partial, the rest empty.
    const counts = Array.from({ length: COVERAGE_BUCKET_COUNT }, (_, i) =>
      i === 0 ? 180 : i === 1 ? 90 : 0,
    );

    const buckets = bucketsFromCounts({
      counts,
      windowStart,
      bucketSeconds: 900,
      intervalSeconds: 5,
    });

    expect(buckets).toHaveLength(COVERAGE_BUCKET_COUNT);
    expect(buckets[0]).toMatchObject({
      start: windowStart.toISOString(),
      received: 180,
      expected: 180,
      coverage: 1,
      level: "full",
    });
    expect(buckets[1]).toMatchObject({ received: 90, level: "partial" });
    expect(buckets[2].level).toBe("none");
    expect(buckets.at(-1)!.end).toBe(END.toISOString());
  });

  it("agrees with bucketCoverage on the same receipts", () => {
    const timestamps = [at(1), at(2), at(3), at(20), at(24 * 60 - 1)];
    const fromRaw = bucketCoverage({
      timestamps,
      windowEnd: END,
      intervalSeconds: 60,
    });
    const counts = fromRaw.map((b) => b.received);

    expect(
      bucketsFromCounts({
        counts,
        windowStart,
        bucketSeconds: 900,
        intervalSeconds: 60,
      }),
    ).toEqual(fromRaw);
  });

  it("caps coverage at 1 when more arrive than expected (jitter)", () => {
    const counts = Array.from({ length: COVERAGE_BUCKET_COUNT }, () => 200);
    const [b] = bucketsFromCounts({
      counts,
      windowStart,
      bucketSeconds: 900,
      intervalSeconds: 5,
    });
    expect(b.coverage).toBe(1);
  });
});
