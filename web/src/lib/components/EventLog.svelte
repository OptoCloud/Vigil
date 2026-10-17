<script lang="ts">
	import { relativeTime } from '#lib/chart/scale.ts';
	import type { EventRecord, Severity } from '#lib/types.ts';
	import StatusBadge from './StatusBadge.svelte';

	interface Props {
		events: EventRecord[];
		now: number;
		/** Show only the newest this many: the dashboard's summary. Omitted: all of them. */
		limit?: number;
		/** Narrow to these severities: the events page's filter. Omitted: every severity. */
		severities?: Severity[];
	}

	let { events, now, limit, severities }: Props = $props();

	const label = { critical: 'Critical', warning: 'Warning', info: 'Info' } as const;

	const shown = $derived(events.filter((event) => !severities || severities.includes(event.severity)).slice(0, limit));
</script>

<ul class="m-0 list-none p-0">
	{#each shown as event (event.id)}
		<li class="grid grid-cols-[84px_1fr_auto] items-start gap-3 border-b border-grid py-1.75 last:border-b-0">
			<span class="pt-px text-xs"><StatusBadge level={event.severity} label={label[event.severity]} /></span>
			<!-- Messages come from the server's own templates, but they embed device names; text interpolation keeps them inert. -->
			<span>{event.message}</span>
			<time class="text-xs whitespace-nowrap text-faint tabular-nums" datetime={event.ts} title={new Date(event.ts).toLocaleString()}
				>{relativeTime(event.ts, now)}</time
			>
		</li>
	{:else}
		<li class="text-faint">
			{#if severities && events.length > 0}
				Nothing at this severity.
			{:else}
				Nothing has happened yet. Stalled fans, unreadable sensors, SMART changes, power events and daemon outages show up here.
			{/if}
		</li>
	{/each}
</ul>
