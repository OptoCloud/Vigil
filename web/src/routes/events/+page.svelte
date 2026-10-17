<script lang="ts">
	// Everything vigil-core has raised or cleared: the newest from its log, then what streamed in.
	import EventLog from '#lib/components/EventLog.svelte';
	import Panel from '#lib/components/Panel.svelte';
	import Segmented from '#lib/components/Segmented.svelte';
	import { getLive } from '#lib/live.svelte.ts';
	import type { Severity } from '#lib/types.ts';

	const live = getLive();

	const FILTERS: { key: 'all' | 'warning' | 'critical'; label: string; severities?: Severity[] }[] = [
		{ key: 'all', label: 'All' },
		{ key: 'warning', label: 'Warnings and worse', severities: ['warning', 'critical'] },
		{ key: 'critical', label: 'Critical', severities: ['critical'] }
	];

	let filter = $state<'all' | 'warning' | 'critical'>('all');
</script>

<svelte:head><title>Events · vigil</title></svelte:head>

<Panel title="Events" subtitle="The newest {live.events.length}, live">
	{#snippet actions()}
		<Segmented label="Severity" options={FILTERS} value={filter} onselect={(key) => (filter = key)} />
	{/snippet}
	<EventLog events={live.events} now={live.now} severities={FILTERS.find((f) => f.key === filter)?.severities} />
</Panel>
