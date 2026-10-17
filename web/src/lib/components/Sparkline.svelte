<script lang="ts">
	import { linearScale } from '#lib/chart/scale.ts';
	import type { SeriesPoints } from '#lib/types.ts';

	interface Props {
		points: SeriesPoints;
		width?: number;
		height?: number;
		/** Hidden from assistive tech when the number beside it already says it all (the stat tiles). */
		decorative?: boolean;
	}

	let { points, width = 96, height = 22, decorative = false }: Props = $props();

	/** Degrees of headroom at least, so a flat 38.0-38.2 doesn't get drawn as a mountain range. */
	const MIN_SPAN = 4;

	const trend = $derived.by(() => {
		if (points.length < 2) return null;
		const values = points.map((point) => point[1]);
		const min = Math.min(...values);
		const max = Math.max(...values);
		const padding = Math.max(0, (MIN_SPAN - (max - min)) / 2);
		const x = linearScale([points[0][0], points[points.length - 1][0]], [2, width - 4]);
		const y = linearScale([min - padding, max + padding], [height - 3, 3]);
		const last = points[points.length - 1];
		return {
			path: points.map(([time, value], i) => `${i === 0 ? 'M' : 'L'}${x(time).toFixed(1)},${y(value).toFixed(1)}`).join(''),
			end: { x: x(last[0]), y: y(last[1]) },
			label: `${min.toFixed(0)} to ${max.toFixed(0)}°C`
		};
	});
</script>

{#if trend}
	<svg
		{width}
		{height}
		class="flex-none"
		role={decorative ? undefined : 'img'}
		aria-hidden={decorative ? 'true' : undefined}
		aria-label={decorative ? undefined : trend.label}
	>
		{#if !decorative}<title>{trend.label}</title>{/if}
		<path d={trend.path} fill="none" stroke="var(--text-muted)" stroke-width="1.5" stroke-linejoin="round" stroke-linecap="round" />
		<circle cx={trend.end.x} cy={trend.end.y} r="2.5" fill="var(--series-1)" stroke="var(--surface)" stroke-width="1" />
	</svg>
{:else if !decorative}
	<span class="text-faint">no history</span>
{/if}
