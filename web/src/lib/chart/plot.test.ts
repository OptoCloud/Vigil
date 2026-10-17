import { describe, expect, it } from 'vitest';
import type { ChartSeries, SeriesPoints } from '../types';
import { linePath, medianStep, readoutAt, tableRows } from './plot';
import { linearScale } from './scale';

const identity = linearScale([0, 1], [0, 1]);

const series = (id: string, points: SeriesPoints): ChartSeries => ({ id, label: id, color: 'red', points });

describe('linePath', () => {
	it('joins evenly spaced samples and breaks where data is missing', () => {
		const points: SeriesPoints = [
			[0, 1],
			[10, 2],
			[20, 3],
			[30, 4],
			[200, 5]
		];
		expect(linePath(points, identity, identity)).toBe('M0.0,1.0L10.0,2.0L20.0,3.0L30.0,4.0M200.0,5.0');
	});

	it('cannot measure a gap from two samples, so joins them', () => {
		expect(medianStep([[0, 1]])).toBe(Infinity);
		expect(
			linePath(
				[
					[0, 1],
					[500, 2]
				],
				identity,
				identity
			)
		).toBe('M0.0,1.0L500.0,2.0');
	});
});

describe('readoutAt', () => {
	const dense = series('dense', [
		[0, 10],
		[10, 11],
		[20, 12]
	]);
	const sparse = series('sparse', [[100, 50]]);

	it('snaps to the densest series and leaves out a value from far away', () => {
		const readout = readoutAt([dense, sparse], 12, 100);
		expect(readout?.time).toBe(10);
		expect(readout?.rows.map((row) => row.series.id)).toEqual(['dense']);
	});

	it('reads nothing without a pointer or a series', () => {
		expect(readoutAt([dense], null, 100)).toBeNull();
		expect(readoutAt([], 5, 100)).toBeNull();
	});
});

describe('tableRows', () => {
	it('thins a long series to a readable table, one column per series', () => {
		const long = series(
			'long',
			Array.from({ length: 100 }, (_, i): [number, number] => [i, i])
		);
		const rows = tableRows([long]);
		expect(rows.length).toBeLessThanOrEqual(25);
		expect(rows[1]).toEqual({ time: 5, values: [5] });
	});
});
