<script lang="ts">
	// The ZFS pools, as vigild's last `zpool status` saw them (ADR-018).
	import { relativeTime } from '#lib/chart/scale.ts';
	import PoolCard from '#lib/components/PoolCard.svelte';
	import { getLive } from '#lib/live.svelte.ts';

	const live = getLive();

	const zfs = $derived(live.snapshot?.zfs);
</script>

<svelte:head><title>Storage · vigil</title></svelte:head>

{#if !live.snapshot}
	<p class="text-faint">Waiting for data.</p>
{:else if !zfs}
	<p class="text-faint">vigild is not reporting ZFS: its [zfs] section is off, or it has not polled yet.</p>
{:else if !zfs.isAvailable}
	<p class="text-faint">
		zpool gave no usable answer at its last poll ({relativeTime(zfs.asOf, live.now)}), so the pools' state is unknown. vigild's journal says
		why.
	</p>
{:else}
	<p class="text-xs text-faint">Polled {relativeTime(zfs.asOf, live.now)}.</p>
	{#each zfs.pools as pool (pool.name)}
		<PoolCard {pool} now={live.now} />
	{:else}
		<p class="text-faint">No pools imported.</p>
	{/each}
{/if}
