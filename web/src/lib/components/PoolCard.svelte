<script lang="ts">
	import { resolve } from '$app/paths';
	import { relativeTime } from '#lib/chart/scale.ts';
	import * as Table from '#lib/components/ui/table/index.ts';
	import { formatCount, shortPort } from '#lib/drives.ts';
	import type { PoolStatus } from '#lib/types.ts';
	import { describeScan, formatBytes, poolVerdict, scanProgress, usedPercent, vdevGroups, vdevVerdict } from '#lib/zfs.ts';
	import Meter from './Meter.svelte';
	import Panel from './Panel.svelte';
	import StatusBadge from './StatusBadge.svelte';

	interface Props {
		pool: PoolStatus;
		now: number;
	}

	let { pool, now }: Props = $props();

	const verdict = $derived(poolVerdict(pool));
	const used = $derived(usedPercent(pool));
	const progress = $derived(scanProgress(pool.scan));
	const groups = $derived(vdevGroups(pool));
</script>

<Panel title={pool.name} subtitle="{formatBytes(pool.allocatedBytes)} of {formatBytes(pool.sizeBytes)} raw, parity included">
	{#snippet actions()}
		<StatusBadge class="text-[13px] font-semibold" level={verdict.level} label={verdict.label} />
	{/snippet}

	<div class="flex flex-col gap-4">
		{#if used !== null}
			<div class="flex items-center gap-3">
				<span class="w-20 text-muted-foreground">Used</span>
				<Meter class="flex-1" label="{pool.name} used" percent={used} />
				<span class="w-12 text-right font-semibold tabular-nums">{used.toFixed(0)}%</span>
			</div>
		{/if}

		{#if pool.scan}
			<div class="flex flex-wrap items-center gap-x-3 gap-y-1">
				<span class="w-20 text-muted-foreground">Last scan</span>
				{#if progress !== null}
					<Meter class="flex-1" label="{describeScan(pool.scan)} progress" percent={progress * 100} />
					<span class="w-12 text-right font-semibold tabular-nums">{(progress * 100).toFixed(1)}%</span>
				{/if}
				<span class="basis-full pl-23 text-xs text-faint">
					{describeScan(pool.scan)}{#if pool.scan.endTime}, {relativeTime(pool.scan.endTime, now)}{:else if pool.scan.startTime}, started {relativeTime(
							pool.scan.startTime,
							now
						)}{/if}{#if pool.scan.errors}: {pool.scan.errors} errors repaired or found{/if}
				</span>
			</div>
		{/if}

		{#if (pool.errorCount ?? 0) > 0}
			<p class="text-status-critical">
				{pool.errorCount} permanent data errors: blocks ZFS could not repair. <code>zpool status -v {pool.name}</code> lists the files.
			</p>
		{/if}

		<Table.Root>
			<Table.Header>
				<Table.Row class="border-grid hover:bg-transparent">
					<Table.Head scope="col" class="pl-0 text-xs text-muted-foreground">Device</Table.Head>
					<Table.Head scope="col" class="text-xs text-muted-foreground">Bay</Table.Head>
					<Table.Head scope="col" class="text-xs text-muted-foreground">State</Table.Head>
					<Table.Head scope="col" class="text-right text-xs text-muted-foreground">Read</Table.Head>
					<Table.Head scope="col" class="text-right text-xs text-muted-foreground">Write</Table.Head>
					<Table.Head scope="col" class="pr-0 text-right text-xs text-muted-foreground">Checksum</Table.Head>
				</Table.Row>
			</Table.Header>
			{#each groups as group (group.parent)}
				<Table.Body>
					<Table.Row class="border-grid hover:bg-transparent">
						<Table.Head scope="rowgroup" colspan={6} class="h-8 pl-0 text-xs font-semibold text-muted-foreground">{group.parent}</Table.Head
						>
					</Table.Row>
					{#each group.vdevs as vdev (vdev.guid ?? vdev.name)}
						{@const v = vdevVerdict(vdev)}
						<Table.Row class="border-grid">
							<Table.Head scope="row" class="max-w-80 truncate pl-3 font-normal" title={vdev.path ?? vdev.name}>
								{#if vdev.deviceName}
									<a class="underline-offset-4 hover:underline" href={resolve('/drives/[wwn]', { wwn: vdev.deviceName })}>{vdev.name}</a>
								{:else}
									{vdev.name}
								{/if}
							</Table.Head>
							<Table.Cell class="tabular-nums" title={vdev.port ?? ''}>{shortPort(vdev.port)}</Table.Cell>
							<Table.Cell><StatusBadge level={v.level} label={v.label} /></Table.Cell>
							<Table.Cell class="text-right tabular-nums">{formatCount(vdev.readErrors)}</Table.Cell>
							<Table.Cell class="text-right tabular-nums">{formatCount(vdev.writeErrors)}</Table.Cell>
							<Table.Cell class="pr-0 text-right tabular-nums">{formatCount(vdev.checksumErrors)}</Table.Cell>
						</Table.Row>
					{/each}
				</Table.Body>
			{/each}
		</Table.Root>
	</div>
</Panel>
