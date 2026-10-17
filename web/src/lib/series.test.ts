import { describe, expect, it } from 'vitest';
import { chartWindow, fanSeries, hottest, LIVE_POINT_MAX_AGE_MS, MAX_SERIES, temperatureSeries, withLivePoint } from './series';
import type { HistoryResponse, SensorReading, Snapshot } from './types';

const NOW = 1_000_000_000;

function sensor(id: string, category: SensorReading['category'], celsius: number | null): SensorReading {
	return { id, category, label: id, celsiusOrNull: celsius, sourcePath: '/sys', isAvailable: celsius !== null };
}

function snapshot(at: number, sensors: SensorReading[] = [], fanCount = 0): Snapshot {
	return {
		timestampUtc: new Date(at).toISOString(),
		sensors,
		fans: Array.from({ length: fanCount }, (_, i) => ({
			id: `fan-${String(i).padStart(2, '0')}`,
			dutyPercent: 50,
			rpm: 900,
			mode: 'manual',
			stalled: false
		})),
		driveHealth: [],
		controlLoopHealthy: true
	};
}

function history(temperatures: HistoryResponse['temperatures'] = {}): HistoryResponse {
	return { range: '1h', from: NOW - 3_600_000, to: NOW - 30_000, bucketSeconds: 10, temperatures, duties: {}, rpms: {}, ups: {} };
}

describe('withLivePoint', () => {
	it('appends a fresh reading after the history', () => {
		expect(withLivePoint([[NOW - 30_000, 40]], 41, NOW - 1_000, NOW)).toEqual([
			[NOW - 30_000, 40],
			[NOW - 1_000, 41]
		]);
	});

	it('leaves the history alone when the reading is stale, missing, or already in it', () => {
		const points: [number, number][] = [[NOW - 30_000, 40]];
		expect(withLivePoint(points, 41, NOW - LIVE_POINT_MAX_AGE_MS - 1, NOW)).toBe(points);
		expect(withLivePoint(points, null, NOW, NOW)).toBe(points);
		expect(withLivePoint(points, 41, NOW - 30_000, NOW)).toBe(points);
	});
});

describe('hottest', () => {
	it('skips an unreadable sensor rather than treating it as cold or hot', () => {
		const sensors = [
			sensor('drive:a', 'drive', 35),
			sensor('drive:b', 'drive', null),
			sensor('drive:c', 'drive', 41),
			sensor('cpu', 'cpu', 70)
		];
		expect(hottest(sensors, 'drive')?.id).toBe('drive:c');
		expect(hottest([sensor('drive:b', 'drive', null)], 'drive')).toBeUndefined();
	});
});

describe('temperatureSeries', () => {
	it('charts each group by its hottest member, live', () => {
		const live = snapshot(NOW - 2_000, [sensor('drive:a', 'drive', 35), sensor('drive:c', 'drive', 41)]);
		const drives = temperatureSeries(history({ 'drives:max': [[NOW - 30_000, 40]] }), live, NOW).find((s) => s.id === 'drives:max');
		expect(drives?.points.at(-1)).toEqual([NOW - 2_000, 41]);
	});
});

describe('chartWindow', () => {
	it('slides the history window forward to a live snapshot, keeping its span', () => {
		expect(chartWindow(history(), NOW - 1_000, NOW)).toEqual({ from: NOW - 1_000 - 3_570_000, to: NOW - 1_000 });
	});
});

describe('fanSeries', () => {
	it('charts at most as many fans as colour can keep apart, in name order', () => {
		const series = fanSeries(null, snapshot(NOW, [], MAX_SERIES + 2), NOW, 'rpm');
		expect(series.map((s) => s.id)).toEqual(Array.from({ length: MAX_SERIES }, (_, i) => `fan-0${i}`));
		expect(series[0].points).toEqual([[NOW, 900]]);
	});
});
