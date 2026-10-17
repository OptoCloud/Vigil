import { describe, expect, it } from 'vitest';
import { Live, MAX_EVENTS } from './live.svelte';
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
