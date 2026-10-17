import { describe, expect, it } from 'vitest';
import { connectionStatus, STALE_AFTER_MS } from './status';
import type { Snapshot } from './types';

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
