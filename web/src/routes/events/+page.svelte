<script lang="ts">
	// Everything vigil-core has raised or cleared: the newest from its log, then what streamed in,
	// and further back a page at a time for whoever asks.
	import EventLog from '#lib/components/EventLog.svelte';
	import Panel from '#lib/components/Panel.svelte';
	import Segmented from '#lib/components/Segmented.svelte';
	import { Button } from '#lib/components/ui/button/index.ts';
	import { appendOlder, OLDER_PAGE_SIZE, oldestLoggedId, olderEventsUrl } from '#lib/events.ts';
	import { getLive } from '#lib/live.svelte.ts';
	import type { EventRecord, Severity } from '#lib/types.ts';

	const live = getLive();

	type Filter = 'all' | 'warning' | 'critical';
	const FILTERS: { key: Filter; label: string; severities?: Severity[] }[] = [
		{ key: 'all', label: 'All' },
		{ key: 'warning', label: 'Warnings and worse', severities: ['warning', 'critical'] },
		{ key: 'critical', label: 'Critical', severities: ['critical'] }
	];

	let filter = $state<Filter>('all');

	// Pages fetched from further back, behind the live list. They were asked for under one
	// filter, so a different filter starts again: a page of criticals has gaps as "all".
	let older = $state<EventRecord[]>([]);
	let loading = $state(false);
	let exhausted = $state(false);
	let problem = $state<string | null>(null);

	const severities = $derived(FILTERS.find((f) => f.key === filter)?.severities);
	const events = $derived(appendOlder(live.events, older));
	const oldest = $derived(oldestLoggedId(events));

	function select(key: Filter) {
		filter = key;
		older = [];
		exhausted = false;
		problem = null;
	}

	async function loadOlder() {
		if (oldest === null || loading) return;
		loading = true;
		problem = null;
		try {
			const response = await fetch(olderEventsUrl(oldest, filter === 'all' ? undefined : filter));
			if (!response.ok) throw new Error(`HTTP ${response.status}`);
			const page = (await response.json()) as EventRecord[];
			older = appendOlder(older, page);
			// A short page is the end of the log (or a database that is down, which answers []).
			exhausted = page.length < OLDER_PAGE_SIZE;
		} catch {
			problem = 'Older events could not be loaded.';
		} finally {
			loading = false;
		}
	}
</script>

<svelte:head><title>Events · vigil</title></svelte:head>

<Panel title="Events" subtitle="The newest {events.length}, live">
	{#snippet actions()}
		<Segmented label="Severity" options={FILTERS} value={filter} onselect={select} />
	{/snippet}
	<EventLog {events} now={live.now} {severities} />
	{#if oldest !== null}
		<div class="mt-3 flex items-center gap-3 text-xs text-faint">
			{#if exhausted}
				<span>That is the whole log.</span>
			{:else}
				<Button variant="outline" size="xs" disabled={loading} onclick={loadOlder}>{loading ? 'Loading…' : 'Older events'}</Button>
			{/if}
			{#if problem}<span role="alert">{problem}</span>{/if}
		</div>
	{/if}
</Panel>
