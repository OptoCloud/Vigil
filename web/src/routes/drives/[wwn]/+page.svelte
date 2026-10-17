<script lang="ts">
	// One disk: its SMART state and history, its temperature, and where it sits in ZFS. Everything
	// is joined on the name vigild gives the disk, so this page follows the disk between bays.
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { relativeTime } from '#lib/chart/scale.ts';
	import DriveHistoryTable from '#lib/components/DriveHistoryTable.svelte';
	import LineChart from '#lib/components/LineChart.svelte';
	import Panel from '#lib/components/Panel.svelte';
	import RangeBar from '#lib/components/RangeBar.svelte';
	import StatusBadge from '#lib/components/StatusBadge.svelte';
	import { deviceOf, formatCount, formatYears, membershipsOf, shortPort, shortWwn, smartVerdict } from '#lib/drives.ts';
	import { getLive } from '#lib/live.svelte.ts';
	import { chartWindow, driveTemperatureSeries } from '#lib/series.ts';
	import type { DriveHealthRecord } from '#lib/types.ts';
	import { vdevVerdict } from '#lib/zfs.ts';

	const live = getLive();

	const wwn = $derived(page.params.wwn ?? '');
	const sensor = $derived(live.snapshot?.sensors.find((s) => s.id === `drive:${wwn}`));
	const health = $derived(live.drives.find((drive) => drive.wwn === wwn));
	const smart = $derived(smartVerdict(health));
	const port = $derived(sensor?.port ?? health?.port ?? null);
	const memberships = $derived(membershipsOf(live.snapshot?.zfs, wwn));
	const known = $derived(sensor !== undefined || health !== undefined);
	const plotWindow = $derived(chartWindow(live.history, live.snapshotAt, live.now));

	let records = $state<DriveHealthRecord[] | null>(null);
	let recordsError = $state('');

	$effect(() => {
		const requested = wwn;
		records = null;
		recordsError = '';
		void (async () => {
			try {
				const response = await fetch(`/api/drives/${encodeURIComponent(requested)}/history`);
				if (!response.ok) throw new Error((await response.text().catch(() => '')).trim() || `HTTP ${response.status}`);
				const result = (await response.json()) as DriveHealthRecord[];
				// Moving to another disk's page while this was in flight must not show this one's.
				if (requested === wwn) records = result;
			} catch (error) {
				if (requested === wwn) recordsError = error instanceof Error ? error.message : String(error);
			}
		})();
	});
</script>

<svelte:head><title>Drive {shortWwn(wwn)} · vigil</title></svelte:head>

<div class="flex flex-wrap items-end justify-between gap-3">
	<div class="min-w-0">
		<a class="text-xs text-muted-foreground underline-offset-4 hover:underline" href={resolve('/drives')}>All drives</a>
		<h1 class="text-lg font-semibold break-all">{wwn}</h1>
		<p class="text-faint">
			{#if known}
				{deviceOf(health?.sourcePath) || 'device unknown'}{#if port}, in bay <span title={port}>{shortPort(port)}</span>{/if}
			{:else if live.snapshot}
				vigild is not reporting this drive now, and vigil-core has no SMART result for it.
			{:else}
				Waiting for data.
			{/if}
		</p>
	</div>
	<RangeBar />
</div>

<div class="grid grid-cols-[repeat(auto-fit,minmax(min(100%,340px),1fr))] items-start gap-3">
	<Panel title="Health">
		{#snippet actions()}
			<StatusBadge class="font-semibold" level={smart.level} label={smart.label} />
		{/snippet}
		<dl
			class="grid grid-cols-[auto_1fr] gap-x-6 gap-y-1.5 [&_dd]:text-right [&_dd]:font-semibold [&_dd]:tabular-nums [&_dt]:text-muted-foreground"
		>
			<dt>Temperature</dt>
			<dd>{sensor?.celsiusOrNull == null ? 'asleep or unreadable' : `${sensor.celsiusOrNull.toFixed(0)}°C`}</dd>
			<dt>Reallocated sectors</dt>
			<dd>{formatCount(health?.reallocatedSectorCount)}</dd>
			<dt>Pending sectors</dt>
			<dd>{formatCount(health?.pendingSectorCount)}</dd>
			<dt>Powered on</dt>
			<dd>{formatYears(health?.powerOnHours)}</dd>
			<dt>Last answered</dt>
			<dd>{health ? relativeTime(health.asOf, live.now) : 'n/a'}</dd>
		</dl>
	</Panel>

	<Panel title="ZFS" more={{ href: resolve('/storage'), label: 'Pools' }}>
		{#if !live.snapshot?.zfs?.isAvailable}
			<p class="text-faint">Pool state is not being reported, so whether this disk is in a pool is unknown.</p>
		{:else if memberships.length === 0}
			<p class="text-faint">Not part of any imported pool.</p>
		{:else}
			<ul class="flex flex-col">
				{#each memberships as { pool, vdev } (vdev.guid ?? vdev.name)}
					{@const v = vdevVerdict(vdev)}
					<li class="flex flex-col gap-1 border-b border-grid py-1.5 last:border-b-0">
						<div class="flex items-center justify-between gap-3">
							<span><span class="font-semibold">{pool.name}</span> <span class="text-faint">/ {vdev.parent ?? 'top level'}</span></span>
							<StatusBadge level={v.level} label={v.label} />
						</div>
						<span class="text-xs text-faint tabular-nums">
							{vdev.name}: {formatCount(vdev.readErrors)} read, {formatCount(vdev.writeErrors)} write, {formatCount(vdev.checksumErrors)} checksum
							errors
						</span>
					</li>
				{/each}
			</ul>
		{/if}
	</Panel>
</div>

<LineChart
	title="Temperature"
	series={driveTemperatureSeries(live.history, live.snapshot, wwn, live.now)}
	from={plotWindow.from}
	to={plotWindow.to}
	unit="°C"
	loading={live.historyLoading}
/>

<Panel title="SMART changes" subtitle="Recorded whenever a count moves; power-on hours alone do not count">
	{#if recordsError}
		<p class="text-faint" role="status">{recordsError}</p>
	{:else if records === null}
		<p class="text-faint">Loading.</p>
	{:else}
		<DriveHistoryTable {records} />
	{/if}
</Panel>
