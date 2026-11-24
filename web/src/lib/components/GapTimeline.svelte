<script lang="ts">
	// When each source was reporting and when it was not, as one strip per source over the same
	// window. The gaps are also listed in words underneath: a state is never colour alone.
	import { formatInstant, formatTick, timeTicks } from '#lib/chart/scale.ts';
	import { duration, type Gap } from '#lib/insights.ts';

	interface Row {
		label: string;
		gaps: Gap[];
		/** When the source began being recorded, if that is inside the window; null when before it. */
		since: number | null;
		/** Percent of the time since then with readings. */
		coverage: number;
	}

	interface Props {
		rows: Row[];
		from: number;
		to: number;
	}

	let { rows, from, to }: Props = $props();

	const LABEL_WIDTH = 96;
	const ROW_HEIGHT = 22;
	const BAR_HEIGHT = 10;
	const AXIS_HEIGHT = 22;
	/** The longest gaps listed per source; a source with more says how many more. */
	const LISTED = 6;

	let width = $state(640);

	const span = $derived(to - from);
	const plot = $derived(Math.max(1, width - LABEL_WIDTH - 56));
	const x = $derived((time: number) => LABEL_WIDTH + ((time - from) / (span || 1)) * plot);
	const ticks = $derived(timeTicks(from, to, Math.max(2, Math.floor(plot / 90))));
	const height = $derived(rows.length * ROW_HEIGHT + AXIS_HEIGHT);
	const broken = $derived(rows.filter((row) => row.gaps.length > 0));
	const young = $derived(rows.filter((row) => row.since !== null));
</script>

<div bind:clientWidth={width}>
	<svg {width} {height} class="block" role="img" aria-label="When each source was reporting. The gaps are listed below.">
		{#each rows as row, i (row.label)}
			{@const middle = i * ROW_HEIGHT + ROW_HEIGHT / 2}
			<text class="fill-muted-foreground text-xs" x="0" y={middle} dominant-baseline="middle">{row.label}</text>
			<!-- Reporting: the quiet colour. Silent: the warning one, over it. -->
			{@const start = x(row.since ?? from)}
			<!-- Before a source was recorded at all there is an outline: nothing to report, which is not a silence. -->
			<rect x={LABEL_WIDTH} y={middle - BAR_HEIGHT / 2} width={plot} height={BAR_HEIGHT} rx="2" fill="none" stroke="var(--grid)" />
			<rect
				x={start}
				y={middle - BAR_HEIGHT / 2}
				width={Math.max(0, LABEL_WIDTH + plot - start)}
				height={BAR_HEIGHT}
				rx="2"
				fill="var(--meter-track)"
			/>
			{#each row.gaps as gap (gap.from)}
				<rect
					x={x(gap.from)}
					y={middle - BAR_HEIGHT / 2}
					width={Math.max(2, x(gap.to) - x(gap.from))}
					height={BAR_HEIGHT}
					rx="2"
					fill="var(--status-warning)"
				>
					<title>{row.label}: no readings for {duration(gap.to - gap.from)}, from {formatInstant(gap.from, span)}</title>
				</rect>
			{/each}
			<text class="fill-faint text-[11px] tabular-nums" x={width} y={middle} text-anchor="end" dominant-baseline="middle">
				{Math.floor(row.coverage)}%
			</text>
		{/each}
		{#each ticks as tick (tick)}
			<text class="fill-faint text-[11px] tabular-nums" x={x(tick)} y={height - 6} text-anchor="middle">{formatTick(tick, span)}</text>
		{/each}
	</svg>

	{#if broken.length === 0}
		<p class="mt-2 text-xs text-faint">Every source reported for as long as it has been recorded.</p>
	{:else}
		<ul class="mt-2 flex flex-col gap-1 text-xs">
			{#each broken as row (row.label)}
				{@const longest = [...row.gaps].sort((a, b) => b.to - b.from - (a.to - a.from)).slice(0, LISTED)}
				<li>
					<span class="font-semibold">{row.label}</span>
					<span class="text-muted-foreground">
						was silent {row.gaps.length === 1 ? 'once' : `${row.gaps.length} times`}:
						{#each longest as gap, i (gap.from)}{i > 0 ? ', ' : ''}{duration(gap.to - gap.from)} from {formatInstant(
								gap.from,
								span
							)}{/each}{row.gaps.length > LISTED ? `, and ${row.gaps.length - LISTED} shorter` : ''}.
					</span>
				</li>
			{/each}
		</ul>
	{/if}
	{#each young as row (row.label)}
		<p class="mt-1 text-xs text-faint">{row.label} has been recorded since {formatInstant(row.since ?? from, span)}.</p>
	{/each}
</div>
