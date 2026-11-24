import { describe, expect, it } from 'vitest';
import { historyUrl, Live, MAX_EVENTS, RANGE_MS, steppedEnd } from './live.svelte';
import type { EventRecord } from './types';

const event = (id: number): EventRecord => ({
	id,
	ts: '2026-10-17T00:00:00.000Z',
	severity: 'info',
	kind: `k${id}`,
	message: `event ${id}`
});

describe('Live', () => {
	it('puts a streamed event first and keeps a bounded log', () => {
		const live = new Live();
		for (let id = 1; id <= MAX_EVENTS + 5; id++) live.apply({ type: 'event', event: event(id) });
		expect(live.events).toHaveLength(MAX_EVENTS);
		expect(live.events[0].id).toBe(MAX_EVENTS + 5);
	});

	it('merges the fetched log behind the live events without showing one twice', () => {
		const live = new Live();
		live.apply({ type: 'event', event: event(3) });
		live.mergeEvents([event(3), event(2), event(1)]);
		expect(live.events.map((e) => e.id)).toEqual([3, 2, 1]);
	});

	it('takes each kind of message into its own state', () => {
		const live = new Live();
		live.apply({ type: 'daemon', connected: true });
		live.apply({ type: 'drives', drives: [] });
		expect(live.daemonConnected).toBe(true);
		expect(live.snapshotAt).toBe(0);
	});
});

describe("the charts' window", () => {
	const now = Date.parse('2026-11-24T12:00:00.000Z');

	it('ends now unless it has been stepped back', () => {
		expect(historyUrl('24h', null)).toBe('/api/history?range=24h');
		expect(historyUrl('24h', now - RANGE_MS['24h'])).toBe(`/api/history?range=24h&to=${now - RANGE_MS['24h']}`);
	});

	it('steps back a whole window at a time', () => {
		const once = steppedEnd(null, '6h', -1, now);
		expect(once).toBe(now - RANGE_MS['6h']);
		expect(steppedEnd(once, '6h', -1, now)).toBe(now - 2 * RANGE_MS['6h']);
	});

	it('follows the clock again once a step later reaches the present', () => {
		const twoBack = now - 2 * RANGE_MS['1h'];
		expect(steppedEnd(twoBack, '1h', 1, now)).toBe(now - RANGE_MS['1h']);
		expect(steppedEnd(now - RANGE_MS['1h'], '1h', 1, now)).toBeNull();
		// Nothing is later than now.
		expect(steppedEnd(null, '1h', 1, now)).toBeNull();
	});
});
