<script lang="ts">
	// The UPS, read from NUT's upsd by vigil-core, with its history.
	import LineChart from '#lib/components/LineChart.svelte';
	import RangeBar from '#lib/components/RangeBar.svelte';
	import UpsPanel from '#lib/components/UpsPanel.svelte';
	import { getLive } from '#lib/live.svelte.ts';
	import { chartWindow, upsSeries } from '#lib/series.ts';

	const live = getLive();

	const plotWindow = $derived(chartWindow(live.history, live.snapshotAt, live.chartNow));
	const series = $derived(upsSeries(live.history, live.ups.reading, live.chartNow));
</script>

<svelte:head><title>Power · vigil</title></svelte:head>

{#if !live.ups.enabled}
	<p class="text-faint">No UPS is configured: vigil-core reads one from NUT when NUT_HOST is set.</p>
{:else}
	<RangeBar />
	<div class="grid grid-cols-[repeat(auto-fit,minmax(min(100%,340px),1fr))] items-start gap-3">
		<UpsPanel ups={live.ups} inputVoltage={live.history?.ups.inputVoltage ?? []} now={live.chartNow} />
		<div class="flex flex-col gap-3">
			<LineChart
				title="Battery and load"
				series={series.percent}
				from={plotWindow.from}
				to={plotWindow.to}
				unit="%"
				yDomain={[0, 100]}
				loading={live.historyLoading}
			/>
			<LineChart
				title="Mains voltage"
				series={series.mains}
				from={plotWindow.from}
				to={plotWindow.to}
				unit=" V"
				loading={live.historyLoading}
			/>
		</div>
	</div>
	<!-- One unit each, so three small charts rather than one with three scales. -->
	<div class="grid grid-cols-[repeat(auto-fit,minmax(min(100%,340px),1fr))] gap-3">
		<LineChart
			title="Battery temperature"
			series={series.battery}
			from={plotWindow.from}
			to={plotWindow.to}
			unit="°C"
			loading={live.historyLoading}
		/>
		<LineChart
			title="Output frequency"
			series={series.frequency}
			from={plotWindow.from}
			to={plotWindow.to}
			unit=" Hz"
			loading={live.historyLoading}
		/>
		<LineChart
			title="Output current"
			series={series.current}
			from={plotWindow.from}
			to={plotWindow.to}
			unit=" A"
			loading={live.historyLoading}
		/>
	</div>
{/if}
