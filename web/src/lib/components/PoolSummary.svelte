<script lang="ts">
	// One line per pool, for the dashboard: its state, how full it is, and a scan that is running.
	// The storage page has the rest.
	import type { ZfsStatus } from '#lib/types.ts';
	import { describeScan, formatBytes, poolVerdict, scanProgress, usedPercent } from '#lib/zfs.ts';
	import Meter from './Meter.svelte';
	import StatusBadge from './StatusBadge.svelte';

	let { zfs }: { zfs: ZfsStatus | null | undefined } = $props();
</script>

{#if !zfs}
	<p class="text-faint">vigild is not reporting ZFS: its [zfs] section is off, or it has not polled yet.</p>
{:else if !zfs.isAvailable}
	<p class="text-faint">zpool gave no usable answer, so the pools' state is unknown. vigild's journal says why.</p>
{:else}
	<ul class="flex flex-col">
		{#each zfs.pools as pool (pool.name)}
			{@const verdict = poolVerdict(pool)}
			{@const progress = scanProgress(pool.scan)}
			{@const used = usedPercent(pool)}
			<li class="grid grid-cols-[minmax(4rem,auto)_1fr_auto] items-center gap-3 border-b border-grid py-2 last:border-b-0">
				<span class="font-semibold">{pool.name}</span>
				{#if used !== null}
					<Meter label="{pool.name} used" percent={used} />
				{:else}
					<span></span>
				{/if}
				<StatusBadge level={verdict.level} label={verdict.label} />
				<span class="col-span-3 -mt-1.5 text-xs text-faint tabular-nums">
					{formatBytes(pool.allocatedBytes)} of {formatBytes(pool.sizeBytes)}{#if pool.scan && progress !== null}, {describeScan(
							pool.scan
						).toLowerCase()}
						{(progress * 100).toFixed(1)}%{/if}
				</span>
			</li>
		{:else}
			<li class="text-faint">No pools imported.</li>
		{/each}
	</ul>
{/if}
