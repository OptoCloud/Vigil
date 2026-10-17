<script lang="ts">
	// One filter row, above everything it scopes: every chart and trend on the page follows it,
	// and the choice carries over to the next page.
	import { getLive, RANGES } from '#lib/live.svelte.ts';
	import Segmented from './Segmented.svelte';

	const live = getLive();
</script>

<div class="flex flex-wrap items-center gap-3">
	<Segmented label="Time range" options={RANGES} value={live.range} onselect={(range) => live.selectRange(range)} />
	{#if live.historyError}
		<span class="text-faint" role="status">History unavailable: {live.historyError}</span>
	{/if}
</div>
