<script lang="ts">
	// One measure against another: every paired reading as a dot, and the mean at each step of
	// x as a line through them. Bespoke for the same reason LineChart is (ADR-011). The maths
	// is in #lib/insights.ts.
	import { linearScale, niceTicks } from '#lib/chart/scale.ts';
	import type { Bin, Pair } from '#lib/insights.ts';

	interface Props {
		/** What the plot shows, for a screen reader: "Duty against temperature". */
		label: string;
		pairs: Pair[];
		/** The mean y at each x, in x order. */
		curve: Bin[];
		xUnit: string;
		yUnit: string;
		/** Fixes the y-axis (0-100 for duty). Omitted: fitted with round bounds. */
		yDomain?: [number, number];
		color?: string;
	}

	let { label, pairs, curve, xUnit, yUnit, yDomain, color = 'var(--series-1)' }: Props = $props();

	const HEIGHT = 220;
	const MARGIN = { top: 12, right: 16, bottom: 26, left: 40 };
	const TOOLTIP_WIDTH = 170;

	let width = $state(480);
	let hover = $state<Bin | null>(null);

	const bounds = (values: number[], target: number): [number, number] => {
		if (values.length === 0) return [0, 1];
		const ticks = niceTicks(Math.min(...values), Math.max(...values), target);
		return [ticks[0], ticks[ticks.length - 1]];
	};

	const xBounds = $derived(
		bounds(
			pairs.map((pair) => pair.x),
			5
		)
	);
	const yBounds = $derived(
		yDomain ??
			bounds(
				pairs.map((pair) => pair.y),
				4
			)
	);
	const x = $derived(linearScale(xBounds, [MARGIN.left, Math.max(MARGIN.left + 1, width - MARGIN.right)]));
	const y = $derived(linearScale(yBounds, [HEIGHT - MARGIN.bottom, MARGIN.top]));
	const xTicks = $derived(niceTicks(xBounds[0], xBounds[1], 5).filter((tick) => tick >= xBounds[0] && tick <= xBounds[1]));
	const yTicks = $derived(niceTicks(yBounds[0], yBounds[1], 4).filter((tick) => tick >= yBounds[0] && tick <= yBounds[1]));
	const path = $derived(curve.map((bin, i) => `${i === 0 ? 'M' : 'L'}${x(bin.x).toFixed(1)},${y(bin.y).toFixed(1)}`).join(''));

	const nearest = (value: number) =>
		curve.reduce<Bin | null>((best, bin) => (!best || Math.abs(bin.x - value) < Math.abs(best.x - value) ? bin : best), null);

	function pointerMove(event: PointerEvent) {
		const box = (event.currentTarget as SVGElement).getBoundingClientRect();
		hover = nearest(x.invert(event.clientX - box.left));
	}

	function keyDown(event: KeyboardEvent) {
		if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
		event.preventDefault();
		const index = hover ? curve.indexOf(hover) : -1;
		hover = curve[Math.min(curve.length - 1, Math.max(0, index + (event.key === 'ArrowLeft' ? -1 : 1)))] ?? null;
	}

	const round = (value: number) => Math.round(value * 10) / 10;
</script>

<div class="relative" bind:clientWidth={width}>
	<!-- A focusable image read with the arrow keys, as LineChart is; the list under the plot's panel says the same in words. -->
	<!-- svelte-ignore a11y_no_noninteractive_tabindex, a11y_no_noninteractive_element_interactions -->
	<svg
		{width}
		height={HEIGHT}
		class="block touch-pan-y rounded outline-none focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-series-1"
		role="img"
		aria-label="{label}. Use the left and right arrow keys to read the average at each step."
		tabindex="0"
		onpointermove={pointerMove}
		onpointerleave={() => (hover = null)}
		onkeydown={keyDown}
		onblur={() => (hover = null)}
	>
		{#each yTicks as tick (tick)}
			<line x1={MARGIN.left} x2={width - MARGIN.right} y1={y(tick)} y2={y(tick)} stroke="var(--grid)" />
			<text class="fill-faint text-[11px] tabular-nums" x={MARGIN.left - 8} y={y(tick)} text-anchor="end" dominant-baseline="middle">
				{tick}
			</text>
		{/each}
		{#each xTicks as tick (tick)}
			<text class="fill-faint text-[11px] tabular-nums" x={x(tick)} y={HEIGHT - 8} text-anchor="middle">{tick}{xUnit}</text>
		{/each}
		<line x1={MARGIN.left} x2={width - MARGIN.right} y1={HEIGHT - MARGIN.bottom} y2={HEIGHT - MARGIN.bottom} stroke="var(--axis)" />

		<!-- Translucent, so where readings pile up reads darker than where one strayed. -->
		{#each pairs as pair (pair.time)}
			<circle cx={x(pair.x)} cy={y(pair.y)} r="3" fill={color} fill-opacity="0.22" />
		{/each}
		<path d={path} fill="none" stroke={color} stroke-width="2" stroke-linejoin="round" stroke-linecap="round" />

		{#if hover}
			<line x1={x(hover.x)} x2={x(hover.x)} y1={MARGIN.top} y2={HEIGHT - MARGIN.bottom} stroke="var(--axis)" stroke-dasharray="3 3" />
			<!-- A ring in the surface colour keeps the marker legible over the dots under it. -->
			<circle cx={x(hover.x)} cy={y(hover.y)} r="5" fill={color} stroke="var(--surface)" stroke-width="2" />
		{/if}
	</svg>

	{#if hover}
		<div
			class="pointer-events-none absolute top-2 rounded-md border border-border bg-popover px-2.5 py-1.5 text-xs shadow-sm"
			style:left="{Math.min(Math.max(x(hover.x) + 12, MARGIN.left), width - TOOLTIP_WIDTH)}px"
			style:width="{TOOLTIP_WIDTH}px"
		>
			<div class="text-muted-foreground">At {round(hover.x)}{xUnit}</div>
			<div class="font-semibold tabular-nums">{round(hover.y)}{yUnit} on average</div>
			<div class="text-faint tabular-nums">{hover.count} reading{hover.count === 1 ? '' : 's'}</div>
		</div>
	{/if}
</div>
