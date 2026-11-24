<script lang="ts">
	// One filter row, above everything it scopes: every chart and trend on the page follows it,
	// and the choice carries over to the next page. The window ends now and follows the clock,
	// unless it is stepped back to look at an earlier one.
	import { formatInstant } from '#lib/chart/scale.ts';
	import { getLive, RANGE_MS, RANGES } from '#lib/live.svelte.ts';
	import { Button } from './ui/button/index.ts';
	import Segmented from './Segmented.svelte';

	const live = getLive();

	const span = $derived(RANGE_MS[live.range]);
</script>

<div class="flex flex-wrap items-center gap-3">
	<Segmented label="Time range" options={RANGES} value={live.range} onselect={(range) => live.selectRange(range)} />
	<div class="flex items-center gap-1.5" role="group" aria-label="Which window">
		<Button variant="outline" size="xs" onclick={() => live.stepHistory(-1)}>Earlier</Button>
		<Button variant="outline" size="xs" disabled={live.historyEnd === null} onclick={() => live.stepHistory(1)}>Later</Button>
		<Button variant="outline" size="xs" disabled={live.historyEnd === null} onclick={() => live.followNow()}>Now</Button>
	</div>
	{#if live.historyEnd !== null}
		<!-- Said in words: a chart of last Tuesday must not pass for a chart of now. -->
		<span class="text-xs text-muted-foreground" role="status">
			{formatInstant(live.historyEnd - span, span)} to {formatInstant(live.historyEnd, span)}, not live
		</span>
	{/if}
	{#if live.historyError}
		<span class="text-faint" role="status">History unavailable: {live.historyError}</span>
	{/if}
</div>
