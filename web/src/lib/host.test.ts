import { describe, expect, it } from 'vitest';
import {
	cpuSeries,
	diskBusySeries,
	diskTrafficSeries,
	fillPercent,
	formatBytes,
	fullestPool,
	GIB,
	memoryDomain,
	memorySeries,
	memoryUsedPercent,
	networkSeries,
	poolFillSeries,
	poolTrafficSeries,
	pressureFullSeries
} from './host';
import { MAX_SERIES } from './series';
import type { HistoryResponse, HostLoad, PoolStatus, ZfsStatus } from './types';

const NOW = 1_000_000_000;
const AT = NOW - 1_000;

function host(overrides: Partial<HostLoad> = {}): HostLoad {
	return {
		asOf: new Date(AT).toISOString(),
		intervalSeconds: 2,
		uptimeSeconds: 1_000,
		memory: {
			totalBytes: 64 * GIB,
			usedBytes: 48 * GIB,
			cachedBytes: 6 * GIB,
			arcBytes: 32 * GIB,
			arcMaxBytes: 32 * GIB,
			swapTotalBytes: 8 * GIB,
			swapUsedBytes: 0
		},
		cpu: { busyPercent: 20, iowaitPercent: 5, logicalCpus: 16 },
		load: { one: 1.5, five: 1, fifteen: 0.5 },
		pressure: { cpuPercent: 1, memoryPercent: null, ioPercent: 25, cpuFullPercent: null, memoryFullPercent: null, ioFullPercent: 5 },
		disks: [{ deviceName: 'naa.1', port: null, readBytesPerSecond: 12_000_000, writtenBytesPerSecond: 3_000_000, busyPercent: 40 }],
		pools: [{ name: 'tank', readBytesPerSecond: 96_000_000, writtenBytesPerSecond: 24_000_000 }],
		interfaces: [{ name: 'vmbr0', receivedBytesPerSecond: 2_500_000, transmittedBytesPerSecond: 500_000 }],
		...overrides
	};
}

function history(overrides: Partial<HistoryResponse> = {}): HistoryResponse {
	return {
		range: '1h',
		from: NOW - 3_600_000,
		to: NOW - 30_000,
		bucketSeconds: 10,
		temperatures: {},
		duties: {},
		rpms: {},
		ups: {},
		host: {},
		trafficIn: {},
		trafficOut: {},
		busy: {},
		poolFill: {},
		...overrides
	};
}

function pool(name: string, sizeBytes: number | null, allocatedBytes: number | null): PoolStatus {
	return { name, state: 'ONLINE', errorCount: 0, sizeBytes, allocatedBytes, scan: null, vdevs: [] };
}

const zfs = (pools: PoolStatus[], isAvailable = true): ZfsStatus => ({ isAvailable, asOf: new Date(AT).toISOString(), pools });

describe('memorySeries', () => {
	it('charts bytes as GiB and appends the live reading', () => {
		const [used, arc] = memorySeries(history({ host: { memoryUsed: [[NOW - 60_000, 40 * GIB]] } }), host(), NOW);

		expect(used.points).toEqual([
			[NOW - 60_000, 40],
			[AT, 48]
		]);
		expect(arc.label).toBe('of which ARC');
		expect(arc.points).toEqual([[AT, 32]]);
	});

	it('draws nothing for a host without ZFS rather than a zero ARC', () => {
		const withArc = host().memory;
		const memory = withArc && { ...withArc, arcBytes: null, arcMaxBytes: null };
		expect(memorySeries(null, host({ memory }), NOW)[1].points).toEqual([]);
	});

	it('runs its axis from nothing to the most RAM the window saw', () => {
		expect(memoryDomain(null, host())).toEqual([0, 64]);
		expect(memoryDomain(history({ host: { memoryTotal: [[NOW - 60_000, 128 * GIB]] } }), host())).toEqual([0, 128]);
		expect(memoryDomain(null, null)).toBeUndefined();
	});

	it('draws the ARC limit, and nothing for it without ZFS', () => {
		expect(memorySeries(null, host(), NOW)[4]).toMatchObject({ label: 'ARC limit', points: [[AT, 32]] });
	});
});

describe('cpuSeries', () => {
	it('has no live point while there is no rate yet', () => {
		const [busy] = cpuSeries(history({ host: { cpuBusy: [[NOW - 60_000, 30]] } }), host({ cpu: null }), NOW);
		expect(busy.points).toEqual([[NOW - 60_000, 30]]);
	});
});

describe('poolTrafficSeries and poolFillSeries', () => {
	it('give each pool the same colour on every pool chart', () => {
		const stored = history({ trafficIn: { 'pool:rpool': [[NOW - 60_000, 1_000_000]] }, poolFill: { rpool: [[NOW - 60_000, 12]] } });
		const read = poolTrafficSeries(stored, host(), NOW, 'read');
		const fill = poolFillSeries(stored, zfs([pool('tank', 100, 25), pool('rpool', 100, 12)]), NOW);

		expect(read.map((s) => [s.label, s.color])).toEqual(fill.map((s) => [s.label, s.color]));
		expect(read.find((s) => s.label === 'tank')?.points).toEqual([[AT, 96]]);
		expect(read.find((s) => s.label === 'rpool')?.points).toEqual([[NOW - 60_000, 1]]);
		expect(fill.find((s) => s.label === 'tank')?.points).toEqual([[AT, 25]]);
	});

	it('stops at as many pools as colour can keep apart', () => {
		const many = Object.fromEntries(Array.from({ length: 9 }, (_, i) => [`p${i}`, [[NOW - 60_000, 1]] as [number, number][]]));
		expect(poolFillSeries(history({ poolFill: many }), null, NOW)).toHaveLength(MAX_SERIES);
	});
});

describe('pressureFullSeries', () => {
	it('has a line only where the kernel counts "full"', () => {
		expect(pressureFullSeries(null, host(), NOW).map((s) => s.points)).toEqual([[], [], [[AT, 5]]]);
	});
});

describe('networkSeries', () => {
	it('draws received and sent for each interface in MB/s, three interfaces at most', () => {
		const lines = networkSeries(null, host(), NOW);
		expect(lines.map((line) => [line.label, line.points])).toEqual([
			['vmbr0 received', [[AT, 2.5]]],
			['vmbr0 sent', [[AT, 0.5]]]
		]);

		const stored = Object.fromEntries(['a', 'b', 'c', 'd'].map((name) => [`net:${name}`, [[NOW - 60_000, 0]] as [number, number][]]));
		expect(networkSeries(history({ trafficIn: stored }), null, NOW)).toHaveLength(MAX_SERIES);
	});
});

describe('one disk', () => {
	it('charts its own throughput and busy time, and nothing for another disk', () => {
		expect(diskTrafficSeries(null, host(), 'naa.1', NOW).map((s) => s.points)).toEqual([[[AT, 12]], [[AT, 3]]]);
		expect(diskBusySeries(null, host(), 'naa.1', NOW)[0].points).toEqual([[AT, 40]]);
		expect(diskTrafficSeries(null, host(), 'naa.2', NOW)[0].points).toEqual([]);
	});
});

describe('the tiles', () => {
	it('read memory as a percent of the RAM each bucket had', () => {
		const stored = history({
			host: {
				memoryUsed: [
					[NOW - 120_000, 32 * GIB],
					[NOW - 60_000, 32 * GIB]
				],
				memoryTotal: [[NOW - 120_000, 128 * GIB]]
			}
		});
		const memory = memoryUsedPercent(stored, host(), NOW);
		expect(memory.value).toBe(75);
		// The first bucket had 128 GiB; the second stored no total, so it is read against today's 64.
		expect(memory.points).toEqual([
			[NOW - 120_000, 25],
			[NOW - 60_000, 50],
			[AT, 75]
		]);
		expect(memoryUsedPercent(null, null, NOW)).toEqual({ value: null, points: [] });
	});

	it('pick the fullest pool zpool could size, and none when zpool gave no answer', () => {
		const pools = [pool('tank', 100, 40), pool('rpool', 100, 81), pool('odd', null, 5)];
		expect(fullestPool(zfs(pools))).toEqual({ name: 'rpool', percent: 81 });
		expect(fullestPool(zfs(pools, false))).toBeNull();
		expect(fillPercent(pool('odd', null, 5))).toBeNull();
	});
});

describe('formatBytes', () => {
	it('picks the nearest binary unit', () => {
		expect(formatBytes(512)).toBe('512 B');
		expect(formatBytes(48 * GIB)).toBe('48.0 GiB');
		expect(formatBytes(1.5 * 2 ** 20)).toBe('1.5 MiB');
		expect(formatBytes(200 * GIB)).toBe('200 GiB');
	});
});
