<script lang="ts">
	// The ZFS pools, as vigild's last `zpool status` saw them (ADR-018).
	import { relativeTime } from '#lib/chart/scale.ts';
	import LineChart from '#lib/components/LineChart.svelte';
	import PoolCard from '#lib/components/PoolCard.svelte';
	import RangeBar from '#lib/components/RangeBar.svelte';
	import { poolFillSeries, poolTrafficSeries } from '#lib/host.ts';
	import { getLive } from '#lib/live.svelte.ts';
	import { chartWindow } from '#lib/series.ts';

	const live = getLive();

	const zfs = $derived(live.snapshot?.zfs);
	const plotWindow = $derived(chartWindow(live.history, live.snapshotAt, live.now));
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
		<PoolCard {pool} lastScrub={live.pools.find((known) => known.name === pool.name)?.lastScrub ?? null} now={live.now} />
	{:else}
		<p class="text-faint">No pools imported.</p>
	{/each}
{/if}

{#if zfs}
	<RangeBar />
	<!-- Fill from zpool's raw numbers, parity included: the ratio is right, but a raidz2's bytes are
	     not what it can store, so the chart is in percent. Throughput is each pool's disks summed. -->
	<LineChart
		title="Pool fill"
		series={poolFillSeries(live.history, zfs, live.now)}
		from={plotWindow.from}
		to={plotWindow.to}
		unit="%"
		yDomain={[0, 100]}
		loading={live.historyLoading}
	/>
	<div class="grid grid-cols-[repeat(auto-fit,minmax(min(100%,440px),1fr))] gap-3">
		<LineChart
			title="Pool reads"
			series={poolTrafficSeries(live.history, live.host, live.now, 'read')}
			from={plotWindow.from}
			to={plotWindow.to}
			unit=" MB/s"
			loading={live.historyLoading}
		/>
		<LineChart
			title="Pool writes"
			series={poolTrafficSeries(live.history, live.host, live.now, 'written')}
			from={plotWindow.from}
			to={plotWindow.to}
			unit=" MB/s"
			loading={live.historyLoading}
		/>
	</div>
{/if}
