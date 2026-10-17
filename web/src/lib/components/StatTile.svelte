<script lang="ts">
	import type { Snippet } from 'svelte';
	import * as Card from '#lib/components/ui/card/index.ts';
	import type { SeriesPoints } from '#lib/types.ts';
	import Sparkline from './Sparkline.svelte';

	interface Props {
		label: string;
		/** null: the sensor is currently unreadable. */
		celsius: number | null;
		/** History for the selected range, drawn as a trend line and summarised as a min/max. */
		points: SeriesPoints;
		/** Replaces the min/max line: which drive is the hottest, as a link to it. */
		detail?: Snippet;
	}

	let { label, celsius, points, detail }: Props = $props();

	const range = $derived.by(() => {
		if (points.length === 0) return null;
		const values = points.map((point) => point[1]);
		return { min: Math.min(...values), max: Math.max(...values) };
	});
</script>

<Card.Root size="sm" class="gap-0.5 px-3.5">
	<div class="text-xs text-muted-foreground">{label}</div>
	<div class="flex items-center justify-between gap-2">
		<!-- Proportional figures on purpose: tabular digits make a large standalone number look loose. -->
		<div class="text-[26px] leading-tight font-semibold whitespace-nowrap">
			{#if celsius === null}
				<span class="text-faint">n/a</span>
			{:else}
				{celsius.toFixed(celsius >= 100 ? 0 : 1)}<span class="ml-0.5 text-sm font-normal text-muted-foreground">°C</span>
			{/if}
		</div>
		<Sparkline {points} width={120} height={32} decorative />
	</div>
	<div class="text-[11px] text-faint tabular-nums">
		{#if detail}{@render detail()}{:else if range}{range.min.toFixed(0)} to {range.max.toFixed(0)}°C in range{:else}&nbsp;{/if}
	</div>
</Card.Root>
