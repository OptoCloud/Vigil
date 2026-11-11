<script lang="ts">
	// Every condition vigil-core has raised and not yet cleared: the verdict, spelled out. Shown
	// under the header on every page, so a problem cannot hide on a section nobody opened.
	import { relativeTime } from '#lib/chart/scale.ts';
	import type { Alert } from '#lib/types.ts';
	import StatusBadge from './StatusBadge.svelte';

	interface Props {
		alerts: Alert[];
		now: number;
	}

	let { alerts, now }: Props = $props();

	const label = { critical: 'Critical', warning: 'Warning', info: 'Info' } as const;
</script>

<section aria-label="Raised conditions" class="rounded-lg border bg-card px-3.5 py-1.5">
	<ul class="m-0 list-none p-0">
		{#each alerts as alert (alert.kind)}
			<li class="grid grid-cols-[84px_1fr_auto] items-start gap-3 border-b border-grid py-1.5 last:border-b-0">
				<span class="pt-px text-xs"><StatusBadge level={alert.severity} label={label[alert.severity]} /></span>
				<span>{alert.message}</span>
				<time
					class="text-xs whitespace-nowrap text-faint tabular-nums"
					datetime={alert.since}
					title="Raised {new Date(alert.since).toLocaleString()}">since {relativeTime(alert.since, now)}</time
				>
			</li>
		{/each}
	</ul>
</section>
