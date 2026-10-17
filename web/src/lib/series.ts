// What the charts and tiles draw: history from /api/history, with the live reading appended so
// every line reaches "now". Pure, so the page components only lay it out.

import { humanize } from '#lib/chart/scale.ts';
import type { ChartSeries, FanStatus, HistoryResponse, SensorReading, SeriesPoints, Snapshot, UpsReading } from '#lib/types.ts';

/**
 * How old a live reading may be and still be drawn as the newest point. Past this the reading
 * is not live any more, and the header already says so.
 */
export const LIVE_POINT_MAX_AGE_MS = 60_000;

/** As many lines as colour can keep apart: the six --series-N tokens in app.css. */
export const MAX_SERIES = 6;

// Colour follows the entity, in a fixed order: the CPU line is the same blue in every range,
// every page and every session. Thirteen drives and two DIMMs would blow through what colour
// can distinguish, so each group is charted as one line: its hottest member.
export const TEMPERATURE_SERIES = [
	{ id: 'cpu', label: 'CPU', color: 'var(--series-1)' },
	{ id: 'gpu', label: 'GPU', color: 'var(--series-2)' },
	{ id: 'hba', label: 'HBA', color: 'var(--series-3)' },
	{ id: 'drives:max', label: 'Hottest drive', color: 'var(--series-4)' },
	{ id: 'board', label: 'Board', color: 'var(--series-5)' },
	{ id: 'memory:max', label: 'Hottest DIMM', color: 'var(--series-6)' }
] as const;

/** The reading with the highest temperature in a category, ignoring unreadable ones. */
export function hottest(sensors: SensorReading[], category: SensorReading['category']): SensorReading | undefined {
	return sensors
		.filter((s) => s.category === category && s.celsiusOrNull !== null)
		.sort((a, b) => (b.celsiusOrNull as number) - (a.celsiusOrNull as number))[0];
}

/** The live value of each TEMPERATURE_SERIES id. */
export function liveTemperatures(sensors: SensorReading[]): Record<string, number | null | undefined> {
	const byId = (id: string) => sensors.find((s) => s.id === id)?.celsiusOrNull;
	return {
		cpu: byId('cpu'),
		gpu: byId('gpu'),
		hba: byId('hba'),
		board: byId('board'),
		'drives:max': hottest(sensors, 'drive')?.celsiusOrNull,
		'memory:max': hottest(sensors, 'memory')?.celsiusOrNull
	};
}

/**
 * History is bucketed and up to 30 s old; appending the live reading makes the line reach
 * "now". Not when the reading is stale, and not twice once history has caught up with it.
 */
export function withLivePoint(points: SeriesPoints | undefined, value: number | null | undefined, at: number, now: number): SeriesPoints {
	const base = points ?? [];
	if (value == null || at === 0 || now - at > LIVE_POINT_MAX_AGE_MS) return base;
	const last = base.at(-1);
	return last && last[0] >= at ? base : [...base, [at, value]];
}

/** The window a chart spans: the history's, slid forward to the live reading when there is one. */
export function chartWindow(history: HistoryResponse | null, snapshotAt: number, now: number): { from: number; to: number } {
	if (!history) return { from: 0, to: 0 };
	const to = Math.max(history.to, now - snapshotAt < LIVE_POINT_MAX_AGE_MS ? snapshotAt : 0);
	return { from: to - (history.to - history.from), to };
}

const timeOf = (snapshot: Snapshot | null) => (snapshot ? new Date(snapshot.timestampUtc).getTime() : 0);

export function temperatureSeries(history: HistoryResponse | null, snapshot: Snapshot | null, now: number): ChartSeries[] {
	const live = liveTemperatures(snapshot?.sensors ?? []);
	const at = timeOf(snapshot);
	return TEMPERATURE_SERIES.map((s) => ({ ...s, points: withLivePoint(history?.temperatures[s.id], live[s.id], at, now) }));
}

/** Every fan that has history in the range or is reporting now, in name order. */
export function fanIds(history: HistoryResponse | null, fans: FanStatus[]): string[] {
	return [...new Set([...Object.keys(history?.duties ?? {}), ...fans.map((fan) => fan.id)])].sort();
}

/**
 * One line per fan, the first MAX_SERIES of them. `metric` picks duty (one fixed 0-100 axis)
 * or speed; the two are never overlaid on two y-scales, which would invent correlations.
 */
export function fanSeries(history: HistoryResponse | null, snapshot: Snapshot | null, now: number, metric: 'duty' | 'rpm'): ChartSeries[] {
	const fans = snapshot?.fans ?? [];
	const at = timeOf(snapshot);
	const stored = metric === 'duty' ? history?.duties : history?.rpms;
	return fanIds(history, fans)
		.slice(0, MAX_SERIES)
		.map((id, index) => {
			const fan = fans.find((f) => f.id === id);
			const live = metric === 'duty' ? fan?.dutyPercent : fan?.rpm;
			return { id, label: humanize(id), color: `var(--series-${index + 1})`, points: withLivePoint(stored?.[id], live, at, now) };
		});
}

/** One disk's temperature, following the disk wherever it is plugged in. */
export function driveTemperatureSeries(
	history: HistoryResponse | null,
	snapshot: Snapshot | null,
	wwn: string,
	now: number
): ChartSeries[] {
	const live = snapshot?.sensors.find((s) => s.id === `drive:${wwn}`)?.celsiusOrNull;
	return [
		{
			id: `drive:${wwn}`,
			label: 'This drive',
			color: 'var(--series-1)',
			points: withLivePoint(history?.temperatures[`drive:${wwn}`], live, timeOf(snapshot), now)
		}
	];
}

/**
 * Charge and load share one 0-100 axis. Runtime (minutes) and mains voltage are other units,
 * so they get their own chart or the panel, never a second y-scale.
 */
export function upsSeries(
	history: HistoryResponse | null,
	reading: UpsReading | null,
	now: number
): { percent: ChartSeries[]; mains: ChartSeries[] } {
	const at = reading ? new Date(reading.timestampUtc).getTime() : 0;
	const point = (metric: 'charge' | 'load' | 'inputVoltage', value: number | null | undefined) =>
		withLivePoint(history?.ups[metric], value, at, now);
	return {
		percent: [
			{ id: 'charge', label: 'Battery charge', color: 'var(--series-3)', points: point('charge', reading?.batteryCharge) },
			{ id: 'load', label: 'Load', color: 'var(--series-2)', points: point('load', reading?.load) }
		],
		mains: [{ id: 'inputVoltage', label: 'Mains in', color: 'var(--series-1)', points: point('inputVoltage', reading?.inputVoltage) }]
	};
}
