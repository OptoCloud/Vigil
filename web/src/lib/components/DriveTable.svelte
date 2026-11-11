<script lang="ts">
	import { resolve } from '$app/paths';
	import { relativeTime } from '#lib/chart/scale.ts';
	import * as Table from '#lib/components/ui/table/index.ts';
	import {
		bayRows,
		deviceOf,
		diskVerdict,
		driveRows,
		formatCount,
		formatYears,
		membershipsOf,
		roleOf,
		shortPort,
		shortWwn,
		smartVerdict,
		zfsDiskVerdict,
		zfsErrors
	} from '#lib/drives.ts';
	import type { Bay, DriveState, HistoryResponse, SensorReading, ZfsStatus } from '#lib/types.ts';
	import Sparkline from './Sparkline.svelte';
	import StatusBadge from './StatusBadge.svelte';

	interface Props {
		/** Live temperatures, from the daemon's snapshot. */
		sensors: SensorReading[];
		/** Last good SMART state per drive, which vigil-core remembers across the drive's naps. */
		drives: DriveState[];
		/** The selected range's history, for each row's trend. */
		history: HistoryResponse | null;
		/** ZFS's view of the same disks, joined by the name every source shares. */
		zfs: ZfsStatus | null | undefined;
		/** Where each port is in the case, from vigild's `[[bays]]`. */
		bayConfig: Bay[];
		now: number;
		/**
		 * Two ways to read the same temperatures. By drive follows a disk wherever it is plugged
		 * in (is this disk running warm?); by bay follows a spot in the case whichever disk sits
		 * there (is this bay getting enough air?). They only differ once a disk has been moved.
		 */
		view: 'drive' | 'bay';
	}

	let { sensors, drives, history, zfs, bayConfig, now, view }: Props = $props();

	// Thirteen drives are far past where colour can tell series apart, so this is a table, with
	// a small magnitude bar doing the at-a-glance comparison over this range of temperatures.
	const BAR_MIN = 20;
	const BAR_MAX = 60;

	const rows = $derived(driveRows(sensors, drives, history));
	const bays = $derived(bayRows(sensors, drives, history, bayConfig));

	const barWidth = (celsius: number) => Math.min(100, Math.max(3, ((celsius - BAR_MIN) / (BAR_MAX - BAR_MIN)) * 100));
</script>

{#if view === 'drive'}
	<Table.Root>
		<Table.Header>
			<Table.Row class="border-grid hover:bg-transparent">
				<Table.Head scope="col" class="pl-0 text-xs text-muted-foreground">Drive</Table.Head>
				<Table.Head scope="col" class="text-xs text-muted-foreground">Bay</Table.Head>
				<Table.Head scope="col" class="text-xs text-muted-foreground">Temperature</Table.Head>
				<Table.Head scope="col" class="text-xs text-muted-foreground">Trend</Table.Head>
				<Table.Head scope="col" class="text-xs text-muted-foreground">SMART</Table.Head>
				<Table.Head scope="col" class="text-xs text-muted-foreground">ZFS</Table.Head>
				<Table.Head scope="col" class="text-right text-xs text-muted-foreground">ZFS errors</Table.Head>
				<Table.Head scope="col" class="text-right text-xs text-muted-foreground">Reallocated</Table.Head>
				<Table.Head scope="col" class="text-right text-xs text-muted-foreground">Pending</Table.Head>
				<Table.Head scope="col" class="text-right text-xs text-muted-foreground">Powered on</Table.Head>
				<Table.Head scope="col" class="pr-0 text-right text-xs text-muted-foreground">Checked</Table.Head>
			</Table.Row>
		</Table.Header>
		<Table.Body>
			{#each rows as row (row.wwn)}
				{@const s = smartVerdict(row.health)}
				{@const memberships = membershipsOf(zfs, row.wwn)}
				{@const z = zfsDiskVerdict(memberships)}
				<Table.Row class="border-grid">
					<!-- The WWN is the drive's identity: unlike sdX it survives reboots and port changes. -->
					<Table.Head scope="row" class="pl-0 font-normal" title={row.wwn}>
						<a class="mr-1.5 font-medium tabular-nums underline-offset-4 hover:underline" href={resolve('/drives/[wwn]', { wwn: row.wwn })}
							>{shortWwn(row.wwn)}</a
						>
						<span class="text-faint">{deviceOf(row.health?.sourcePath)}</span>
					</Table.Head>
					<Table.Cell class="tabular-nums" title={row.port ?? ''}>{shortPort(row.port)}</Table.Cell>
					<Table.Cell>{@render temperature(row.sensor?.celsiusOrNull ?? null)}</Table.Cell>
					<Table.Cell><Sparkline points={row.trend} /></Table.Cell>
					<Table.Cell><StatusBadge level={s.level} label={s.label} /></Table.Cell>
					<Table.Cell>
						{#if z}
							<StatusBadge level={z.level} label={z.label} />
							<span class="ml-1 text-xs text-faint">{memberships.map(roleOf).join('; ')}</span>
						{:else}
							<span class="text-faint">not in a pool</span>
						{/if}
					</Table.Cell>
					<Table.Cell class="text-right tabular-nums">{formatCount(zfsErrors(memberships))}</Table.Cell>
					<Table.Cell class="text-right tabular-nums">{formatCount(row.health?.reallocatedSectorCount)}</Table.Cell>
					<Table.Cell class="text-right tabular-nums">{formatCount(row.health?.pendingSectorCount)}</Table.Cell>
					<Table.Cell class="text-right tabular-nums">{formatYears(row.health?.powerOnHours)}</Table.Cell>
					<Table.Cell class="pr-0 text-right text-faint tabular-nums">{row.health ? relativeTime(row.health.asOf, now) : ''}</Table.Cell>
				</Table.Row>
			{:else}
				<Table.Row><Table.Cell colspan={11} class="text-faint">No drives reported yet.</Table.Cell></Table.Row>
			{/each}
		</Table.Body>
	</Table.Root>
{:else}
	<Table.Root>
		<Table.Header>
			<Table.Row class="border-grid hover:bg-transparent">
				<Table.Head scope="col" class="pl-0 text-xs text-muted-foreground">Bay</Table.Head>
				<Table.Head scope="col" class="text-xs text-muted-foreground">Drive in it</Table.Head>
				<Table.Head scope="col" class="text-xs text-muted-foreground">ZFS</Table.Head>
				<Table.Head scope="col" class="text-right text-xs text-muted-foreground">ZFS errors</Table.Head>
				<Table.Head scope="col" class="text-xs text-muted-foreground">Temperature</Table.Head>
				<Table.Head scope="col" class="text-xs text-muted-foreground">Trend</Table.Head>
				<Table.Head scope="col" class="pr-0 text-xs text-muted-foreground">Health</Table.Head>
			</Table.Row>
		</Table.Header>
		<Table.Body>
			{#each bays as bay (bay.port)}
				{@const memberships = bay.wwn ? membershipsOf(zfs, bay.wwn) : []}
				{@const s = diskVerdict(bay.health, memberships)}
				{@const z = zfsDiskVerdict(memberships)}
				<Table.Row class="border-grid">
					<!-- The bay is the place: it stays put whatever disk is in it, so it is what to read before pulling one. -->
					<Table.Head scope="row" class="pl-0 font-normal" title={bay.port}>
						{#if bay.place}
							<span class="font-medium">{bay.place.cage} · {bay.place.slot}</span>
							<span class="block text-xs text-faint tabular-nums">{shortPort(bay.port)}</span>
						{:else}
							<span class="tabular-nums">{shortPort(bay.port)}</span>
							<span class="block text-xs text-faint">not in [[bays]]</span>
						{/if}
					</Table.Head>
					<Table.Cell title={bay.wwn ?? ''}>
						{#if bay.wwn}
							<a class="font-medium tabular-nums underline-offset-4 hover:underline" href={resolve('/drives/[wwn]', { wwn: bay.wwn })}
								>{bay.health?.serialNumber ?? shortWwn(bay.wwn)}</a
							>
							{#if bay.health?.serialNumber}<span class="block text-xs text-faint tabular-nums">{shortWwn(bay.wwn)}</span>{/if}
						{:else}
							<span class="text-faint">empty</span>
						{/if}
					</Table.Cell>
					<Table.Cell>
						{#if z}
							<StatusBadge level={z.level} label={z.label} />
							<span class="block text-xs text-faint">{memberships.map(roleOf).join('; ')}</span>
						{:else if bay.wwn}
							<span class="text-faint">not in a pool</span>
						{/if}
					</Table.Cell>
					<Table.Cell class="text-right tabular-nums">{bay.wwn ? formatCount(zfsErrors(memberships)) : ''}</Table.Cell>
					<!-- A bay that is empty now but had a disk earlier in the range keeps its trend. -->
					<Table.Cell
						>{#if bay.wwn}{@render temperature(bay.sensor?.celsiusOrNull ?? null)}{/if}</Table.Cell
					>
					<Table.Cell
						>{#if bay.wwn || bay.trend.length > 0}<Sparkline points={bay.trend} />{/if}</Table.Cell
					>
					<Table.Cell class="pr-0"
						>{#if bay.wwn}<StatusBadge level={s.level} label={s.label} />{/if}</Table.Cell
					>
				</Table.Row>
			{:else}
				<Table.Row>
					<Table.Cell colspan={7} class="text-faint"
						>No bays yet: list them as [[bays]] in vigild.toml, or wait for a drive that reports its port.</Table.Cell
					>
				</Table.Row>
			{/each}
		</Table.Body>
	</Table.Root>
{/if}

{#snippet temperature(celsius: number | null)}
	{#if celsius === null}
		<span class="text-faint">asleep or unreadable</span>
	{:else}
		<div class="flex min-w-37.5 items-center gap-2">
			<!-- Thin, one hue, square at the baseline and rounded at the data end. -->
			<div class="flex-1"><div class="h-2 rounded-r bg-series-1" style:width="{barWidth(celsius)}%"></div></div>
			<span class="tabular-nums">{celsius.toFixed(0)}°C</span>
		</div>
	{/if}
{/snippet}
