<script lang="ts">
	import { humanize } from '#lib/chart/scale.ts';
	import * as Table from '#lib/components/ui/table/index.ts';
	import type { FanStatus } from '#lib/types.ts';
	import Meter from './Meter.svelte';
	import StatusBadge from './StatusBadge.svelte';

	let { fans }: { fans: FanStatus[] } = $props();

	const sorted = $derived([...fans].sort((a, b) => a.id.localeCompare(b.id)));

	function state(fan: FanStatus): { level: 'good' | 'warning' | 'critical'; label: string } {
		if (fan.stalled) return { level: 'critical', label: 'Stalled' };
		if (fan.mode !== 'manual') return { level: 'warning', label: fan.mode === null ? 'Mode unreadable' : `Not controlled (${fan.mode})` };
		return { level: 'good', label: 'Controlled' };
	}
</script>

<Table.Root>
	<Table.Header class="sr-only">
		<Table.Row
			><Table.Head scope="col">Fan</Table.Head><Table.Head scope="col">Duty</Table.Head><Table.Head scope="col">Speed</Table.Head
			><Table.Head scope="col">State</Table.Head></Table.Row
		>
	</Table.Header>
	<Table.Body>
		{#each sorted as fan (fan.id)}
			{@const s = state(fan)}
			<Table.Row class="border-grid hover:bg-transparent">
				<Table.Head scope="row" class="pl-0 font-normal">{humanize(fan.id)}</Table.Head>
				<Table.Cell class="w-full">
					<div class="flex items-center gap-2">
						<Meter class="flex-1" label="{humanize(fan.id)} duty" percent={fan.dutyPercent} />
						<span class="w-9.5 text-right font-semibold tabular-nums">{fan.dutyPercent}%</span>
					</div>
				</Table.Cell>
				<Table.Cell class="px-4 text-right tabular-nums"
					>{fan.rpm === null ? 'n/a' : fan.rpm.toLocaleString()} <span class="text-faint">rpm</span></Table.Cell
				>
				<Table.Cell class="pr-0 text-xs"><StatusBadge level={s.level} label={s.label} /></Table.Cell>
			</Table.Row>
		{:else}
			<Table.Row><Table.Cell class="text-faint">Waiting for the daemon.</Table.Cell></Table.Row>
		{/each}
	</Table.Body>
</Table.Root>
