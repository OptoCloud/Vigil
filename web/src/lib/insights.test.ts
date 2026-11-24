import { describe, expect, it } from 'vitest';
import {
	binned,
	correlation,
	coverage,
	dayStart,
	duration,
	fanResponse,
	forecast,
	gaps,
	pairByTime,
	recordedSince,
	rhythm
} from './insights';
import type { SeriesPoints } from './types';

const HOUR = 3_600_000;
const DAY = 24 * HOUR;

/** The value, or a failed test: these cases are about what is there, not whether it is. */
function some<T>(value: T | null | undefined): T {
	if (value == null) throw new Error('expected a value');
	return value;
}

describe('a fan and its temperature', () => {
	// A fan that follows the drives: duty is 2 x temperature - 10. Each temperature three times
	// over, since a degree seen once or twice is left out of the curve.
	const drives: SeriesPoints = [34, 36, 38, 40, 42, 34, 36, 38, 40, 42, 34, 36, 38, 40, 42].map((celsius, i) => [i * HOUR, celsius]);
	const duty: SeriesPoints = drives.map(([time, celsius]) => [time, 2 * celsius - 10]);
	const cpu: SeriesPoints = drives.map(([time], i) => [time, [50, 41, 47, 39, 52, 44, 49][i % 7]]);

	it('pairs only the instants both series have', () => {
		expect(
			pairByTime(
				[
					[0, 1],
					[HOUR, 2]
				],
				[
					[HOUR, 20],
					[2 * HOUR, 30]
				]
			)
		).toEqual([{ time: HOUR, x: 2, y: 20 }]);
	});

	it('finds the temperature the duty follows, among the candidates', () => {
		const response = fanResponse(duty, { cpu, 'drives:max': drives }, ['cpu', 'drives:max']);

		expect(response?.follows).toBe('drives:max');
		expect(response?.r).toBeCloseTo(1);
		expect(response?.curve[0]).toEqual({ x: 34, y: 58, count: 3 });
		expect(response?.curve).toHaveLength(5);
	});

	it('does not credit a sensor that was only there for a few of the readings', () => {
		// Three points that happen to line up perfectly with the duty.
		const fluke: SeriesPoints = duty.slice(0, 3).map(([time, percent]) => [time, percent / 2]);

		expect(fanResponse(duty, { fluke, cpu }, ['fluke', 'cpu'])?.follows).toBe('cpu');
	});

	it('says nothing about a fan whose duty never moved', () => {
		const flat: SeriesPoints = drives.map(([time]) => [time, 40]);

		expect(correlation(pairByTime(drives, flat))).toBeNull();
		expect(fanResponse(flat, { 'drives:max': drives }, ['drives:max'])).toBeNull();
		expect(fanResponse(duty, {}, ['cpu'])).toBeNull();
	});

	it('averages the duty at each degree', () => {
		const pairs = [
			{ time: 0, x: 37.6, y: 60 },
			{ time: 1, x: 38.4, y: 70 },
			{ time: 2, x: 40, y: 80 }
		];

		expect(binned(pairs)).toEqual([
			{ x: 38, y: 65, count: 2 },
			{ x: 40, y: 80, count: 1 }
		]);
	});
});

describe("the day's rhythm", () => {
	// Local times, as the grid is the viewer's clock.
	const at = (day: number, hour: number) => new Date(2026, 10, day, hour).getTime();

	it('lays a series out as days by time of day, averaging what shares a slot', () => {
		const grid = some(
			rhythm(
				[
					[at(20, 1), 70],
					[at(20, 2), 50],
					[at(20, 13), 40],
					[at(21, 1), 72]
				],
				6
			)
		);

		expect(grid.days).toEqual([dayStart(at(20, 0)), dayStart(at(21, 0))]);
		expect(grid.slots).toBe(4);
		expect(grid.cells).toEqual([
			[60, null, 40, null],
			[72, null, null, null]
		]);
		expect([grid.min, grid.max]).toEqual([40, 72]);
	});

	it('has nothing to draw without readings', () => {
		expect(rhythm([], 2)).toBeNull();
	});
});

describe('when a pool is full', () => {
	it('fits the rate and says when each level is reached', () => {
		// 70 % and a point a day: 80 % in ten days from the last reading, 90 % in twenty.
		const points: SeriesPoints = Array.from({ length: 6 }, (_, day) => [day * DAY, 65 + day]);
		const result = some(forecast(points));

		expect(result.percent).toBe(70);
		expect(result.perDay).toBeCloseTo(1);
		expect(result.reaches[0]).toBeCloseTo(15 * DAY, -3);
		expect(result.reaches[1]).toBeCloseTo(25 * DAY, -3);
		expect(result.basisDays).toBe(5);
	});

	it('never reaches a level it is shrinking away from, or is already past', () => {
		const shrinking: SeriesPoints = Array.from({ length: 6 }, (_, day) => [day * DAY, 50 - day]);
		const past: SeriesPoints = Array.from({ length: 6 }, (_, day) => [day * DAY, 82 + day]);

		expect(some(forecast(shrinking)).reaches).toEqual([null, null]);
		expect(some(forecast(past)).reaches[0]).toBeNull();
		expect(some(forecast(past)).reaches[1]).not.toBeNull();
	});

	it('does not guess from an afternoon', () => {
		expect(
			forecast([
				[0, 40],
				[HOUR, 41],
				[2 * HOUR, 42]
			])
		).toBeNull();
		expect(forecast([])).toBeNull();
	});
});

describe('when a source went quiet', () => {
	const points: SeriesPoints = [2, 3, 4, 9, 10].map((hour) => [hour * HOUR, 1]);

	it('finds the silences, the start and the end of the window included', () => {
		expect(gaps(points, 0, 14 * HOUR, HOUR)).toEqual([
			{ from: 4 * HOUR, to: 9 * HOUR },
			{ from: 10 * HOUR, to: 14 * HOUR }
		]);
		// From the window's start, when the source began later than it.
		expect(gaps(points, -10 * HOUR, 10 * HOUR, HOUR)[0]).toEqual({ from: -10 * HOUR, to: 2 * HOUR });
		expect(gaps([], 0, 10 * HOUR, HOUR)).toEqual([{ from: 0, to: 10 * HOUR }]);
	});

	it('does not call one late bucket a gap', () => {
		expect(
			gaps(
				[
					[0, 1],
					[2 * HOUR, 1],
					[3 * HOUR, 1]
				],
				0,
				3 * HOUR,
				HOUR
			)
		).toEqual([]);
	});

	it('tells a series that began inside the window from one that fell silent', () => {
		expect(recordedSince(points, -10 * HOUR, 10 * HOUR, HOUR)).toBe(2 * HOUR);
		expect(recordedSince(points, 0, 10 * HOUR, HOUR)).toBeNull();
		expect(recordedSince([], 0, 10 * HOUR, HOUR)).toBeNull();
	});

	it('says how much of the window is covered and how long a gap was', () => {
		expect(coverage([{ from: 0, to: 5 * HOUR }], 0, 20 * HOUR)).toBe(75);
		expect(coverage([], 0, 20 * HOUR)).toBe(100);
		expect([duration(20 * 60_000), duration(3 * HOUR), duration(30 * HOUR)]).toEqual(['20 min', '3 h', '1 d 6 h']);
	});
});
