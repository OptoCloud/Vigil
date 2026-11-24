<script lang="ts">
	// Relationships and trends the other pages' charts do not show: how each fan answers heat,
	// when in the day the host is busy, when a pool will be full, and when a source went quiet.
	// All of it is read out of the history vigil-core already serves; nothing here is live, so
	// this page asks for its own window and leaves the other pages' range alone.
	import GapTimeline from '#lib/components/GapTimeline.svelte';
	import Heatmap from '#lib/components/Heatmap.svelte';
	import Panel from '#lib/components/Panel.svelte';
	import ScatterChart from '#lib/components/ScatterChart.svelte';
	import Segmented from '#lib/components/Segmented.svelte';
	import { humanize } from '#lib/chart/scale.ts';
	import { coverage, fanResponse, FILL_LEVELS, forecast, FORECAST_MIN_DAYS, gaps, recordedSince, rhythm } from '#lib/insights.ts';
	import { historyUrl } from '#lib/live.svelte.ts';
	import type { HistoryResponse, SeriesPoints } from '#lib/types.ts';

	type Window = '7d' | '30d';
	const WINDOWS: { key: Window; label: string; slotHours: number }[] = [
		{ key: '7d', label: '7 days', slotHours: 1 },
		{ key: '30d', label: '30 days', slotHours: 2 }
	];

	/** The temperatures a fan curve can follow, as history names them, and what to call each. */
	const HEAT: Record<string, string> = {
		cpu: 'the CPU',
		gpu: 'the GPU',
		hba: 'the HBA',
		board: 'the board',
		'drives:max': 'the hottest drive',
		'memory:max': 'the hottest DIMM'
	};

	type MetricKey = 'cpu' | 'drives' | 'busy' | 'stall';
	const METRICS: { key: MetricKey; label: string; unit: string; of: (history: HistoryResponse) => SeriesPoints | undefined }[] = [
		{ key: 'cpu', label: 'CPU temperature', unit: ' °C', of: (history) => history.temperatures.cpu },
		{ key: 'drives', label: 'Hottest drive', unit: ' °C', of: (history) => history.temperatures['drives:max'] },
		{ key: 'busy', label: 'CPU busy', unit: ' %', of: (history) => history.host.cpuBusy },
		{ key: 'stall', label: 'I/O stall', unit: ' %', of: (history) => history.host.pressureIoFull }
	];

	let selected = $state<Window>('30d');
	let metric = $state<MetricKey>('cpu');
	let history = $state<HistoryResponse | null>(null);
	let problem = $state('');
	let loading = $state(true);

	const slotHours = $derived(WINDOWS.find((w) => w.key === selected)?.slotHours ?? 2);

	$effect(() => {
		const requested = selected;
		loading = true;
		fetch(historyUrl(requested, null))
			.then(async (response) => {
				if (!response.ok) throw new Error((await response.text().catch(() => '')).trim() || `HTTP ${response.status}`);
				return (await response.json()) as HistoryResponse;
			})
			.then((result) => {
				// A slow answer for a window since left must not replace the current one.
				if (requested !== selected) return;
				history = result;
				problem = '';
			})
			.catch((error: unknown) => {
				if (requested === selected) problem = error instanceof Error ? error.message : String(error);
			})
			.finally(() => {
				if (requested === selected) loading = false;
			});
	});

	// Every temperature there is, less each bay's copy of its drive's: a curve can follow one disk.
	const candidates = $derived(Object.keys(history?.temperatures ?? {}).filter((id) => !id.startsWith('port:')));
	/** "the CPU", or for one disk or DIMM the end of its id, which is what tells it from its neighbours. */
	const heat = (id: string) => HEAT[id] ?? (id.startsWith('drive:') ? `drive …${id.slice(-8)}` : id);

	const fans = $derived(
		Object.entries(history?.duties ?? {})
			.sort(([a], [b]) => a.localeCompare(b))
			.map(([id, duty]) => ({ id, response: fanResponse(duty, history?.temperatures ?? {}, candidates) }))
	);

	const chosen = $derived(METRICS.find((m) => m.key === metric) ?? METRICS[0]);
	const grid = $derived(history ? rhythm(chosen.of(history) ?? [], slotHours) : null);

	const pools = $derived(
		Object.entries(history?.poolFill ?? {})
			.sort(([a], [b]) => a.localeCompare(b))
			.map(([name, points]) => ({ name, percent: points.at(-1)?.[1], forecast: forecast(points) }))
	);

	const sources = $derived.by(() => {
		if (!history) return [];
		const { from, to, bucketSeconds } = history;
		const first = (series: Record<string, SeriesPoints>) => Object.values(series)[0] ?? [];
		const candidates: [string, SeriesPoints | undefined][] = [
			['Sensors', history.temperatures.cpu],
			['Fans', first(history.duties)],
			['UPS', history.ups.charge],
			['Host load', history.host.cpuBusy],
			['Pools', first(history.poolFill)]
		];
		return candidates
			.filter((candidate): candidate is [string, SeriesPoints] => candidate[1] !== undefined)
			.map(([label, points]) => {
				// Judged from its first reading: before that it was not silent, it was not recorded.
				const since = recordedSince(points, from, to, bucketSeconds * 1000);
				const found = gaps(points, since ?? from, to, bucketSeconds * 1000);
				return { label, since, gaps: found, coverage: coverage(found, since ?? from, to) };
			});
	});

	const round = (value: number, digits = 1) => Math.round(value * 10 ** digits) / 10 ** digits;
	const day = (time: number) => new Date(time).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
	/** How well the duty tracks the temperature, in a word: a number alone means little to most readers. */
	const strength = (r: number) => (r >= 0.9 ? 'closely' : r >= 0.6 ? 'loosely' : 'barely');
</script>

<svelte:head><title>Insights · vigil</title></svelte:head>

<div class="flex flex-wrap items-center gap-3">
	<Segmented label="Window" options={WINDOWS} value={selected} onselect={(key: Window) => (selected = key)} />
	{#if problem}<span class="text-faint" role="status">History unavailable: {problem}</span>{/if}
</div>

<div class="flex flex-col gap-3 transition-opacity {loading ? 'opacity-55' : ''}">
	<Panel
		title="Fan response"
		subtitle="Each fan's duty against the temperature it tracks most closely. The line is the average duty at each degree."
	>
		<div class="grid grid-cols-[repeat(auto-fit,minmax(min(100%,360px),1fr))] gap-x-6 gap-y-4">
			{#each fans as fan (fan.id)}
				<section class="min-w-0">
					<h3 class="text-sm font-semibold">{humanize(fan.id)}</h3>
					{#if fan.response}
						<p class="mb-1 text-xs text-muted-foreground">
							Follows {heat(fan.response.follows)}
							{strength(fan.response.r)} (r = {round(fan.response.r, 2)}).
						</p>
						<ScatterChart
							label="{humanize(fan.id)}: duty against {heat(fan.response.follows)}"
							pairs={fan.response.pairs}
							curve={fan.response.curve}
							xUnit="°"
							yUnit=" %"
							yDomain={[0, 100]}
						/>
					{:else}
						<p class="text-xs text-faint">Its duty did not change in this window, so there is no response to draw.</p>
					{/if}
				</section>
			{:else}
				<p class="text-faint">No fan history in this window.</p>
			{/each}
		</div>
	</Panel>

	<Panel
		title="Daily rhythm"
		subtitle="Days across, time of day down, in your time zone. A dark row is something that happens at that hour every day."
	>
		{#snippet actions()}
			<Segmented label="Metric" options={METRICS} value={metric} onselect={(key: MetricKey) => (metric = key)} />
		{/snippet}
		{#if grid}
			<Heatmap label={chosen.label} {grid} unit={chosen.unit} {slotHours} />
		{:else}
			<p class="text-faint">No {chosen.label.toLowerCase()} recorded in this window.</p>
		{/if}
	</Panel>

	<div class="grid grid-cols-[repeat(auto-fit,minmax(min(100%,440px),1fr))] gap-3">
		<Panel title="Capacity forecast" subtitle="Each pool's fill and where its trend over this window leads.">
			<table class="w-full text-sm tabular-nums">
				<thead>
					<tr class="text-left text-xs text-muted-foreground">
						<th scope="col" class="py-1 font-semibold">Pool</th>
						<th scope="col" class="py-1 text-right font-semibold">Now</th>
						<th scope="col" class="py-1 text-right font-semibold">Per day</th>
						{#each FILL_LEVELS as level (level)}<th scope="col" class="py-1 text-right font-semibold">{level} % full</th>{/each}
					</tr>
				</thead>
				<tbody>
					{#each pools as pool (pool.name)}
						<tr class="border-t border-grid">
							<th scope="row" class="py-1.5 text-left font-normal">{pool.name}</th>
							<td class="py-1.5 text-right">{pool.percent === undefined ? 'n/a' : `${round(pool.percent)} %`}</td>
							{#if pool.forecast}
								<td class="py-1.5 text-right">{pool.forecast.perDay > 0 ? '+' : ''}{round(pool.forecast.perDay, 2)} pt</td>
								{#each pool.forecast.reaches as reached, i (FILL_LEVELS[i])}
									<td class="py-1.5 text-right">
										{reached ? day(reached) : pool.forecast.percent >= FILL_LEVELS[i] ? 'past it' : 'not at this rate'}
									</td>
								{/each}
							{:else}
								<td class="py-1.5 text-right text-faint" colspan={1 + FILL_LEVELS.length}>under {FORECAST_MIN_DAYS} days of history</td>
							{/if}
						</tr>
					{:else}
						<tr><td class="py-1.5 text-faint" colspan={3 + FILL_LEVELS.length}>No pool history in this window.</td></tr>
					{/each}
				</tbody>
			</table>
			<p class="mt-2 text-xs text-faint">
				A straight line through this window's readings. A one-off copy bends it, so read the date as "at this rate", not as a promise.
			</p>
		</Panel>

		<Panel title="Data gaps" subtitle="When each source was reporting, and when it was not.">
			{#if history && sources.length > 0}
				<GapTimeline rows={sources} from={history.from} to={history.to} />
			{:else}
				<p class="text-faint">No history in this window.</p>
			{/if}
		</Panel>
	</div>
</div>
