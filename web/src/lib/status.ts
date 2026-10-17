// Whether what the page shows is live, as one verdict for the header: the stream to vigil-core,
// vigil-core's link to vigild, and the snapshot itself, in that order, the first problem winning.

import { relativeTime } from '#lib/chart/scale.ts';
import type { Snapshot } from '#lib/types.ts';

export type StreamState = 'connecting' | 'open' | 'lost';

/**
 * How old the newest snapshot may be before the page says it is stale. vigild publishes one
 * every few seconds; three missed in a row is not jitter.
 */
export const STALE_AFTER_MS = 15_000;

export interface ConnectionStatus {
	level: 'good' | 'warning' | 'critical';
	label: string;
	/** Why, for the banner. Empty when all is well. */
	detail: string;
}

export function connectionStatus(stream: StreamState, daemonConnected: boolean, snapshot: Snapshot | null, now: number): ConnectionStatus {
	if (stream === 'connecting') return { level: 'warning', label: 'Connecting', detail: 'Waiting for the live stream from vigil-core.' };
	if (stream === 'lost')
		return {
			level: 'critical',
			label: 'vigil-core unreachable',
			detail: 'This page cannot reach vigil-core, so nothing shown is live. It reconnects on its own.'
		};
	if (!daemonConnected)
		return {
			level: 'critical',
			label: 'Daemon unreachable',
			detail: 'vigil-core cannot reach vigild. If it is not running, the fans are on BIOS control.'
		};
	if (!snapshot) return { level: 'warning', label: 'Waiting for data', detail: 'Connected, but no snapshot has arrived yet.' };
	if (now - new Date(snapshot.timestampUtc).getTime() > STALE_AFTER_MS)
		return { level: 'warning', label: 'Stale', detail: `The last snapshot is ${relativeTime(snapshot.timestampUtc, now)} old.` };
	if (!snapshot.controlLoopHealthy)
		return {
			level: 'critical',
			label: 'Control loop unhealthy',
			detail: 'A fan channel could not be driven, or the loop has stopped polling.'
		};
	return { level: 'good', label: 'Live', detail: '' };
}
