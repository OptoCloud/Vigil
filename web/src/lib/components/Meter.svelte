<script lang="ts">
	// A single ratio against a limit: fan duty, battery charge, pool capacity, scan progress. The
	// track is a lighter step of the fill's own ramp.
	interface Props {
		label: string;
		/** 0 to 100; clamped, so an overloaded UPS draws a full bar rather than spilling out. */
		percent: number;
		class?: string;
	}

	let { label, percent, class: className = '' }: Props = $props();
</script>

<div
	class="h-2 min-w-15 overflow-hidden rounded-full bg-meter-track {className}"
	role="meter"
	aria-label={label}
	aria-valuemin="0"
	aria-valuemax="100"
	aria-valuenow={Math.round(percent)}
>
	<!-- style: rather than a class, as the width is data. -->
	<div class="h-full rounded-full bg-meter-fill transition-[width] duration-400" style:width="{Math.min(100, Math.max(0, percent))}%"></div>
</div>
