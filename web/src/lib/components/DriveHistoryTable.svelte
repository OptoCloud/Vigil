<script lang="ts">
	// One drive's recorded health changes. A row is written only when a count moved, so each one
	// is something that happened to the disk, not a poll.
	import * as Table from '#lib/components/ui/table/index.ts';
	import { formatCount, formatYears } from '#lib/drives.ts';
	import type { DriveHealthRecord } from '#lib/types.ts';

	let { records }: { records: DriveHealthRecord[] } = $props();

	const passed = (value: boolean | null) => (value === null ? 'n/a' : value ? 'Passed' : 'FAILED');
</script>

<Table.Root>
	<Table.Header>
		<Table.Row class="border-grid hover:bg-transparent">
			<Table.Head scope="col" class="pl-0 text-xs text-muted-foreground">When</Table.Head>
			<Table.Head scope="col" class="text-xs text-muted-foreground">SMART</Table.Head>
			<Table.Head scope="col" class="text-right text-xs text-muted-foreground">Reallocated</Table.Head>
			<Table.Head scope="col" class="text-right text-xs text-muted-foreground">Pending</Table.Head>
			<Table.Head scope="col" class="pr-0 text-right text-xs text-muted-foreground">Powered on</Table.Head>
		</Table.Row>
	</Table.Header>
	<Table.Body>
		{#each records as record (record.ts)}
			<Table.Row class="border-grid">
				<Table.Head scope="row" class="pl-0 font-normal tabular-nums">
					<time datetime={record.ts}>{new Date(record.ts).toLocaleString()}</time>
				</Table.Head>
				<Table.Cell>{passed(record.passed)}</Table.Cell>
				<Table.Cell class="text-right tabular-nums">{formatCount(record.reallocatedSectorCount)}</Table.Cell>
				<Table.Cell class="text-right tabular-nums">{formatCount(record.pendingSectorCount)}</Table.Cell>
				<Table.Cell class="pr-0 text-right tabular-nums">{formatYears(record.powerOnHours)}</Table.Cell>
			</Table.Row>
		{:else}
			<Table.Row
				><Table.Cell colspan={5} class="text-faint"
					>No changes recorded: the counts have not moved since vigil first saw this drive.</Table.Cell
				></Table.Row
			>
		{/each}
	</Table.Body>
</Table.Root>
