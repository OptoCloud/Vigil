import { describe, expect, it } from 'vitest';
import { appendOlder, OLDER_PAGE_SIZE, oldestLoggedId, olderEventsUrl } from './events';
import type { EventRecord } from './types';

const event = (id: number): EventRecord => ({ id, ts: '2026-11-24T12:00:00.000Z', severity: 'warning', kind: `k${id}`, message: 'm' });

describe('oldestLoggedId', () => {
	it('is the smallest id the database assigned', () => {
		expect(oldestLoggedId([event(9), event(7), event(8)])).toBe(7);
	});

	it('ignores events raised while the database was down, which have no place in the log', () => {
		expect(oldestLoggedId([event(-1790000000000), event(12)])).toBe(12);
		expect(oldestLoggedId([event(-1790000000000)])).toBeNull();
		expect(oldestLoggedId([])).toBeNull();
	});
});

describe('olderEventsUrl', () => {
	it('asks for the page below an id', () => {
		expect(olderEventsUrl(41)).toBe(`/api/events?before=41&limit=${OLDER_PAGE_SIZE}`);
		expect(olderEventsUrl(41, 'info')).toBe(`/api/events?before=41&limit=${OLDER_PAGE_SIZE}`);
	});

	it('narrows to a severity and worse', () => {
		expect(olderEventsUrl(41, 'critical')).toBe(`/api/events?before=41&limit=${OLDER_PAGE_SIZE}&severity=critical`);
	});
});

describe('appendOlder', () => {
	it('adds the older page after what is shown, once each', () => {
		expect(appendOlder([event(5), event(4)], [event(4), event(3)]).map((e) => e.id)).toEqual([5, 4, 3]);
	});
});
