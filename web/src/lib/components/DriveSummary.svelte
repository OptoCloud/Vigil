<script lang="ts">
	// The drives at a glance, for the dashboard: how many, and the ones that need looking at.
	import { resolve } from '$app/paths';
	import { driveRows, shortWwn, smartVerdict } from '#lib/drives.ts';
	import type { DriveState, SensorReading } from '#lib/types.ts';
	import StatusBadge from './StatusBadge.svelte';

	let { sensors, drives }: { sensors: SensorReading[]; drives: DriveState[] } = $props();

	const rows = $derived(driveRows(sensors, drives, null).map((row) => ({ ...row, smart: smartVerdict(row.health) })));
	const attention = $derived(rows.filter((row) => row.smart.level === 'warning' || row.smart.level === 'critical'));
	const unknown = $derived(rows.filter((row) => row.smart.level === 'unknown').length);
	const asleep = $derived(rows.filter((row) => row.sensor?.celsiusOrNull == null).length);
</script>

{#if rows.length === 0}
	<p class="text-faint">No drives reported yet.</p>
{:else}
	<div class="flex flex-col gap-2">
		<StatusBadge
			class="font-semibold"
			level={attention.length === 0 ? 'good' : attention.some((row) => row.smart.level === 'critical') ? 'critical' : 'warning'}
			label={attention.length === 0
				? `${rows.length} drives, SMART passing`
				: `${attention.length} of ${rows.length} drives need attention`}
		/>
		{#if attention.length > 0}
			<ul class="flex flex-col">
				{#each attention as row (row.wwn)}
					<li class="flex items-center justify-between gap-3 border-b border-grid py-1.5 last:border-b-0">
						<a
							class="font-medium tabular-nums underline-offset-4 hover:underline"
							href={resolve('/drives/[wwn]', { wwn: row.wwn })}
							title={row.wwn}>{shortWwn(row.wwn)}</a
						>
						<StatusBadge level={row.smart.level} label={row.smart.label} />
					</li>
				{/each}
			</ul>
		{/if}
		<p class="text-xs text-faint">
			{#if asleep > 0}{asleep} asleep or unreadable now.{/if}
			{#if unknown > 0}{unknown} with no SMART result yet.{/if}
		</p>
	</div>
{/if}
