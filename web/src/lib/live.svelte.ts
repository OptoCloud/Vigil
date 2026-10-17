// What every page shows, held once for the whole app: the root layout connects, and each page
// reads the same state, so moving between pages never drops the stream or refetches history.
//
// The page is a static build with no server of its own, so it starts empty: the live stream's
// first messages are vigil-core's full current state, and the event log is fetched once.

import { createContext } from 'svelte';
import type { DriveState, EventRecord, HistoryResponse, LiveMessage, RangeKey, Snapshot, UpsState } from '#lib/types.ts';
import type { StreamState } from '#lib/status.ts';

/** As many events as the page keeps: the log's first page plus what streamed in since. */
export const MAX_EVENTS = 100;

/** How often the history is refetched. vigil-core buckets it at 10 s and up, so faster gains nothing. */
const HISTORY_REFRESH_MS = 30_000;

export const RANGES: { key: RangeKey; label: string }[] = [
	{ key: '1h', label: 'Last hour' },
	{ key: '6h', label: '6 hours' },
	{ key: '24h', label: '24 hours' },
	{ key: '7d', label: '7 days' },
	{ key: '30d', label: '30 days' },
	{ key: '1y', label: '1 year' }
];

export class Live {
	snapshot = $state<Snapshot | null>(null);
	daemonConnected = $state(false);
	drives = $state<DriveState[]>([]);
	events = $state<EventRecord[]>([]);
	// Disabled, so hidden until vigil-core sends the real one: none of these values is ever shown.
	ups = $state<UpsState>({
		enabled: false,
		name: '',
		reading: null,
		error: null,
		limits: { hostShutdownSeconds: null, batteryTemperatureWarn: 0 }
	});

	// The stream IS the connection to vigil-core: whether EventSource is open is whether
	// vigil-core can be reached.
	stream = $state<StreamState>('connecting');
	now = $state(Date.now());

	range = $state<RangeKey>('1h');
	history = $state<HistoryResponse | null>(null);
	historyError = $state('');
	historyLoading = $state(false);

	readonly snapshotAt = $derived(this.snapshot ? Date.parse(this.snapshot.timestampUtc) : 0);

	/** Opens the stream and the clocks. Returns what closes them. */
	connect(): () => void {
		// EventSource reconnects on its own; onerror/onopen just keep the header honest about it.
		// vigil-core's keepalive is a named `keepalive` event, which onmessage never receives.
		const source = new EventSource('/api/live');
		source.onopen = () => (this.stream = 'open');
		source.onerror = () => (this.stream = 'lost');
		source.onmessage = (message) => this.apply(JSON.parse(message.data) as LiveMessage);

		void this.loadEvents();
		void this.loadHistory(this.range, true);

		const clock = setInterval(() => (this.now = Date.now()), 1000);
		const refresh = setInterval(() => void this.loadHistory(this.range, false), HISTORY_REFRESH_MS);
		return () => {
			source.close();
			clearInterval(clock);
			clearInterval(refresh);
		};
	}

	apply(live: LiveMessage) {
		if (live.type === 'snapshot') this.snapshot = live.snapshot;
		else if (live.type === 'daemon') this.daemonConnected = live.connected;
		else if (live.type === 'drives') this.drives = live.drives;
		else if (live.type === 'event') this.events = [live.event, ...this.events].slice(0, MAX_EVENTS);
		else if (live.type === 'ups') this.ups = live.ups;
	}

	selectRange(range: RangeKey) {
		this.range = range;
		void this.loadHistory(range, true);
	}

	/**
	 * The newest events, for what the stream cannot replay: it carries only events raised after
	 * it connects. Merged by id, so one raised between the two requests is not shown twice.
	 * vigil-core answers [] when the database is down, which leaves the live ones in place.
	 */
	async loadEvents() {
		try {
			const response = await fetch('/api/events');
			if (!response.ok) return;
			this.mergeEvents((await response.json()) as EventRecord[]);
		} catch {
			// The header already says when vigil-core cannot be reached.
		}
	}

	mergeEvents(logged: EventRecord[]) {
		const live = this.events.map((event) => event.id);
		this.events = [...this.events, ...logged.filter((event) => !live.includes(event.id))].slice(0, MAX_EVENTS);
	}

	async loadHistory(requested: RangeKey, showLoading: boolean) {
		if (showLoading) this.historyLoading = true;
		try {
			const response = await fetch(`/api/history?range=${requested}`);
			// vigil-core explains a failure in plain text ("History is unavailable: ...").
			if (!response.ok) throw new Error((await response.text().catch(() => '')).trim() || `HTTP ${response.status}`);
			const result = (await response.json()) as HistoryResponse;
			// A slow response for a range the user has since left must not overwrite the current one.
			if (requested === this.range) {
				this.history = result;
				this.historyError = '';
			}
		} catch (error) {
			if (requested === this.range) this.historyError = error instanceof Error ? error.message : String(error);
		} finally {
			if (requested === this.range) this.historyLoading = false;
		}
	}
}

export const [getLive, setLive] = createContext<Live>();
