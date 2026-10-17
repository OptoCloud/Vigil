// How ZFS pool state reads to a person (ADR-018). States are zpool's own words; anything this
// does not recognise reads as a problem, never as healthy, so a state a later ZFS adds is seen.

import type { Level, Verdict } from '#lib/drives.ts';
import type { PoolScan, PoolStatus, VdevStatus, ZfsStatus } from '#lib/types.ts';

export function poolVerdict(pool: PoolStatus): Verdict {
	if (pool.state === 'ONLINE') {
		if ((pool.errorCount ?? 0) > 0) return { level: 'critical', label: `${pool.errorCount} permanent errors` };
		if (pool.vdevs.some((vdev) => hasErrors(vdev))) return { level: 'warning', label: 'Online, with errors' };
		return { level: 'good', label: 'Online' };
	}
	if (pool.state === 'DEGRADED') return { level: 'warning', label: 'Degraded' };
	return { level: 'critical', label: titleCase(pool.state) };
}

export function vdevVerdict(vdev: VdevStatus): Verdict {
	const reason = vdev.aux ? ` (${vdev.aux})` : '';
	switch (vdev.state) {
		case 'ONLINE':
			return hasErrors(vdev) ? { level: 'warning', label: 'Online, with errors' } : { level: 'good', label: 'Online' };
		// A hot spare's own states: AVAIL is ready, INUSE is standing in for a failed disk.
		case 'AVAIL':
			return { level: 'good', label: 'Spare, available' };
		case 'INUSE':
			return { level: 'warning', label: 'Spare, in use' };
		case 'DEGRADED':
			return { level: 'warning', label: `Degraded${reason}` };
		default:
			return { level: 'critical', label: `${titleCase(vdev.state)}${reason}` };
	}
}

/** The worst of the pools, for a one-line summary. Unknown when zpool gave no answer. */
export function zfsVerdict(zfs: ZfsStatus | null | undefined): Verdict {
	if (!zfs) return { level: 'unknown', label: 'Not reported' };
	if (!zfs.isAvailable) return { level: 'unknown', label: 'zpool unreadable' };
	if (zfs.pools.length === 0) return { level: 'unknown', label: 'No pools' };
	const RANK: Record<Level, number> = { good: 0, unknown: 1, warning: 2, critical: 3 };
	const verdicts = zfs.pools.map(poolVerdict);
	const worst = verdicts.reduce((a, b) => (RANK[b.level] > RANK[a.level] ? b : a));
	return worst.level === 'good'
		? { level: 'good', label: zfs.pools.length === 1 ? 'Pool online' : `${zfs.pools.length} pools online` }
		: worst;
}

/** How full the pool is, as zpool list's CAP column: raw allocation against raw size, 0 to 100. */
export function usedPercent(pool: PoolStatus): number | null {
	return pool.sizeBytes && pool.allocatedBytes !== null ? (pool.allocatedBytes / pool.sizeBytes) * 100 : null;
}

export function hasErrors(vdev: VdevStatus): boolean {
	return (vdev.readErrors ?? 0) + (vdev.writeErrors ?? 0) + (vdev.checksumErrors ?? 0) > 0;
}

/**
 * How far a running scan has got, 0 to 1: issued against what there is to examine, which is
 * the figure zpool status itself prints. Null when it is not running or will not say.
 */
export function scanProgress(scan: PoolScan | null): number | null {
	if (!scan || scan.state !== 'SCANNING' || !scan.toExamineBytes) return null;
	return Math.min(1, (scan.issuedBytes ?? 0) / scan.toExamineBytes);
}

/** "Resilver finished", "Scrub running", "Scrub canceled". */
export function describeScan(scan: PoolScan): string {
	const what = titleCase(scan.function === 'ERRORSCRUB' ? 'error scrub' : scan.function);
	const state = { SCANNING: 'running', FINISHED: 'finished', CANCELED: 'canceled' }[scan.state] ?? scan.state.toLowerCase();
	return `${what} ${state}`;
}

/**
 * The vdevs grouped under what they sit in ("raidz2-0", "spare-5", "spares"), in the order
 * zpool listed them, which is the pool's own layout order.
 */
export function vdevGroups(pool: PoolStatus): { parent: string; vdevs: VdevStatus[] }[] {
	const groups = new Map<string, VdevStatus[]>();
	for (const vdev of pool.vdevs) {
		const parent = vdev.class === 'spare' && vdev.parent === null ? 'spares' : (vdev.parent ?? pool.name);
		groups.set(parent, [...(groups.get(parent) ?? []), vdev]);
	}
	return [...groups].map(([parent, vdevs]) => ({ parent, vdevs }));
}

const BYTE_UNITS = ['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB'];

/** Binary units, as zpool list prints them. */
export function formatBytes(bytes: number | null): string {
	if (bytes === null) return 'n/a';
	let value = bytes;
	let unit = 0;
	while (value >= 1024 && unit < BYTE_UNITS.length - 1) {
		value /= 1024;
		unit++;
	}
	return `${value.toFixed(value >= 100 || unit === 0 ? 0 : 1)} ${BYTE_UNITS[unit]}`;
}

function titleCase(word: string): string {
	const lower = word.toLowerCase();
	return lower.charAt(0).toUpperCase() + lower.slice(1);
}
