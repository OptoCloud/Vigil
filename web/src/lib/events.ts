// The event log beyond its first page: what the events page asks vigil-core for when someone
// wants to look further back than the newest fifty.
import type { EventRecord, Severity } from './types';

/** How many older events one request asks for. vigil-core allows up to 200. */
export const OLDER_PAGE_SIZE = 100;

/**
 * The id to page back from: the oldest event the database numbered. An event raised while the
 * database was down carries a negative id of the page's own, which the log has never heard of.
 * Null when nothing shown came from the log, so there is nothing to page back from.
 */
export function oldestLoggedId(events: EventRecord[]): number | null {
	const logged = events.map((event) => event.id).filter((id) => id > 0);
	return logged.length > 0 ? Math.min(...logged) : null;
}

/** The request for the page before `before`, narrowed to `minimum` and worse when given. */
export function olderEventsUrl(before: number, minimum?: Severity): string {
	const query = new URLSearchParams({ before: String(before), limit: String(OLDER_PAGE_SIZE) });
	if (minimum && minimum !== 'info') query.set('severity', minimum);
	return `/api/events?${query}`;
}

/** `older` appended to `shown`, without any event twice. */
export function appendOlder(shown: EventRecord[], older: EventRecord[]): EventRecord[] {
	const have = new Set(shown.map((event) => event.id));
	return [...shown, ...older.filter((event) => !have.has(event.id))];
}
