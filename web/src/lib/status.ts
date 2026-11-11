// The header's one verdict (ADR-019). First whether what the page shows is live: the stream to
// vigil-core, vigil-core's link to vigild, and the snapshot itself, in that order, the first
// problem winning. Then everything vigil-core has raised. Green means nothing is raised, not
// just that the fans are fine: a DEGRADED pool once sat behind a page that said Live for nine days.

import { relativeTime } from '#lib/chart/scale.ts';
import type { Alert, Snapshot } from '#lib/types.ts';

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

const RANK = { good: 0, warning: 1, critical: 2 } as const;

/**
 * The connection's state and every raised condition, as one verdict: whichever is worse, the
 * connection on a tie, since a page that is not live cannot vouch for anything it shows. The
 * detail names the worst condition and how many others there are.
 */
export function verdict(connection: ConnectionStatus, alerts: Alert[]): ConnectionStatus {
	const worst = alerts.reduce<Alert | null>((a, b) => (a && RANK[levelOf(a)] >= RANK[levelOf(b)] ? a : b), null);
	if (!worst || RANK[levelOf(worst)] <= RANK[connection.level]) return connection;

	const others = alerts.length - 1;
	return {
		level: levelOf(worst),
		label: alerts.length === 1 ? (worst.severity === 'critical' ? 'Critical' : 'Warning') : `${alerts.length} problems`,
		detail: others === 0 ? worst.message : `${worst.message} And ${others} other ${others === 1 ? 'problem' : 'problems'}.`
	};
}

/** An info alert cannot be raised, but the type allows it: it reads as a warning rather than as nothing. */
const levelOf = (alert: Alert): 'warning' | 'critical' => (alert.severity === 'critical' ? 'critical' : 'warning');
