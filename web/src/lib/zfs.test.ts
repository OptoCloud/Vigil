import { describe, expect, it } from 'vitest';
import type { PoolStatus, VdevStatus } from './types';
import { describeScan, formatBytes, poolVerdict, scanProgress, usedPercent, vdevGroups, vdevVerdict, zfsVerdict } from './zfs';

function vdev(name: string, changes: Partial<VdevStatus> = {}): VdevStatus {
	return {
		name,
		guid: name,
		path: null,
		deviceName: null,
		port: null,
		parent: 'raidz2-0',
		class: 'normal',
		state: 'ONLINE',
		aux: null,
		readErrors: 0,
		writeErrors: 0,
		checksumErrors: 0,
		slowIos: 0,
		...changes
	};
}

function pool(changes: Partial<PoolStatus> = {}): PoolStatus {
	return {
		name: 'void',
		state: 'ONLINE',
		errorCount: 0,
		sizeBytes: null,
		allocatedBytes: null,
		scan: null,
		vdevs: [vdev('a')],
		...changes
	};
}

// The night of the fault (docs/incidents/2026-10-17-void-disk-fault.md), as vigild reports it.
const FAULT = pool({
	state: 'DEGRADED',
	vdevs: [
		vdev('a'),
		vdev('faulted', { parent: 'spare-5', state: 'FAULTED', aux: 'ERR_EXCEEDED', readErrors: 14 }),
		vdev('spare-in-use', { parent: 'spare-5', class: 'spare', state: 'INUSE' }),
		vdev('spare-idle', { parent: null, class: 'spare', state: 'AVAIL', readErrors: null, writeErrors: null, checksumErrors: null })
	]
});

describe('poolVerdict', () => {
	it('reads the fault as degraded and a pool with permanent errors as critical', () => {
		expect(poolVerdict(FAULT).level).toBe('warning');
		expect(poolVerdict(pool({ errorCount: 2 })).level).toBe('critical');
		expect(poolVerdict(pool({ vdevs: [vdev('a', { checksumErrors: 3 })] })).label).toBe('Online, with errors');
		expect(poolVerdict(pool()).level).toBe('good');
	});

	it('reads a state it does not know as a problem, never as healthy', () => {
		expect(poolVerdict(pool({ state: 'SOMETHING_NEW' })).level).toBe('critical');
	});
});

describe('vdevVerdict', () => {
	it('says why a disk is out, and tells a ready spare from one standing in', () => {
		expect(vdevVerdict(FAULT.vdevs[1])).toEqual({ level: 'critical', label: 'Faulted (ERR_EXCEEDED)' });
		expect(vdevVerdict(FAULT.vdevs[2]).label).toBe('Spare, in use');
		expect(vdevVerdict(FAULT.vdevs[3]).level).toBe('good');
	});
});

describe('zfsVerdict', () => {
	it('is the worst pool, and unknown when zpool gave no answer', () => {
		expect(zfsVerdict({ isAvailable: true, asOf: 't', pools: [pool(), FAULT] }).label).toBe('Degraded');
		expect(zfsVerdict({ isAvailable: true, asOf: 't', pools: [pool(), pool({ name: 'rpool' })] }).label).toBe('2 pools online');
		expect(zfsVerdict({ isAvailable: false, asOf: 't', pools: [] }).level).toBe('unknown');
		expect(zfsVerdict(null).level).toBe('unknown');
	});
});

describe('scans', () => {
	const running = {
		function: 'RESILVER',
		state: 'SCANNING',
		startTime: null,
		endTime: null,
		toExamineBytes: 1000,
		examinedBytes: 900,
		issuedBytes: 250,
		processedBytes: 240,
		errors: 0
	};

	it('measures progress as zpool does, by what has been issued', () => {
		expect(scanProgress(running)).toBe(0.25);
		expect(scanProgress({ ...running, state: 'FINISHED' })).toBeNull();
		expect(scanProgress(null)).toBeNull();
	});

	it('names the scan and its state', () => {
		expect(describeScan(running)).toBe('Resilver running');
		expect(describeScan({ ...running, function: 'SCRUB', state: 'CANCELED' })).toBe('Scrub canceled');
	});
});

describe('vdevGroups', () => {
	it('groups leaves under what they sit in, idle spares on their own', () => {
		expect(vdevGroups(FAULT).map((group) => [group.parent, group.vdevs.length])).toEqual([
			['raidz2-0', 1],
			['spare-5', 2],
			['spares', 1]
		]);
	});
});

describe('usedPercent', () => {
	it('is allocation against size, and unknown without both', () => {
		expect(usedPercent(pool({ sizeBytes: 400, allocatedBytes: 100 }))).toBe(25);
		expect(usedPercent(pool({ sizeBytes: null, allocatedBytes: 100 }))).toBeNull();
	});
});

describe('formatBytes', () => {
	it('uses binary units, as zpool list does', () => {
		expect(formatBytes(512)).toBe('512 B');
		expect(formatBytes(1536)).toBe('1.5 KiB');
		expect(formatBytes(32_000_000_000_000)).toBe('29.1 TiB');
		expect(formatBytes(null)).toBe('n/a');
	});
});
