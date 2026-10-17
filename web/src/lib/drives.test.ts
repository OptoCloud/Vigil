import { describe, expect, it } from 'vitest';
import { bayRows, driveRows, formatYears, membershipsOf, shortPort, smartVerdict } from './drives';
import type { DriveState, HistoryResponse, SensorReading, VdevStatus, ZfsStatus } from './types';

const BAY_1 = 'pci-0000:01:00.1-ata-1';
const BAY_2 = 'pci-0000:01:00.1-ata-2';

function sensor(wwn: string, celsius: number | null, port: string | null): SensorReading {
	return {
		id: `drive:${wwn}`,
		category: 'drive',
		label: wwn,
		celsiusOrNull: celsius,
		sourcePath: '/sys',
		isAvailable: celsius !== null,
		port
	};
}

function drive(wwn: string, changes: Partial<DriveState> = {}): DriveState {
	return {
		wwn,
		port: null,
		passed: true,
		reallocatedSectorCount: 0,
		pendingSectorCount: 0,
		powerOnHours: 8766,
		sourcePath: '/dev/sda',
		asOf: '2026-10-17T00:00:00.000Z',
		...changes
	};
}

function vdev(name: string, deviceName: string | null, changes: Partial<VdevStatus> = {}): VdevStatus {
	return {
		name,
		guid: name,
		path: null,
		deviceName,
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

describe('driveRows', () => {
	it('lists a disk known only from SMART, and an asleep one last, hottest first otherwise', () => {
		const rows = driveRows([sensor('a', 30, BAY_1), sensor('b', 40, BAY_2), sensor('c', null, null)], [drive('d')], null);
		expect(rows.map((row) => row.wwn)).toEqual(['b', 'a', 'c', 'd']);
	});

	it('takes the port from SMART when the disk reports no temperature', () => {
		expect(driveRows([], [drive('d', { port: BAY_2 })], null)[0].port).toBe(BAY_2);
	});
});

describe('bayRows', () => {
	it('keeps a bay emptied during the range, in physical order', () => {
		const history = { temperatures: { [`port:${BAY_2}`]: [] } } as unknown as HistoryResponse;
		const bays = bayRows([sensor('a', 30, BAY_1)], [], history);
		expect(bays.map((bay) => [bay.port, bay.wwn])).toEqual([
			[BAY_1, 'a'],
			[BAY_2, undefined]
		]);
	});
});

describe('smartVerdict', () => {
	it('reads failure, then pending, then reallocated, and nothing as unknown', () => {
		expect(smartVerdict(drive('a', { passed: false, pendingSectorCount: 8 })).level).toBe('critical');
		expect(smartVerdict(drive('a', { pendingSectorCount: 8, reallocatedSectorCount: 1 })).label).toBe('Pending sectors');
		expect(smartVerdict(drive('a', { reallocatedSectorCount: 1 })).label).toBe('Reallocated sectors');
		expect(smartVerdict(drive('a')).level).toBe('good');
		expect(smartVerdict(undefined).level).toBe('unknown');
	});
});

describe('membershipsOf', () => {
	it('finds the vdev on a disk by the name every source shares', () => {
		const zfs: ZfsStatus = {
			isAvailable: true,
			asOf: 't',
			pools: [
				{
					name: 'void',
					state: 'ONLINE',
					errorCount: 0,
					sizeBytes: null,
					allocatedBytes: null,
					scan: null,
					vdevs: [vdev('sda', 'a'), vdev('sdb', 'b')]
				}
			]
		};
		expect(membershipsOf(zfs, 'b').map((m) => [m.pool.name, m.vdev.name])).toEqual([['void', 'sdb']]);
		expect(membershipsOf(zfs, 'z')).toEqual([]);
		expect(membershipsOf(null, 'a')).toEqual([]);
	});
});

describe('formatting', () => {
	it('shortens a port to what tells bays apart', () => {
		expect(shortPort('pci-0000:03:00.0-sas-phy2-lun-0')).toBe('sas-phy2');
		expect(shortPort(BAY_1)).toBe('ata-1');
		expect(shortPort(null)).toBe('');
	});

	it('reads power-on hours as years', () => {
		expect(formatYears(8766)).toBe('1.0 y');
		expect(formatYears(null)).toBe('n/a');
	});
});
