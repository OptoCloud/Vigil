import { describe, expect, it } from 'vitest';
import { connectionStatus, STALE_AFTER_MS, verdict } from './status';
import type { Alert, Snapshot } from './types';

const NOW = 1_000_000_000;

const snapshot = (age: number, controlLoopHealthy = true): Snapshot => ({
	timestampUtc: new Date(NOW - age).toISOString(),
	sensors: [],
	fans: [],
	driveHealth: [],
	controlLoopHealthy
});

describe('connectionStatus', () => {
	it('blames the nearest broken link first', () => {
		expect(connectionStatus('lost', false, null, NOW).label).toBe('vigil-core unreachable');
		expect(connectionStatus('open', false, snapshot(0), NOW).label).toBe('Daemon unreachable');
		expect(connectionStatus('open', true, null, NOW).label).toBe('Waiting for data');
	});

	it('calls an old snapshot stale before judging what it says', () => {
		expect(connectionStatus('open', true, snapshot(STALE_AFTER_MS + 1, false), NOW).label).toBe('Stale');
		expect(connectionStatus('open', true, snapshot(0, false), NOW).level).toBe('critical');
	});

	it('is live with nothing to explain', () => {
		expect(connectionStatus('open', true, snapshot(1_000), NOW)).toEqual({ level: 'good', label: 'Live', detail: '' });
	});
});

const alert = (severity: Alert['severity'], message: string): Alert => ({
	kind: message,
	severity,
	message,
	since: '2026-10-08T04:47:30.000Z'
});

describe('verdict', () => {
	const live = connectionStatus('open', true, snapshot(1_000), NOW);

	it('is green only when nothing is raised', () => {
		expect(verdict(live, [])).toBe(live);
		expect(verdict(live, [alert('warning', 'Pool void has 1 of its 2 hot spares available.')]).level).toBe('warning');
	});

	it('names the worst condition and counts the rest', () => {
		const result = verdict(live, [
			alert('warning', 'One spare short.'),
			alert('critical', 'Pool void is DEGRADED.'),
			alert('critical', 'a FAULTED.')
		]);
		expect(result).toEqual({ level: 'critical', label: '3 problems', detail: 'Pool void is DEGRADED. And 2 other problems.' });
	});

	it('puts a connection problem first when it is as bad', () => {
		const lost = connectionStatus('open', false, snapshot(0), NOW);
		expect(verdict(lost, [alert('critical', 'Pool void is DEGRADED.')])).toBe(lost);
		const stale = connectionStatus('open', true, snapshot(STALE_AFTER_MS + 1), NOW);
		expect(verdict(stale, [alert('critical', 'Pool void is DEGRADED.')]).label).toBe('Critical');
	});
});
