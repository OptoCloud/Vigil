<script lang="ts">
	// The dashboard: everything at a glance, each panel linking to the page with the rest of it.
	import { resolve } from '$app/paths';
	import DriveSummary from '#lib/components/DriveSummary.svelte';
	import EventLog from '#lib/components/EventLog.svelte';
	import FanList from '#lib/components/FanList.svelte';
	import LineChart from '#lib/components/LineChart.svelte';
	import Panel from '#lib/components/Panel.svelte';
	import PoolSummary from '#lib/components/PoolSummary.svelte';
	import RangeBar from '#lib/components/RangeBar.svelte';
	import StatTile from '#lib/components/StatTile.svelte';
	import UpsSummary from '#lib/components/UpsSummary.svelte';
	import { shortWwn } from '#lib/drives.ts';
	import { getLive } from '#lib/live.svelte.ts';
	import { chartWindow, hottest, liveTemperatures, temperatureSeries } from '#lib/series.ts';

	const live = getLive();

	/** As many events as fit beside the fan list without making the row taller. */
	const RECENT_EVENTS = 8;

	const sensors = $derived(live.snapshot?.sensors ?? []);
	const hottestDrive = $derived(hottest(sensors, 'drive'));
	const series = $derived(temperatureSeries(live.history, live.snapshot, live.now));
	const plotWindow = $derived(chartWindow(live.history, live.snapshotAt, live.now));

	const temperatures = $derived(liveTemperatures(sensors));
</script>

<svelte:head><title>vigil</title></svelte:head>

<RangeBar />

<section class="grid grid-cols-[repeat(auto-fit,minmax(190px,1fr))] gap-3" aria-label="Temperatures">
	{#each series as s (s.id)}
		{@const celsius = temperatures[s.id] ?? null}
		{#if s.id === 'drives:max'}
			<StatTile label={s.label} {celsius} points={s.points}>
				{#snippet detail()}
					{#if hottestDrive}
						{@const wwn = hottestDrive.id.replace(/^drive:/, '')}
						<a class="underline-offset-4 hover:underline" href={resolve('/drives/[wwn]', { wwn: wwn })}>drive {shortWwn(wwn)}</a>
					{:else}&nbsp;{/if}
				{/snippet}
			</StatTile>
		{:else}
			<StatTile label={s.label} {celsius} points={s.points} />
		{/if}
	{/each}
</section>

<div class="grid grid-cols-[repeat(auto-fit,minmax(min(100%,340px),1fr))] items-start gap-3">
	<Panel title="Storage" more={{ href: resolve('/storage'), label: 'Pools' }}>
		<PoolSummary zfs={live.snapshot?.zfs} />
	</Panel>
	<Panel title="Drives" more={{ href: resolve('/drives'), label: 'All drives' }}>
		<DriveSummary {sensors} drives={live.drives} />
	</Panel>
	{#if live.ups.enabled}
		<Panel title="Power" more={{ href: resolve('/power'), label: 'UPS' }}>
			<UpsSummary ups={live.ups} />
		</Panel>
	{/if}
</div>

<LineChart title="Temperatures" {series} from={plotWindow.from} to={plotWindow.to} unit="°C" loading={live.historyLoading} />

<div class="grid grid-cols-[repeat(auto-fit,minmax(min(100%,440px),1fr))] items-start gap-3">
	<Panel title="Fans" more={{ href: resolve('/cooling'), label: 'Cooling' }}>
		<FanList fans={live.snapshot?.fans ?? []} />
	</Panel>
	<Panel title="Recent events" more={{ href: resolve('/events'), label: 'All events' }}>
		<EventLog events={live.events} now={live.now} limit={RECENT_EVENTS} />
	</Panel>
</div>
