// The drives as the pages list them: one row per disk, or per bay, joined from the live
// temperatures, vigil-core's remembered SMART state, and ZFS's view of the same disk. Every
// source names a disk the same way (vigild's daemon/src/disks.rs), which is what the joins rely on.

import type { DriveState, HistoryResponse, PoolStatus, SensorReading, SeriesPoints, VdevStatus, ZfsStatus } from '#lib/types.ts';
import { vdevVerdict } from '#lib/zfs.ts';

export type Level = 'good' | 'warning' | 'critical' | 'unknown';

export interface Verdict {
	level: Level;
	label: string;
}

export interface DriveRow {
	wwn: string;
	port: string | null;
	sensor: SensorReading | undefined;
	health: DriveState | undefined;
	trend: SeriesPoints;
}

export interface BayRow {
	port: string;
	/** The disk in it now, or undefined for a bay that had one earlier in the range. */
	wwn: string | undefined;
	sensor: SensorReading | undefined;
	health: DriveState | undefined;
	trend: SeriesPoints;
}

const DRIVE_PREFIX = 'drive:';
const PORT_PREFIX = 'port:';

const wwnOf = (sensor: SensorReading) => sensor.id.slice(DRIVE_PREFIX.length);
const driveSensors = (sensors: SensorReading[]) => sensors.filter((s) => s.category === 'drive');

/** Every disk reporting a temperature now or with remembered SMART state, hottest first. */
export function driveRows(sensors: SensorReading[], drives: DriveState[], history: HistoryResponse | null): DriveRow[] {
	const health = new Map(drives.map((drive) => [drive.wwn, drive]));
	const temperatures = new Map(driveSensors(sensors).map((s) => [wwnOf(s), s]));

	return [...new Set([...temperatures.keys(), ...health.keys()])]
		.map((wwn) => {
			const sensor = temperatures.get(wwn);
			return {
				wwn,
				port: sensor?.port ?? health.get(wwn)?.port ?? null,
				sensor,
				health: health.get(wwn),
				trend: history?.temperatures[`${DRIVE_PREFIX}${wwn}`] ?? []
			};
		})
		.sort((a, b) => (b.sensor?.celsiusOrNull ?? -1) - (a.sensor?.celsiusOrNull ?? -1) || a.wwn.localeCompare(b.wwn));
}

/** Every bay that has a drive now or had one in the selected range, in physical order. */
export function bayRows(sensors: SensorReading[], drives: DriveState[], history: HistoryResponse | null): BayRow[] {
	const health = new Map(drives.map((drive) => [drive.wwn, drive]));
	const occupants = new Map(
		driveSensors(sensors)
			.filter((s) => s.port)
			.map((s) => [s.port as string, s])
	);
	const recorded = Object.keys(history?.temperatures ?? {})
		.filter((id) => id.startsWith(PORT_PREFIX))
		.map((id) => id.slice(PORT_PREFIX.length));

	return [...new Set([...occupants.keys(), ...recorded])]
		.sort((a, b) => a.localeCompare(b, undefined, { numeric: true }))
		.map((port) => {
			const sensor = occupants.get(port);
			const wwn = sensor ? wwnOf(sensor) : undefined;
			return { port, wwn, sensor, health: wwn ? health.get(wwn) : undefined, trend: history?.temperatures[`${PORT_PREFIX}${port}`] ?? [] };
		});
}

export function smartVerdict(drive: DriveState | undefined): Verdict {
	if (!drive || drive.passed === null) return { level: 'unknown', label: 'No data yet' };
	if (!drive.passed) return { level: 'critical', label: 'FAILED' };
	if ((drive.pendingSectorCount ?? 0) > 0) return { level: 'warning', label: 'Pending sectors' };
	if ((drive.reallocatedSectorCount ?? 0) > 0) return { level: 'warning', label: 'Reallocated sectors' };
	return { level: 'good', label: 'Passed' };
}

/** Where a disk sits in ZFS: every leaf vdev on it, with its pool. Usually one, or none. */
export interface Membership {
	pool: PoolStatus;
	vdev: VdevStatus;
}

export function membershipsOf(zfs: ZfsStatus | null | undefined, wwn: string): Membership[] {
	return (zfs?.pools ?? []).flatMap((pool) => pool.vdevs.filter((vdev) => vdev.deviceName === wwn).map((vdev) => ({ pool, vdev })));
}

const RANK: Record<Level, number> = { good: 0, unknown: 1, warning: 2, critical: 3 };

/** ZFS's view of one disk: the worst of the vdevs on it, or null when it is in no pool. */
export function zfsDiskVerdict(memberships: Membership[]): Verdict | null {
	const verdicts = memberships.map((m) => vdevVerdict(m.vdev));
	return verdicts.length === 0 ? null : verdicts.reduce((a, b) => (RANK[b.level] > RANK[a.level] ? b : a));
}

/**
 * One verdict for a disk, from both of the things that judge it. ZFS's wins when it is worse:
 * a disk ZFS has faulted reads critical whatever SMART says, because on the night of the
 * void fault SMART still said PASSED for the disk ZFS had thrown out.
 */
export function diskVerdict(health: DriveState | undefined, memberships: Membership[]): Verdict {
	const smart = smartVerdict(health);
	const zfs = zfsDiskVerdict(memberships);
	return zfs && RANK[zfs.level] > RANK[smart.level] && zfs.level !== 'good' ? { level: zfs.level, label: `ZFS: ${zfs.label}` } : smart;
}

/** "void", "void, spare", "void, log": the pool a disk is in and, unless it holds data, its role. */
export function roleOf(membership: Membership): string {
	const role = membership.vdev.class === 'normal' ? '' : `, ${membership.vdev.class}`;
	return `${membership.pool.name}${role}`;
}

/** Read, write and checksum errors together, as zpool status shows them per vdev. */
export function zfsErrors(memberships: Membership[]): number | null {
	if (memberships.length === 0) return null;
	return memberships.reduce((sum, m) => sum + (m.vdev.readErrors ?? 0) + (m.vdev.writeErrors ?? 0) + (m.vdev.checksumErrors ?? 0), 0);
}

/** "pci-0000:03:00.0-sas-phy2-lun-0" to "sas-phy2", "pci-0000:01:00.1-ata-3" to "ata-3": the controller is in the title. */
export const shortPort = (port: string | null | undefined) => port?.replace(/^pci-[0-9a-f:.]+-/i, '').replace(/-lun-0$/, '') ?? '';

/** The last eight characters: enough to tell thirteen WWNs apart at a glance, the full one in the title. */
export const shortWwn = (wwn: string) => wwn.slice(-8);

export const deviceOf = (path: string | undefined) => path?.replace('/dev/', '') ?? '';

export const formatCount = (value: number | null | undefined) => (value == null ? 'n/a' : value.toLocaleString());

/** A Julian year: SMART counts power-on hours, and nobody reads 8760 of them as a year at a glance. */
const HOURS_PER_YEAR = 8766;

export const formatYears = (hours: number | null | undefined) => (hours == null ? 'n/a' : `${(hours / HOURS_PER_YEAR).toFixed(1)} y`);
