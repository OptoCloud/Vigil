<script lang="ts">
	import { relativeTime } from '#lib/chart/scale.ts';
	import type { SeriesPoints, UpsState } from '#lib/types.ts';
	import {
		describeTransferReason,
		formatDuration,
		formatRuntime,
		guardVerdict,
		marginVerdict,
		shutdownMargin,
		summarizeUps
	} from '#lib/ups.ts';
	import * as Card from '#lib/components/ui/card/index.ts';
	import Meter from './Meter.svelte';
	import StatusBadge from './StatusBadge.svelte';

	interface Props {
		ups: UpsState;
		/** Mains voltage history for the selected range, summarised as its lowest and highest. */
		inputVoltage: SeriesPoints;
		now: number;
	}

	let { ups, inputVoltage, now }: Props = $props();

	const reading = $derived(ups.reading);
	const summary = $derived(summarizeUps(ups));
	const margin = $derived(shutdownMargin(ups));
	const marginBadge = $derived(margin ? marginVerdict(margin) : null);
	const guard = $derived(reading ? guardVerdict(reading.monitors) : null);
	const batteryHot = $derived(reading?.batteryTemperature != null && reading.batteryTemperature >= ups.limits.batteryTemperatureWarn);

	const voltageRange = $derived.by(() => {
		if (inputVoltage.length === 0) return null;
		const values = inputVoltage.map((point) => point[1]);
		return { min: Math.min(...values), max: Math.max(...values) };
	});

	const variables = $derived(reading ? Object.entries(reading.variables).sort(([a], [b]) => a.localeCompare(b)) : []);
	const outputDetail = $derived(
		[
			reading?.outputFrequency != null ? `${reading.outputFrequency.toFixed(1)} Hz` : null,
			reading?.outputCurrent != null ? `${reading.outputCurrent.toFixed(1)} A` : null
		]
			.filter((part) => part !== null)
			.join(', ')
	);
	const volts = (value: number | null) => (value === null ? 'n/a' : `${value.toFixed(value >= 100 ? 0 : 1)} V`);
</script>

<Card.Root class="min-w-0 gap-2">
	<Card.Header class="flex items-start justify-between gap-3">
		<div class="min-w-0">
			<Card.Title class="text-sm font-semibold text-muted-foreground"><h2>UPS</h2></Card.Title>
			<p class="text-xs text-faint">{reading?.model ?? ups.error ?? 'Waiting for NUT'}</p>
		</div>
		<StatusBadge class="text-[13px] font-semibold" level={summary.level} label={summary.label} />
	</Card.Header>
	<Card.Content class="min-w-0">
		{#if reading}
			<!-- One table of label and value rows. Its cells are styled from here, through arbitrary
		     variants, rather than a class list repeated on each of the dozen rows. -->
			<table
				class="w-full border-collapse [&_.detail]:block [&_.detail]:text-xs [&_.detail]:font-normal [&_.detail]:whitespace-normal [&_.detail]:text-faint [&_.muted]:ml-1.5 [&_.muted]:font-normal [&_.muted]:text-faint [&_.value]:pl-3 [&_.value]:text-right [&_.value]:font-semibold [&_.value]:tabular-nums [&_td]:border-b [&_td]:border-grid [&_td]:py-1.75 [&_td]:whitespace-nowrap [&_th]:border-b [&_th]:border-grid [&_th]:py-1.75 [&_th]:pr-3 [&_th]:text-left [&_th]:font-normal [&_th]:whitespace-nowrap [&_th]:text-muted-foreground [&_tr:last-child>*]:border-b-0"
			>
				<tbody>
					<tr>
						<th scope="row">Battery</th>
						<td class="w-full">
							{#if reading.batteryCharge !== null}
								<Meter label="Battery charge" percent={reading.batteryCharge} />
							{/if}
						</td>
						<td class="value">{reading.batteryCharge === null ? 'n/a' : `${Math.round(reading.batteryCharge)}%`}</td>
					</tr>
					<tr>
						<th scope="row">Load</th>
						<td class="w-full">
							{#if reading.load !== null}
								<Meter label="UPS load" percent={reading.load} />
							{/if}
						</td>
						<td class="value">
							{reading.load === null ? 'n/a' : `${Math.round(reading.load)}%`}
							{#if reading.realPower !== null}<span class="muted">{reading.realPower} W</span>{/if}
						</td>
					</tr>
					<tr>
						<th scope="row">Runtime</th>
						<td colspan="2" class="value"
							>{reading.batteryRuntimeSeconds === null ? 'n/a' : formatRuntime(reading.batteryRuntimeSeconds)}</td
						>
					</tr>
					{#if margin}
						<tr>
							<th scope="row">Shutdown at</th>
							<td colspan="2" class="value">
								{formatDuration(margin.startsAt)} left{#if reading.lowBatteryCharge !== null}&nbsp;or {Math.round(
										reading.lowBatteryCharge
									)}%{/if}
								{#if margin.untilShutdown !== null}<span class="detail"
										>after {formatRuntime(margin.untilShutdown)} on battery at this load</span
									>{/if}
							</td>
						</tr>
					{/if}
					{#if marginBadge && ups.limits.hostShutdownSeconds !== null}
						<tr>
							<th scope="row">Shutdown margin</th>
							<td colspan="2" class="value">
								<StatusBadge level={marginBadge.level} label={marginBadge.label} />
								<span class="detail">orion takes {formatDuration(ups.limits.hostShutdownSeconds)} to shut down</span>
							</td>
						</tr>
					{/if}
					{#if guard}
						<tr>
							<th scope="row">Watched by</th>
							<td colspan="2" class="value"><StatusBadge level={guard.level} label={guard.label} /></td>
						</tr>
					{/if}
					<tr>
						<th scope="row">Mains in</th>
						<td colspan="2" class="value">
							{volts(reading.inputVoltage)}
							{#if voltageRange}<span class="detail">{voltageRange.min.toFixed(0)} to {voltageRange.max.toFixed(0)} V in range</span>{/if}
						</td>
					</tr>
					<tr>
						<th scope="row">Output</th>
						<td colspan="2" class="value">
							{volts(reading.outputVoltage)}
							{#if outputDetail}<span class="detail">{outputDetail}</span>{/if}
						</td>
					</tr>
					{#if reading.transferReason !== null}
						<tr>
							<th scope="row">Last transfer</th>
							<td colspan="2" class="value">{describeTransferReason(reading.transferReason)}</td>
						</tr>
					{/if}
					<tr>
						<th scope="row">Battery voltage</th>
						<td colspan="2" class="value">{volts(reading.batteryVoltage)}</td>
					</tr>
					{#if reading.batteryTemperature !== null}
						<tr>
							<th scope="row">Battery temperature</th>
							<td colspan="2" class="value">
								{#if batteryHot}
									<StatusBadge level="warning" label="{Math.round(reading.batteryTemperature)} °C" />
								{:else}
									{Math.round(reading.batteryTemperature)} °C
								{/if}
								<span class="detail">alert at {ups.limits.batteryTemperatureWarn} °C</span>
							</td>
						</tr>
					{/if}
					{#if reading.batteryDate !== null}
						<tr>
							<th scope="row">Battery installed</th>
							<td colspan="2" class="value">{reading.batteryDate}</td>
						</tr>
					{/if}
				</tbody>
			</table>

			<details class="mt-2 text-xs">
				<summary class="cursor-pointer text-faint"
					>All {variables.length} variables from upsd, read {relativeTime(reading.timestampUtc, now)}</summary
				>
				<table
					class="w-full text-xs tabular-nums [&_td]:py-0.75 [&_td]:text-right [&_td]:break-words [&_th]:py-0.75 [&_th]:text-left [&_th]:font-normal [&_th]:break-words"
				>
					<tbody>
						{#each variables as [name, value] (name)}
							<tr><th scope="row">{name}</th><td>{value}</td></tr>
						{/each}
					</tbody>
				</table>
			</details>
		{:else}
			<p class="mt-3 text-faint">
				{ups.error ? `No reading: ${ups.error}.` : 'Connecting to upsd.'} This only affects the dashboard; orion's own upsmon still guards the
				shutdown.
			</p>
		{/if}
	</Card.Content>
</Card.Root>
