/**
 * 24h 受信状況（coverage）のバケット化（#457）。
 *
 * 24h を 96 バケット × 15 分に割り、各バケットの `received / expected`（expected = 900 / 期待周期）
 * から coverage を出して 3 段階（full / partial / none）に落とす。ここは **pure** で、描画部品
 * （`TelemetryCoverageBar`）はこの {@link CoverageBucket} 配列だけを受け取る。将来
 * `GET /telemetries/coverage?pointId=&window=24h&bucket=15m` がサーバー側で同じ配列を返すように
 * なったら、取得経路だけ差し替えれば描画はそのまま使える。
 *
 * Phase 1 はクライアント側で raw の受信時刻から数える。統合クエリ（`/telemetries/query`）の粒度は
 * raw / hour / day しか無く、集計行に件数（count）も載らないので、集計粒度から coverage は出せない。
 * そのため raw を取るのは **宣言周期から 24h の件数が小さいと分かるポイントだけ**
 * （{@link planCoverageFetch}）。5 秒周期のような高頻度ポイント（24h ≒ 17k 件）は raw を取らず、
 * coverage API を待つ。
 */

export const COVERAGE_BUCKET_SECONDS = 15 * 60;
export const COVERAGE_BUCKET_COUNT = 96;
export const COVERAGE_WINDOW_SECONDS =
  COVERAGE_BUCKET_SECONDS * COVERAGE_BUCKET_COUNT;

/**
 * クライアントで raw を数えてよい 24h 件数の上限。期待周期 43.2 秒以上のポイントがこれに収まる
 * （1 分周期 = 1,440 件）。
 */
export const MAX_RAW_COVERAGE_SAMPLES = 2000;

/** coverage がこの比率以上なら「受信できている」とみなす（ジッタで 1 件欠けた程度は full）。 */
export const FULL_COVERAGE_RATIO = 0.9;

export type CoverageLevel = "full" | "partial" | "none";

export type CoverageBucket = {
  /** バケット開始（ISO-8601, 含む）。 */
  start: string;
  /** バケット終了（ISO-8601, 含まない）。 */
  end: string;
  /** このバケット内で実際に受信した件数（表示用）。 */
  received: number;
  /** coverage の分母（look-back 区間に期待される件数）。 */
  expected: number;
  /** 0..1 に丸めた受信率。 */
  coverage: number;
  level: CoverageLevel;
};

export function coverageLevel(coverage: number): CoverageLevel {
  if (coverage >= FULL_COVERAGE_RATIO) return "full";
  if (coverage > 0) return "partial";
  return "none";
}

/**
 * 判定に使う look-back 秒数。期待周期がバケットより長いポイント（例: 1 時間周期）は 15 分バケットに
 * 1 件も来ないのが正常なので、各バケットの終端から「1 周期ぶん」遡って数える。バケット以下の周期なら
 * バケットそのもの。
 */
function lookbackSeconds(intervalSeconds: number, bucketSeconds: number) {
  return Math.max(bucketSeconds, intervalSeconds);
}

/** 昇順ソート済み配列で `value` 以上になる最初の添字。 */
function lowerBound(sorted: number[], value: number): number {
  let lo = 0;
  let hi = sorted.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (sorted[mid] < value) lo = mid + 1;
    else hi = mid;
  }
  return lo;
}

function countInRange(sorted: number[], from: number, to: number): number {
  return lowerBound(sorted, to) - lowerBound(sorted, from);
}

/**
 * 受信時刻の配列を 24h × 15 分の coverage バケット（古い順）にする。窓は `[windowEnd - 24h, windowEnd)`。
 * 窓外・解釈できない時刻は捨てる。ただし期待周期がバケットより長い場合、先頭バケットの look-back は
 * 窓の手前まで届くので、窓より前の受信も判定には数える（件数表示には数えない）。
 */
export function bucketCoverage({
  timestamps,
  windowEnd,
  intervalSeconds,
  bucketSeconds = COVERAGE_BUCKET_SECONDS,
  bucketCount = COVERAGE_BUCKET_COUNT,
}: {
  timestamps: readonly string[];
  windowEnd: Date;
  /** 期待更新周期（秒, sbco:interval）。正の値であること。 */
  intervalSeconds: number;
  bucketSeconds?: number;
  bucketCount?: number;
}): CoverageBucket[] {
  const bucketMs = bucketSeconds * 1000;
  const endMs = windowEnd.getTime();
  const startMs = endMs - bucketMs * bucketCount;
  const lookbackMs = lookbackSeconds(intervalSeconds, bucketSeconds) * 1000;
  const expected = lookbackMs / 1000 / intervalSeconds;

  const sorted = timestamps
    .map((t) => Date.parse(t))
    .filter((ms) => Number.isFinite(ms))
    .sort((a, b) => a - b);

  const buckets: CoverageBucket[] = [];
  for (let i = 0; i < bucketCount; i++) {
    const bStart = startMs + i * bucketMs;
    const bEnd = bStart + bucketMs;
    const received = countInRange(sorted, bStart, bEnd);
    const inLookback =
      lookbackMs === bucketMs
        ? received
        : countInRange(sorted, bEnd - lookbackMs, bEnd);
    const coverage = Math.min(1, inLookback / expected);
    buckets.push({
      start: new Date(bStart).toISOString(),
      end: new Date(bEnd).toISOString(),
      received,
      expected,
      coverage,
      level: coverageLevel(coverage),
    });
  }
  return buckets;
}

export type CoverageSummary = Record<CoverageLevel, number> & {
  /** 連続した none バケットの最長区間（分）。欠測なしなら 0。 */
  longestGapMinutes: number;
};

export function summarizeCoverage(
  buckets: readonly CoverageBucket[],
): CoverageSummary {
  const summary: CoverageSummary = {
    full: 0,
    partial: 0,
    none: 0,
    longestGapMinutes: 0,
  };
  let runMs = 0;
  for (const b of buckets) {
    summary[b.level]++;
    if (b.level === "none") {
      runMs += Date.parse(b.end) - Date.parse(b.start);
      summary.longestGapMinutes = Math.max(
        summary.longestGapMinutes,
        Math.round(runMs / 60000),
      );
    } else {
      runMs = 0;
    }
  }
  return summary;
}

export type CoverageFetchPlan =
  | { kind: "raw"; start: Date; end: Date }
  | { kind: "unavailable"; reason: "no-interval" | "too-dense" };

/**
 * coverage を出すためにどの範囲の raw を取るか（取ってよいか）を決める。期待周期が無ければ
 * expected が決まらないので出せない。宣言周期から見て 24h の件数が
 * {@link MAX_RAW_COVERAGE_SAMPLES} を超えるなら raw は取らない（高頻度ポイントの全件取得を防ぐ）。
 */
export function planCoverageFetch({
  intervalSeconds,
  windowEnd,
  maxRawSamples = MAX_RAW_COVERAGE_SAMPLES,
}: {
  intervalSeconds: number | null | undefined;
  windowEnd: Date;
  maxRawSamples?: number;
}): CoverageFetchPlan {
  if (
    intervalSeconds === null ||
    intervalSeconds === undefined ||
    !Number.isFinite(intervalSeconds) ||
    intervalSeconds <= 0
  ) {
    return { kind: "unavailable", reason: "no-interval" };
  }
  const lookback = lookbackSeconds(intervalSeconds, COVERAGE_BUCKET_SECONDS);
  const spanSeconds =
    COVERAGE_WINDOW_SECONDS + (lookback - COVERAGE_BUCKET_SECONDS);
  if (spanSeconds / intervalSeconds > maxRawSamples) {
    return { kind: "unavailable", reason: "too-dense" };
  }
  const end = new Date(windowEnd.getTime());
  const start = new Date(end.getTime() - spanSeconds * 1000);
  return { kind: "raw", start, end };
}
