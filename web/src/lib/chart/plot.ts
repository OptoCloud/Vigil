// What LineChart draws and reads out, as data: the line paths, the crosshair readout and the
// table view. No DOM, so it is unit-tested; the component keeps only the SVG and the pointer.

import { nearestIndex, type Scale } from '#lib/chart/scale.ts';
import type { ChartSeries, SeriesPoints } from '#lib/types.ts';

/**
 * How many median steps apart two samples may be and still be joined. Wider than that the data
 * really is missing (vigil-core was down); bridging it with a straight line would invent readings.
 */
const GAP_STEPS = 4;

/** How far, as a share of the window, a sample may sit from the crosshair and still be read out at it. */
const READOUT_REACH = 1 / 50;

/** Rows in the table view: enough to read the shape, few enough to read at all. */
const TABLE_ROWS = 24;

/** An SVG path through the points, broken wherever a gap says data is missing. */
export function linePath(points: SeriesPoints, x: Scale, y: Scale): string {
	const gap = medianStep(points) * GAP_STEPS;
	let d = '';
	for (let i = 0; i < points.length; i++) {
		const [time, value] = points[i];
		const broken = i === 0 || time - points[i - 1][0] > gap;
		d += `${broken ? 'M' : 'L'}${x(time).toFixed(1)},${y(value).toFixed(1)}`;
	}
	return d;
}

/** The typical spacing of samples: the bucket size, measured rather than trusted. */
export function medianStep(points: SeriesPoints): number {
	if (points.length < 3) return Infinity;
	const steps = points
		.slice(1)
		.map((point, i) => point[0] - points[i][0])
		.sort((a, b) => a - b);
	return steps[steps.length >> 1];
}

/** The series with the most samples: what the crosshair snaps to and the table is sampled from. */
function densest(series: ChartSeries[]): ChartSeries | null {
	return series.reduce<ChartSeries | null>((a, b) => (!a || b.points.length > a.points.length ? b : a), null);
}

export interface Readout {
	time: number;
	rows: { series: ChartSeries; value: number }[];
}

/**
 * The crosshair finds the X: it snaps to the nearest sample of the densest series, and the
 * readout then lists EVERY series there, so the pointer never has to land on a line.
 */
export function readoutAt(series: ChartSeries[], hoverTime: number | null, span: number): Readout | null {
	const reference = densest(series);
	if (hoverTime === null || !reference) return null;
	const time = reference.points[nearestIndex(reference.points, hoverTime)][0];

	const rows = series.flatMap((s) => {
		const point = s.points[nearestIndex(s.points, time)];
		// Don't report a value from far away as if it belonged to this instant.
		return Math.abs(point[0] - time) <= span * READOUT_REACH ? [{ series: s, value: point[1] }] : [];
	});
	return { time, rows };
}

/** The chart's accessible twin: the same data, thinned to a readable number of rows. */
export function tableRows(series: ChartSeries[]): { time: number; values: (number | undefined)[] }[] {
	const reference = densest(series);
	if (!reference) return [];
	const stride = Math.max(1, Math.ceil(reference.points.length / TABLE_ROWS));
	return reference.points
		.filter((_, i) => i % stride === 0)
		.map(([time]) => ({ time, values: series.map((s) => s.points[nearestIndex(s.points, time)]?.[1]) }));
}

/**
 * One CSV field. Quoted when it holds a comma, a quote or a line break; and a field a
 * spreadsheet would run as a formula (it starts with =, +, - or @) is led with an apostrophe,
 * since a series is named after a sensor or a fan, which is to say after someone's config.
 */
function csvField(text: string): string {
	const inert = /^[=+\-@]/.test(text) ? `'${text}` : text;
	return /[",\r\n]/.test(inert) ? `"${inert.replaceAll('"', '""')}"` : inert;
}

/**
 * Every point of every series as CSV, at full resolution (the table view thins; a download
 * must not). One row per instant any series has a point at, in time order, with the time in
 * ISO 8601 UTC; a series with no point at that instant leaves its cell empty, never a zero.
 */
export function toCsv(series: ChartSeries[], unit: string): string {
	const columns = series.map((s) => new Map(s.points));
	const times = [...new Set(series.flatMap((s) => s.points.map(([time]) => time)))].sort((a, b) => a - b);
	const header = ['time', ...series.map((s) => (unit ? `${s.label} (${unit.trim()})` : s.label))];
	const rows = times.map((time) => [new Date(time).toISOString(), ...columns.map((column) => String(column.get(time) ?? ''))]);
	return [header, ...rows].map((row) => row.map(csvField).join(',')).join('\r\n') + '\r\n';
}
