<script lang="ts">
	// The host's load (ADR-025): memory, CPU, load, pressure and the network, from vigild's
	// counters as vigil-core turns them into rates.
	import { formatBytes, cpuSeries, loadSeries, memoryDomain, memorySeries, networkSeries, pressureSeries } from '#lib/host.ts';
	import LineChart from '#lib/components/LineChart.svelte';
	import RangeBar from '#lib/components/RangeBar.svelte';
	import { getLive } from '#lib/live.svelte.ts';
	import { chartWindow } from '#lib/series.ts';

	const live = getLive();

	const plotWindow = $derived(chartWindow(live.history, live.snapshotAt, live.now));
	const network = $derived(networkSeries(live.history, live.host, live.now));
	const uptime = $derived(live.host?.uptimeSeconds ?? null);

	/** "12 days", "5 hours": uptime to the largest whole unit, which is all a glance needs. */
	function formatUptime(seconds: number): string {
		const units: [number, string][] = [
			[86_400, 'day'],
			[3_600, 'hour'],
			[60, 'minute']
		];
		for (const [size, name] of units) {
			const count = Math.floor(seconds / size);
			if (count >= 1) return `${count} ${name}${count === 1 ? '' : 's'}`;
		}
		return 'under a minute';
	}
</script>

<svelte:head><title>System · vigil</title></svelte:head>

<RangeBar />

{#if !live.host && live.snapshot}
	<p class="text-faint">vigild is not reporting the host's load: its [host] section is off, or it predates it.</p>
{:else if live.host}
	<p class="text-xs text-faint">
		{#if live.host.memory}{formatBytes(live.host.memory.totalBytes)} of RAM{/if}{#if live.host.cpu}, {live.host.cpu.logicalCpus} CPUs{/if}{#if uptime !== null},
			up {formatUptime(uptime)}{/if}.
	</p>
{/if}

<!-- One unit per chart, never a second y-scale: GiB, percent of CPU time, run-queue length and
     percent of time stalled are four different questions. -->
<LineChart
	title="Memory"
	series={memorySeries(live.history, live.host, live.now)}
	from={plotWindow.from}
	to={plotWindow.to}
	unit=" GiB"
	yDomain={memoryDomain(live.host)}
	loading={live.historyLoading}
/>

<div class="grid grid-cols-[repeat(auto-fit,minmax(min(100%,440px),1fr))] gap-3">
	<LineChart
		title="CPU"
		series={cpuSeries(live.history, live.host, live.now)}
		from={plotWindow.from}
		to={plotWindow.to}
		unit="%"
		yDomain={[0, 100]}
		loading={live.historyLoading}
	/>
	<LineChart
		title="Load average"
		series={loadSeries(live.history, live.host, live.now)}
		from={plotWindow.from}
		to={plotWindow.to}
		unit=""
		loading={live.historyLoading}
	/>
</div>

<div class="grid grid-cols-[repeat(auto-fit,minmax(min(100%,440px),1fr))] gap-3">
	<!-- PSI: how much of the time something was held up waiting. Usage says how busy the host is;
	     this says whether that is hurting. -->
	<LineChart
		title="Pressure (time stalled)"
		series={pressureSeries(live.history, live.host, live.now)}
		from={plotWindow.from}
		to={plotWindow.to}
		unit="%"
		yDomain={[0, 100]}
		loading={live.historyLoading}
	/>
	{#if network.length > 0}
		<LineChart title="Network" series={network} from={plotWindow.from} to={plotWindow.to} unit=" MB/s" loading={live.historyLoading} />
	{/if}
</div>
