// What the host-load charts and tiles draw (ADR-025): history from /api/history with the live
// load from the `host` message appended, in the units the charts read. Pure, so the pages only
// lay it out.
//
// Memory is charted in GiB and traffic in MB/s rather than in bytes, because the chart prints
// a value to one decimal with its unit after it: 46384140288 B says nothing at a glance.

import { MAX_SERIES, withLivePoint } from '#lib/series.ts';
import type { ChartSeries, HistoryResponse, HostLoad, HostMetric, SeriesPoints, ZfsStatus } from '#lib/types.ts';

export const GIB = 2 ** 30;
/** Decimal, as disk and network throughput are quoted everywhere else. */
export const MB = 1e6;

const color = (n: number) => `var(--series-${n})`;
const timeOf = (host: HostLoad | null) => (host ? Date.parse(host.asOf) : 0);
const scaled = (points: SeriesPoints | undefined, divisor: number): SeriesPoints | undefined =>
	points?.map(([time, value]) => [time, value / divisor]);

/** One chart line from a host metric: its history, scaled, with the live value at the end. */
function hostLine(
	history: HistoryResponse | null,
	host: HostLoad | null,
	now: number,
	line: { id: HostMetric; label: string; color: string; live: number | null | undefined; divisor?: number }
): ChartSeries {
	const divisor = line.divisor ?? 1;
	const live = line.live == null ? line.live : line.live / divisor;
	return {
		id: line.id,
		label: line.label,
		color: line.color,
		points: withLivePoint(scaled(history?.host[line.id], divisor), live, timeOf(host), now)
	};
}

/** Used, the ARC, cache and swap in GiB. The ARC is inside "used": Linux does not count it as cache. */
export function memorySeries(history: HistoryResponse | null, host: HostLoad | null, now: number): ChartSeries[] {
	const memory = host?.memory;
	const line = (id: HostMetric, label: string, n: number, live: number | null | undefined) =>
		hostLine(history, host, now, { id, label, color: color(n), live, divisor: GIB });
	return [
		line('memoryUsed', 'Used', 1, memory?.usedBytes),
		line('arc', 'of which ARC', 3, memory?.arcBytes),
		line('memoryCached', 'Cache', 4, memory?.cachedBytes),
		line('swapUsed', 'Swap used', 2, memory?.swapUsedBytes),
		// The most the ARC may grow to: the used line can only fall by as much as the ARC gives back.
		line('arcMax', 'ARC limit', 5, memory?.arcMaxBytes)
	];
}

/**
 * The memory chart's axis: 0 to the most RAM the host had in the window, so a full host reads as
 * a full chart, and a host that lost a DIMM does not draw last month off the top.
 */
export function memoryDomain(history: HistoryResponse | null, host: HostLoad | null): [number, number] | undefined {
	const totals = [...(history?.host.memoryTotal ?? []).map(([, bytes]) => bytes), ...(host?.memory ? [host.memory.totalBytes] : [])];
	return totals.length > 0 ? [0, Math.max(...totals) / GIB] : undefined;
}

export function cpuSeries(history: HistoryResponse | null, host: HostLoad | null, now: number): ChartSeries[] {
	return [
		hostLine(history, host, now, { id: 'cpuBusy', label: 'Busy', color: color(1), live: host?.cpu?.busyPercent }),
		hostLine(history, host, now, { id: 'cpuIowait', label: 'Waiting on I/O', color: color(2), live: host?.cpu?.iowaitPercent })
	];
}

export function loadSeries(history: HistoryResponse | null, host: HostLoad | null, now: number): ChartSeries[] {
	const load = host?.load;
	return [
		hostLine(history, host, now, { id: 'load1', label: '1 min', color: color(1), live: load?.one }),
		hostLine(history, host, now, { id: 'load5', label: '5 min', color: color(3), live: load?.five }),
		hostLine(history, host, now, { id: 'load15', label: '15 min', color: color(5), live: load?.fifteen })
	];
}

export function pressureSeries(history: HistoryResponse | null, host: HostLoad | null, now: number): ChartSeries[] {
	const pressure = host?.pressure;
	return [
		hostLine(history, host, now, { id: 'pressureCpu', label: 'CPU', color: color(1), live: pressure?.cpuPercent }),
		hostLine(history, host, now, { id: 'pressureMemory', label: 'Memory', color: color(4), live: pressure?.memoryPercent }),
		hostLine(history, host, now, { id: 'pressureIo', label: 'I/O', color: color(2), live: pressure?.ioPercent })
	];
}

/** PSI's "full": the time everything runnable was stalled at once. Coloured as "some" is. */
export function pressureFullSeries(history: HistoryResponse | null, host: HostLoad | null, now: number): ChartSeries[] {
	const pressure = host?.pressure;
	return [
		hostLine(history, host, now, { id: 'pressureCpuFull', label: 'CPU', color: color(1), live: pressure?.cpuFullPercent }),
		hostLine(history, host, now, { id: 'pressureMemoryFull', label: 'Memory', color: color(4), live: pressure?.memoryFullPercent }),
		hostLine(history, host, now, { id: 'pressureIoFull', label: 'I/O', color: color(2), live: pressure?.ioFullPercent })
	];
}

/** Every name with history under `prefix` or reporting live now, in name order. */
function namesOf(history: Record<string, SeriesPoints> | undefined, prefix: string, live: string[]): string[] {
	const stored = Object.keys(history ?? {})
		.filter((id) => id.startsWith(prefix))
		.map((id) => id.slice(prefix.length));
	return [...new Set([...stored, ...live])].sort();
}

/**
 * One line per pool, the first MAX_SERIES of them, coloured by the pool's place in name order
 * so it is the same colour on every pool chart. `direction` picks read or written.
 */
export function poolTrafficSeries(
	history: HistoryResponse | null,
	host: HostLoad | null,
	now: number,
	direction: 'read' | 'written'
): ChartSeries[] {
	const stored = direction === 'read' ? history?.trafficIn : history?.trafficOut;
	const pools = host?.pools ?? [];
	return namesOf(
		stored,
		'pool:',
		pools.map((pool) => pool.name)
	)
		.slice(0, MAX_SERIES)
		.map((name, index) => {
			const pool = pools.find((p) => p.name === name);
			const live = direction === 'read' ? pool?.readBytesPerSecond : pool?.writtenBytesPerSecond;
			return {
				id: `pool:${name}`,
				label: name,
				color: color(index + 1),
				points: withLivePoint(scaled(stored?.[`pool:${name}`], MB), live == null ? live : live / MB, timeOf(host), now)
			};
		});
}

/** Received and transmitted for each published interface, as many interfaces as two lines each fit. */
export function networkSeries(history: HistoryResponse | null, host: HostLoad | null, now: number): ChartSeries[] {
	const links = host?.interfaces ?? [];
	const names = namesOf(
		history?.trafficIn,
		'net:',
		links.map((link) => link.name)
	).slice(0, MAX_SERIES / 2);
	return names.flatMap((name, index) => {
		const link = links.find((l) => l.name === name);
		const line = (direction: 'in' | 'out', live: number | undefined, n: number): ChartSeries => {
			const stored = direction === 'in' ? history?.trafficIn : history?.trafficOut;
			return {
				id: `net:${name}:${direction}`,
				label: `${name} ${direction === 'in' ? 'received' : 'sent'}`,
				color: color(n),
				points: withLivePoint(scaled(stored?.[`net:${name}`], MB), live == null ? live : live / MB, timeOf(host), now)
			};
		};
		return [line('in', link?.receivedBytesPerSecond, index * 2 + 1), line('out', link?.transmittedBytesPerSecond, index * 2 + 2)];
	});
}

/** One disk's read and write throughput, in MB/s. */
export function diskTrafficSeries(history: HistoryResponse | null, host: HostLoad | null, deviceName: string, now: number): ChartSeries[] {
	const disk = host?.disks.find((d) => d.deviceName === deviceName);
	const id = `disk:${deviceName}`;
	const at = timeOf(host);
	return [
		{
			id: `${id}:read`,
			label: 'Read',
			color: color(1),
			points: withLivePoint(scaled(history?.trafficIn[id], MB), disk && disk.readBytesPerSecond / MB, at, now)
		},
		{
			id: `${id}:written`,
			label: 'Written',
			color: color(2),
			points: withLivePoint(scaled(history?.trafficOut[id], MB), disk && disk.writtenBytesPerSecond / MB, at, now)
		}
	];
}

export function diskBusySeries(history: HistoryResponse | null, host: HostLoad | null, deviceName: string, now: number): ChartSeries[] {
	const disk = host?.disks.find((d) => d.deviceName === deviceName);
	return [
		{
			id: `disk:${deviceName}:busy`,
			label: 'Busy',
			color: color(3),
			points: withLivePoint(history?.busy[`disk:${deviceName}`], disk?.busyPercent, timeOf(host), now)
		}
	];
}

/** A pool's fill, percent of its raw size allocated, from one zpool poll. Null when zpool could not size it. */
export function fillPercent(pool: { sizeBytes: number | null; allocatedBytes: number | null }): number | null {
	return pool.sizeBytes && pool.allocatedBytes !== null ? (pool.allocatedBytes / pool.sizeBytes) * 100 : null;
}

/** Each pool's fill over time, coloured as on the traffic charts. */
export function poolFillSeries(history: HistoryResponse | null, zfs: ZfsStatus | null | undefined, now: number): ChartSeries[] {
	const pools = zfs?.isAvailable ? zfs.pools : [];
	const at = zfs ? Date.parse(zfs.asOf) : 0;
	return [...new Set([...Object.keys(history?.poolFill ?? {}), ...pools.map((pool) => pool.name)])]
		.sort()
		.slice(0, MAX_SERIES)
		.map((name, index) => {
			const pool = pools.find((p) => p.name === name);
			return {
				id: `pool:${name}`,
				label: name,
				color: color(index + 1),
				points: withLivePoint(history?.poolFill[name], pool ? fillPercent(pool) : null, at, now)
			};
		});
}

/** The fullest pool now, for the overview's tile; null without one zpool could size. */
export function fullestPool(zfs: ZfsStatus | null | undefined): { name: string; percent: number } | null {
	const sized = (zfs?.isAvailable ? zfs.pools : [])
		.map((pool) => ({ name: pool.name, percent: fillPercent(pool) }))
		.filter((pool): pool is { name: string; percent: number } => pool.percent !== null);
	return sized.sort((a, b) => b.percent - a.percent)[0] ?? null;
}

/** Memory used as a percent of the host's RAM: the tile's value, and its history for the trend line. */
export function memoryUsedPercent(
	history: HistoryResponse | null,
	host: HostLoad | null,
	now: number
): { value: number | null; points: SeriesPoints } {
	const memory = host?.memory;
	if (!memory || memory.totalBytes <= 0) return { value: null, points: [] };
	const value = (memory.usedBytes / memory.totalBytes) * 100;
	// Each bucket against its own total, stored beside it; today's only where none was.
	const totals = new Map(history?.host.memoryTotal ?? []);
	const points: SeriesPoints = (history?.host.memoryUsed ?? []).map(([time, used]) => {
		const total = totals.get(time) ?? memory.totalBytes;
		return [time, total > 0 ? (used / total) * 100 : 0];
	});
	return { value, points: withLivePoint(points, value, timeOf(host), now) };
}

/** "44.0 GiB", "512 MiB": a byte count to the nearest binary unit, for the line under a tile. */
export function formatBytes(bytes: number): string {
	const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB'];
	let value = bytes;
	let unit = 0;
	while (value >= 1024 && unit < units.length - 1) {
		value /= 1024;
		unit++;
	}
	return `${value.toFixed(value >= 100 || unit === 0 ? 0 : 1)} ${units[unit]}`;
}
