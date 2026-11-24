<script lang="ts">
	// A metric as days across and the time of day down, each cell shaded by its value: one hue,
	// light to dark, since this is magnitude. A nightly job is a dark row; an odd day, a dark
	// column. The grid is worked out in #lib/insights.ts.
	import type { Rhythm } from '#lib/insights.ts';

	interface Props {
		/** What is shown, for a screen reader and the tooltip: "CPU temperature". */
		label: string;
		grid: Rhythm;
		unit: string;
		slotHours: number;
	}

	let { label, grid, unit, slotHours }: Props = $props();

	const MARGIN = { top: 4, right: 4, bottom: 22, left: 40 };
	const CELL_HEIGHT = 16;
	/** The surface shows through between cells, so neighbours of one shade stay countable. */
	const GAP = 2;
	const TOOLTIP_WIDTH = 190;

	let width = $state(640);
	let hover = $state<{ day: number; slot: number } | null>(null);

	const height = $derived(MARGIN.top + grid.slots * CELL_HEIGHT + MARGIN.bottom);
	const cellWidth = $derived(Math.max(4, (width - MARGIN.left - MARGIN.right) / Math.max(1, grid.days.length)));
	/** A date under every nth column, so the labels never collide. */
	const labelEvery = $derived(Math.max(1, Math.ceil(64 / cellWidth)));

	/** 10 % to 100 % of the hue over the surface: the lowest cell is still visibly a cell. */
	const shade = (value: number) => {
		const share = grid.max === grid.min ? 1 : (value - grid.min) / (grid.max - grid.min);
		return `color-mix(in oklab, var(--series-1) ${Math.round(10 + 90 * share)}%, var(--surface))`;
	};

	const round = (value: number) => Math.round(value * 10) / 10;
	const hours = (slot: number) => `${String(slot * slotHours).padStart(2, '0')}:00`;
	const date = (day: number) => new Date(day).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
	const weekday = (day: number) => new Date(day).toLocaleDateString(undefined, { weekday: 'short' });

	const slotIndexes = $derived([...Array(grid.slots).keys()]);

	/** The cell under the pointer, from where it is: the cells themselves stay plain shapes. */
	function pointerMove(event: PointerEvent) {
		const box = (event.currentTarget as SVGElement).getBoundingClientRect();
		const day = Math.floor((event.clientX - box.left - MARGIN.left) / cellWidth);
		const slot = Math.floor((event.clientY - box.top - MARGIN.top) / CELL_HEIGHT);
		hover = day >= 0 && day < grid.days.length && slot >= 0 && slot < grid.slots ? { day, slot } : null;
	}

	const shown = $derived(hover ? { ...hover, value: grid.cells[hover.day][hover.slot] } : null);

	function keyDown(event: KeyboardEvent) {
		const step: Record<string, [number, number]> = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };
		const move = step[event.key];
		if (!move) return;
		event.preventDefault();
		const from = hover ?? { day: grid.days.length - 1, slot: 0 };
		hover = {
			day: Math.min(grid.days.length - 1, Math.max(0, from.day + move[0])),
			slot: Math.min(grid.slots - 1, Math.max(0, from.slot + move[1]))
		};
	}
</script>

<div class="relative" bind:clientWidth={width}>
	<!-- svelte-ignore a11y_no_noninteractive_tabindex, a11y_no_noninteractive_element_interactions -->
	<svg
		{width}
		{height}
		class="block rounded outline-none focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-series-1"
		role="img"
		aria-label="{label} by day and time of day. Use the arrow keys to read a cell."
		tabindex="0"
		onpointermove={pointerMove}
		onpointerleave={() => (hover = null)}
		onkeydown={keyDown}
		onblur={() => (hover = null)}
	>
		{#each slotIndexes as slot (slot)}
			{#if (slot * slotHours) % 6 === 0}
				<text
					class="fill-faint text-[11px] tabular-nums"
					x={MARGIN.left - 8}
					y={MARGIN.top + slot * CELL_HEIGHT + CELL_HEIGHT / 2}
					text-anchor="end"
					dominant-baseline="middle"
				>
					{hours(slot)}
				</text>
			{/if}
		{/each}

		{#each grid.days as day, d (day)}
			{#each grid.cells[d] as value, slot (slot)}
				<!-- A missing reading is an outline, never the lightest shade: unknown is not low. -->
				<rect
					x={MARGIN.left + d * cellWidth + GAP / 2}
					y={MARGIN.top + slot * CELL_HEIGHT + GAP / 2}
					width={Math.max(1, cellWidth - GAP)}
					height={CELL_HEIGHT - GAP}
					rx="2"
					fill={value === null ? 'none' : shade(value)}
					stroke={hover?.day === d && hover.slot === slot ? 'var(--text-primary)' : value === null ? 'var(--grid)' : 'none'}
					stroke-width={hover?.day === d && hover.slot === slot ? 2 : 1}
				/>
			{/each}
			{#if d % labelEvery === 0}
				<text
					class="fill-faint text-[11px] tabular-nums"
					x={MARGIN.left + d * cellWidth + cellWidth / 2}
					y={height - 6}
					text-anchor="middle"
				>
					{date(day)}
				</text>
			{/if}
		{/each}
	</svg>

	{#if shown}
		<div
			class="pointer-events-none absolute top-1 rounded-md border border-border bg-popover px-2.5 py-1.5 text-xs shadow-sm"
			style:left="{Math.min(Math.max(MARGIN.left + shown.day * cellWidth + cellWidth + 8, MARGIN.left), width - TOOLTIP_WIDTH)}px"
			style:width="{TOOLTIP_WIDTH}px"
		>
			<div class="text-muted-foreground">{weekday(grid.days[shown.day])} {date(grid.days[shown.day])}, from {hours(shown.slot)}</div>
			<div class="font-semibold tabular-nums">{shown.value === null ? 'No reading' : `${round(shown.value)}${unit}`}</div>
		</div>
	{/if}

	<!-- The scale, in words and in the same shades: the lowest and highest cell in this window. -->
	<div class="mt-1 flex items-center gap-2 text-xs text-faint tabular-nums">
		<span>{round(grid.min)}{unit}</span>
		<span class="h-2 w-24 rounded-sm" style:background="linear-gradient(to right, {shade(grid.min)}, {shade(grid.max)})"></span>
		<span>{round(grid.max)}{unit}</span>
		<span class="ml-2 inline-block h-2.5 w-2.5 rounded-sm border border-grid"></span>
		<span>no reading</span>
	</div>
</div>
