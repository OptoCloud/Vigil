<script lang="ts">
	// Temperatures and what the fans are doing about them.
	import FanList from '#lib/components/FanList.svelte';
	import LineChart from '#lib/components/LineChart.svelte';
	import Panel from '#lib/components/Panel.svelte';
	import RangeBar from '#lib/components/RangeBar.svelte';
	import { getLive } from '#lib/live.svelte.ts';
	import { chartWindow, fanIds, fanSeries, MAX_SERIES, temperatureSeries } from '#lib/series.ts';

	const live = getLive();

	const plotWindow = $derived(chartWindow(live.history, live.snapshotAt, live.now));
	const fans = $derived(fanIds(live.history, live.snapshot?.fans ?? []));
</script>

<svelte:head><title>Cooling · vigil</title></svelte:head>

<RangeBar />

<LineChart
	title="Temperatures"
	series={temperatureSeries(live.history, live.snapshot, live.now)}
	from={plotWindow.from}
	to={plotWindow.to}
	unit="°C"
	loading={live.historyLoading}
/>

<div class="grid grid-cols-[repeat(auto-fit,minmax(min(100%,440px),1fr))] gap-3">
	<!-- Duty shares one fixed 0-100 axis, and speed gets a chart of its own rather than a second y-scale:
	     a 2,700 rpm HBA fan next to 600 rpm case fans would say nothing, and two scales invent correlations. -->
	<LineChart
		title="Fan duty"
		series={fanSeries(live.history, live.snapshot, live.now, 'duty')}
		from={plotWindow.from}
		to={plotWindow.to}
		unit="%"
		yDomain={[0, 100]}
		loading={live.historyLoading}
	/>
	<LineChart
		title="Fan speed"
		series={fanSeries(live.history, live.snapshot, live.now, 'rpm')}
		from={plotWindow.from}
		to={plotWindow.to}
		unit=" rpm"
		loading={live.historyLoading}
	/>
</div>
{#if fans.length > MAX_SERIES}
	<p class="-mt-1.5 text-xs text-faint">
		{fans.length - MAX_SERIES} more fan(s) are not charted: six is as many lines as colour can keep apart. All of them are listed below.
	</p>
{/if}

<Panel title="Fans">
	<FanList fans={live.snapshot?.fans ?? []} />
</Panel>
