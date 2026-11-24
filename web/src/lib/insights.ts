// What the Insights page works out from history vigil-core already serves: how a fan answers
// the temperature it follows, when in the day a metric runs high, when a pool will be full,
// and when a source stopped reporting. Pure, so all of it is unit-tested; the page draws it.
import type { SeriesPoints } from './types';

const HOUR = 3_600_000;
const DAY = 24 * HOUR;

// ---------------------------------------------------------------- a fan and its temperature

export interface Pair {
	time: number;
	x: number;
	y: number;
}

/** The points of two series that share an instant: history buckets every series alike, so most do. */
export function pairByTime(x: SeriesPoints, y: SeriesPoints): Pair[] {
	const at = new Map(x);
	return y.flatMap(([time, value]) => {
		const other = at.get(time);
		return other === undefined ? [] : [{ time, x: other, y: value }];
	});
}

/**
 * Pearson's r, or null when it would say nothing: under three pairs, or one side that never
 * moved (a fan held at one duty has no relationship to show, which is different from r = 0).
 */
export function correlation(pairs: Pair[]): number | null {
	if (pairs.length < 3) return null;
	const mean = (values: number[]) => values.reduce((sum, value) => sum + value, 0) / values.length;
	const mx = mean(pairs.map((pair) => pair.x));
	const my = mean(pairs.map((pair) => pair.y));
	let sxy = 0;
	let sxx = 0;
	let syy = 0;
	for (const { x, y } of pairs) {
		sxy += (x - mx) * (y - my);
		sxx += (x - mx) ** 2;
		syy += (y - my) ** 2;
	}
	return sxx === 0 || syy === 0 ? null : sxy / Math.sqrt(sxx * syy);
}

export interface Bin {
	x: number;
	/** The mean of y over the pairs whose x rounds to this bin. */
	y: number;
	count: number;
}

/** The mean y at each whole step of x, in x order: the curve a cloud of pairs traces. */
export function binned(pairs: Pair[], step = 1): Bin[] {
	const bins = new Map<number, { sum: number; count: number }>();
	for (const { x, y } of pairs) {
		const key = Math.round(x / step) * step;
		const bin = bins.get(key) ?? { sum: 0, count: 0 };
		bin.sum += y;
		bin.count += 1;
		bins.set(key, bin);
	}
	return [...bins].map(([x, { sum, count }]) => ({ x, y: sum / count, count })).sort((a, b) => a.x - b.x);
}

/** The share of a fan's readings a temperature has to be there for, to be what the fan follows. */
export const MIN_OVERLAP = 0.8;

/** A bin with fewer readings than this is one odd afternoon, and is left out of the curve. */
export const CURVE_MIN_READINGS = 3;

export interface FanResponse {
	/** The temperature series the fan's duty follows most closely. */
	follows: string;
	r: number;
	pairs: Pair[];
	curve: Bin[];
}

/**
 * Which of `candidates` a fan's duty tracks, judged by correlation, and the pairs behind it.
 * vigild's curves are not published, so this is read off what the fan did. Null when the duty
 * never moved or nothing overlaps.
 */
export function fanResponse(duty: SeriesPoints, temperatures: Record<string, SeriesPoints>, candidates: string[]): FanResponse | null {
	let best: FanResponse | null = null;
	for (const id of candidates) {
		const pairs = pairByTime(temperatures[id] ?? [], duty);
		// A sensor seen for a fraction of the window can correlate with anything by chance.
		if (pairs.length < duty.length * MIN_OVERLAP) continue;
		const r = correlation(pairs);
		if (r !== null && (best === null || r > best.r)) {
			best = { follows: id, r, pairs, curve: binned(pairs).filter((bin) => bin.count >= CURVE_MIN_READINGS) };
		}
	}
	return best;
}

// ---------------------------------------------------------------- the day's rhythm

export interface Rhythm {
	/** Local midnight of each day, oldest first. */
	days: number[];
	/** How many slots a day is cut into. */
	slots: number;
	/** `cells[day][slot]`: the mean over that slot, or null where there is no reading. */
	cells: (number | null)[][];
	min: number;
	max: number;
}

/** Local midnight of the day `time` falls in. The viewer's clock: a nightly job is nightly to them. */
export function dayStart(time: number): number {
	const date = new Date(time);
	return new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
}

/**
 * A metric as a grid of days by time of day. Null when there is nothing to draw. A day that a
 * clock change made 23 or 25 hours long keeps 24 hours' worth of slots: the odd hour lands in
 * its neighbour's slot, which is what the wall clock said.
 */
export function rhythm(points: SeriesPoints, slotHours: number): Rhythm | null {
	if (points.length === 0) return null;
	const slots = Math.round(24 / slotHours);
	const sums = new Map<number, { sum: number; count: number }[]>();
	for (const [time, value] of points) {
		const day = dayStart(time);
		const slot = Math.min(slots - 1, Math.floor(new Date(time).getHours() / slotHours));
		const row = sums.get(day) ?? Array.from({ length: slots }, () => ({ sum: 0, count: 0 }));
		row[slot].sum += value;
		row[slot].count += 1;
		sums.set(day, row);
	}

	const days = [...sums.keys()].sort((a, b) => a - b);
	const cells = days.map((day) => (sums.get(day) ?? []).map(({ sum, count }) => (count === 0 ? null : sum / count)));
	const known = cells.flat().filter((cell): cell is number => cell !== null);
	return { days, slots, cells, min: Math.min(...known), max: Math.max(...known) };
}

// ---------------------------------------------------------------- when a pool is full

/** The fill levels ZFS slows down at, which is what vigil-core warns and goes critical on. */
export const FILL_LEVELS = [80, 90] as const;

export interface Forecast {
	percent: number;
	/** Percentage points a day, by least squares over the window. Zero or below: not growing. */
	perDay: number;
	/** When each of FILL_LEVELS is reached at this rate; null when never, or already past. */
	reaches: (number | null)[];
	/** How many days of readings the rate rests on. */
	basisDays: number;
}

/** Fewer days than this and a rate is a guess about one afternoon. */
export const FORECAST_MIN_DAYS = 2;

/** A pool's fill, its rate of change, and when it reaches each level. Null without enough history. */
export function forecast(points: SeriesPoints): Forecast | null {
	if (points.length < 3) return null;
	const first = points[0][0];
	const last = points[points.length - 1];
	const basisDays = (last[0] - first) / DAY;
	if (basisDays < FORECAST_MIN_DAYS) return null;

	const xs = points.map(([time]) => (time - first) / DAY);
	const ys = points.map(([, value]) => value);
	const mx = xs.reduce((sum, x) => sum + x, 0) / xs.length;
	const my = ys.reduce((sum, y) => sum + y, 0) / ys.length;
	let sxy = 0;
	let sxx = 0;
	xs.forEach((x, i) => {
		sxy += (x - mx) * (ys[i] - my);
		sxx += (x - mx) ** 2;
	});
	const perDay = sxx === 0 ? 0 : sxy / sxx;

	const percent = last[1];
	const reaches = FILL_LEVELS.map((level) => (perDay > 0 && percent < level ? last[0] + ((level - percent) / perDay) * DAY : null));
	return { percent, perDay, reaches, basisDays };
}

// ---------------------------------------------------------------- when a source went quiet

export interface Gap {
	from: number;
	to: number;
}

/** A series is bucketed, so its points are one bucket apart; this many buckets apart is a gap. */
export const GAP_BUCKETS = 2.5;

/**
 * The stretches of `[from, to]` in which a series has no point, the start and the end
 * included: a source that began reporting halfway through the window was quiet before that.
 */
export function gaps(points: SeriesPoints, from: number, to: number, bucketMs: number): Gap[] {
	const limit = GAP_BUCKETS * bucketMs;
	const found: Gap[] = [];
	let previous = from;
	for (const [time] of points) {
		if (time < from || time > to) continue;
		if (time - previous > limit) found.push({ from: previous, to: time });
		previous = time;
	}
	if (to - previous > limit) found.push({ from: previous, to });
	return found;
}

/**
 * When a series starts, if that is later than the window does: a metric vigil only began
 * recording last week was not silent before that, it did not exist. Null when it covers the
 * window's start, or has no point in it at all.
 */
export function recordedSince(points: SeriesPoints, from: number, to: number, bucketMs: number): number | null {
	const first = points.find(([time]) => time >= from && time <= to)?.[0];
	return first !== undefined && first - from > GAP_BUCKETS * bucketMs ? first : null;
}

/** The share of `[from, to]` a series covers, 0 to 100. */
export function coverage(found: Gap[], from: number, to: number): number {
	const missing = found.reduce((sum, gap) => sum + (gap.to - gap.from), 0);
	return to > from ? Math.max(0, 100 * (1 - missing / (to - from))) : 0;
}

/** "3 h", "1 d 6 h": how long a gap lasted, to the precision anyone acts on. */
export function duration(ms: number): string {
	const hours = Math.round(ms / HOUR);
	if (hours < 1) return `${Math.max(1, Math.round(ms / 60_000))} min`;
	return hours < 24 ? `${hours} h` : `${Math.floor(hours / 24)} d ${hours % 24} h`;
}
