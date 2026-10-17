<script lang="ts">
	// The UPS at a glance, for the dashboard. The power page has every reading.
	import type { UpsState } from '#lib/types.ts';
	import { formatRuntime, summarizeUps } from '#lib/ups.ts';
	import Meter from './Meter.svelte';
	import StatusBadge from './StatusBadge.svelte';

	let { ups }: { ups: UpsState } = $props();

	const summary = $derived(summarizeUps(ups));
	const reading = $derived(ups.reading);
</script>

<div class="flex flex-col gap-2">
	<StatusBadge class="font-semibold" level={summary.level} label={summary.label} />
	{#if reading}
		{#each [{ label: 'Battery', value: reading.batteryCharge }, { label: 'Load', value: reading.load }] as row (row.label)}
			<div class="flex items-center gap-3">
				<span class="w-16 text-muted-foreground">{row.label}</span>
				{#if row.value !== null}<Meter class="flex-1" label="UPS {row.label.toLowerCase()}" percent={row.value} />{/if}
				<span class="ml-auto w-10 text-right font-semibold tabular-nums">{row.value === null ? 'n/a' : `${Math.round(row.value)}%`}</span>
			</div>
		{/each}
		<p class="text-xs text-faint">
			{reading.batteryRuntimeSeconds === null
				? 'Runtime not reported'
				: `${formatRuntime(reading.batteryRuntimeSeconds)} of runtime at this load`}
		</p>
	{:else}
		<p class="text-faint">{ups.error ? `No reading: ${ups.error}.` : 'Connecting to upsd.'}</p>
	{/if}
</div>
