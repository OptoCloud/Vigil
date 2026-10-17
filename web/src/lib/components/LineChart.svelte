<script lang="ts">
	// Bespoke by decision (ADR-011): gap detection, the crosshair readout and the table view are
	// behaviour shadcn's chart primitives would lose. The maths is in #lib/chart/plot.ts.
	import { linePath, readoutAt, tableRows } from '#lib/chart/plot.ts';
	import { formatInstant, formatTick, linearScale, niceTicks, timeTicks } from '#lib/chart/scale.ts';
	import { Button } from '#lib/components/ui/button/index.ts';
	import * as Card from '#lib/components/ui/card/index.ts';
	import type { ChartSeries } from '#lib/types.ts';

	interface Props {
		title: string;
		series: ChartSeries[];
		from: number;
		to: number;
		unit: string;
		/** Fixes the y-axis (e.g. 0-100 for duty). Omitted: fitted to the data with round bounds. */
		yDomain?: [number, number];
		loading?: boolean;
	}

	let { title, series, from, to, unit, yDomain, loading = false }: Props = $props();

	const uid = $props.id();
	const clipId = `plot-${uid}`;

	const HEIGHT = 240;
	const MARGIN = { top: 12, right: 16, bottom: 26, left: 40 };
	/** Roughly how wide a time label is, so ticks never collide. */
	const TICK_SPACING = 90;
	/** The tooltip's widest, so it is kept inside the plot on the right. */
	const TOOLTIP_WIDTH = 190;

	let width = $state(640);
	// A plain Set is correct here: toggle() assigns a fresh one rather than mutating this one,
	// so the $state assignment is what drives reactivity.
	let hidden = $state(new Set<string>());
	let hoverTime = $state<number | null>(null);
	let showTable = $state(false);

	const visible = $derived(series.filter((s) => !hidden.has(s.id) && s.points.length > 0));
	const span = $derived(to - from);

	const yBounds = $derived.by((): [number, number] => {
		if (yDomain) return yDomain;
		const values = visible.flatMap((s) => s.points.map((point) => point[1]));
		if (values.length === 0) return [0, 1];
		const ticks = niceTicks(Math.min(...values), Math.max(...values), 4);
		return [ticks[0], ticks[ticks.length - 1]];
	});

	const x = $derived(linearScale([from, to], [MARGIN.left, Math.max(MARGIN.left + 1, width - MARGIN.right)]));
	const y = $derived(linearScale(yBounds, [HEIGHT - MARGIN.bottom, MARGIN.top]));
	const yTicks = $derived(niceTicks(yBounds[0], yBounds[1], 4).filter((tick) => tick >= yBounds[0] && tick <= yBounds[1]));
	const xTicks = $derived(timeTicks(from, to, Math.max(2, Math.floor((width - MARGIN.left - MARGIN.right) / TICK_SPACING))));

	const readout = $derived(readoutAt(visible, hoverTime, span));
	const rows = $derived(showTable ? tableRows(visible) : []);
	const latest = $derived(new Map(series.map((s) => [s.id, s.points.at(-1)?.[1]])));
	const tooltipLeft = $derived(readout ? Math.min(Math.max(x(readout.time) + 12, MARGIN.left), width - TOOLTIP_WIDTH) : 0);

	function pointerMove(event: PointerEvent) {
		const bounds = (event.currentTarget as SVGElement).getBoundingClientRect();
		hoverTime = Math.min(to, Math.max(from, x.invert(event.clientX - bounds.left)));
	}

	function keyDown(event: KeyboardEvent) {
		if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
		event.preventDefault();
		const step = (span / 60) * (event.key === 'ArrowLeft' ? -1 : 1);
		hoverTime = Math.min(to, Math.max(from, (hoverTime ?? to) + step));
	}

	function toggle(id: string) {
		// SvelteSet is not needed: this builds a NEW Set and assigns it to the $state above,
		// which is what makes the change reactive. Nothing is ever mutated in place.
		// eslint-disable-next-line svelte/prefer-svelte-reactivity
		const next = new Set(hidden);
		if (!next.delete(id)) next.add(id);
		hidden = next;
	}

	const format = (value: number | undefined) => (value === undefined ? 'n/a' : `${Math.round(value * 10) / 10}${unit}`);
</script>

<Card.Root class="min-w-0 gap-2">
	<Card.Header class="flex items-center justify-between gap-3">
		<Card.Title class="text-sm font-semibold text-muted-foreground"><h2>{title}</h2></Card.Title>
		<Button variant="outline" size="xs" aria-pressed={showTable} onclick={() => (showTable = !showTable)}>
			{showTable ? 'Show chart' : 'Show table'}
		</Button>
	</Card.Header>

	<Card.Content class="min-w-0">
		<!-- Always present for several series: identity never rests on colour-matching alone. Click to hide a series. -->
		<ul class="mb-1 -ml-1.5 flex flex-wrap gap-x-1.5 gap-y-0.5">
			{#each series as s (s.id)}
				<li>
					<button
						type="button"
						class="flex items-center gap-1.5 rounded-md px-1.5 py-0.5 text-xs hover:bg-accent {hidden.has(s.id) ? 'opacity-40' : ''}"
						aria-pressed={!hidden.has(s.id)}
						onclick={() => toggle(s.id)}
					>
						<!-- A short stroke, mirroring the mark: these are lines, so the key is a line. -->
						<span class="h-0.75 w-3.5 rounded-sm" style:background={s.color}></span>
						<!-- Text stays in ink; the coloured key beside it carries identity. -->
						<span class="text-muted-foreground">{s.label}</span>
						<span class="font-semibold tabular-nums">{format(latest.get(s.id))}</span>
					</button>
				</li>
			{/each}
		</ul>

		{#if showTable}
			<div class="mt-1.5 max-h-66.5 overflow-auto">
				<table class="w-full text-xs tabular-nums">
					<thead>
						<tr>
							<th scope="col" class="sticky top-0 bg-card px-2 py-1 text-left font-semibold text-muted-foreground">Time</th>
							{#each visible as s (s.id)}
								<th scope="col" class="sticky top-0 bg-card px-2 py-1 text-right font-semibold text-muted-foreground">{s.label}</th>
							{/each}
						</tr>
					</thead>
					<tbody>
						{#each rows as row (row.time)}
							<tr class="border-b border-grid">
								<th scope="row" class="px-2 py-1 text-left font-normal text-muted-foreground">{formatInstant(row.time, span)}</th>
								{#each row.values as value, i (visible[i].id)}<td class="px-2 py-1 text-right">{format(value)}</td>{/each}
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{:else}
			<!-- A refetch holds the previous render at reduced opacity: no skeleton, no layout jump. -->
			<div class="relative transition-opacity {loading ? 'opacity-55' : ''}" bind:clientWidth={width}>
				<!-- The plot is a focusable image whose values are read with the arrow keys; the table view is the full equivalent. -->
				<!-- svelte-ignore a11y_no_noninteractive_tabindex, a11y_no_noninteractive_element_interactions -->
				<svg
					{width}
					height={HEIGHT}
					class="block touch-pan-y rounded outline-none focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-series-1"
					role="img"
					aria-label="{title}. Use the left and right arrow keys to read values, or switch to the table view."
					tabindex="0"
					onpointermove={pointerMove}
					onpointerleave={() => (hoverTime = null)}
					onkeydown={keyDown}
					onblur={() => (hoverTime = null)}
				>
					{#each yTicks as tick (tick)}
						<line x1={MARGIN.left} x2={width - MARGIN.right} y1={y(tick)} y2={y(tick)} stroke="var(--grid)" />
						<text class="fill-faint text-[11px] tabular-nums" x={MARGIN.left - 8} y={y(tick)} text-anchor="end" dominant-baseline="middle"
							>{tick}</text
						>
					{/each}
					{#each xTicks as tick (tick)}
						<text class="fill-faint text-[11px] tabular-nums" x={x(tick)} y={HEIGHT - 8} text-anchor="middle">{formatTick(tick, span)}</text
						>
					{/each}
					<line x1={MARGIN.left} x2={width - MARGIN.right} y1={HEIGHT - MARGIN.bottom} y2={HEIGHT - MARGIN.bottom} stroke="var(--axis)" />

					<defs>
						<clipPath id={clipId}>
							<!-- 2px of slack top and bottom, so a line sitting on an axis bound (100% duty) is not half cut. -->
							<rect
								x={MARGIN.left}
								y={MARGIN.top - 2}
								width={Math.max(0, width - MARGIN.left - MARGIN.right)}
								height={HEIGHT - MARGIN.top - MARGIN.bottom + 4}
							/>
						</clipPath>
					</defs>

					<!-- History buckets can start before the window's left edge and a fitted axis can round inside
					     the data, so the lines are clipped to the plot area instead of spilling over the labels. -->
					<g clip-path="url(#{clipId})">
						{#each visible as s (s.id)}
							<path
								d={linePath(s.points, x, y)}
								fill="none"
								stroke={s.color}
								stroke-width="2"
								stroke-linejoin="round"
								stroke-linecap="round"
							/>
						{/each}
					</g>

					{#if readout}
						<line x1={x(readout.time)} x2={x(readout.time)} y1={MARGIN.top} y2={HEIGHT - MARGIN.bottom} stroke="var(--text-muted)" />
						{#each readout.rows as row (row.series.id)}
							<!-- The surface-coloured ring keeps a marker legible where it sits on top of other lines. -->
							<circle cx={x(readout.time)} cy={y(row.value)} r="4" fill={row.series.color} stroke="var(--surface)" stroke-width="2" />
						{/each}
					{/if}
				</svg>

				{#if readout}
					<div
						class="pointer-events-none absolute top-2 min-w-37.5 rounded-lg border bg-popover px-2.5 py-2 text-xs shadow-lg"
						style:left="{tooltipLeft}px"
						role="status"
					>
						<div class="mb-1 text-muted-foreground">{formatInstant(readout.time, span)}</div>
						{#each [...readout.rows].sort((a, b) => b.value - a.value) as row (row.series.id)}
							<div class="flex items-center gap-1.5 whitespace-nowrap">
								<span class="h-0.5 w-2.5 flex-none" style:background={row.series.color}></span>
								<strong class="tabular-nums">{format(row.value)}</strong>
								<span class="text-faint">{row.series.label}</span>
							</div>
						{/each}
					</div>
				{/if}

				{#if visible.length === 0 && !loading}
					<p class="absolute inset-0 grid place-items-center text-faint">No history for this range yet.</p>
				{/if}
			</div>
		{/if}
	</Card.Content>
</Card.Root>
