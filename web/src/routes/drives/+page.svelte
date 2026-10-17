<script lang="ts">
	// Every disk, by itself or by the bay it sits in. Each one links to its own page.
	import DriveTable from '#lib/components/DriveTable.svelte';
	import Panel from '#lib/components/Panel.svelte';
	import RangeBar from '#lib/components/RangeBar.svelte';
	import Segmented from '#lib/components/Segmented.svelte';
	import { getLive } from '#lib/live.svelte.ts';

	const live = getLive();

	let view = $state<'drive' | 'bay'>('drive');

	const VIEWS = [
		{ key: 'drive', label: 'By drive' },
		{ key: 'bay', label: 'By bay' }
	] as const;
</script>

<svelte:head><title>Drives · vigil</title></svelte:head>

<RangeBar />

<Panel title="Drives">
	{#snippet actions()}
		<Segmented label="Group temperatures by" options={VIEWS} value={view} onselect={(key) => (view = key)} />
	{/snippet}
	<DriveTable sensors={live.snapshot?.sensors ?? []} drives={live.drives} history={live.history} now={live.now} {view} />
</Panel>
