<script lang="ts">
	// Text that may hold vigil-core's redaction marker, with each marker drawn as a masked chip.
	// Text interpolation throughout, so a device name or a message stays inert.
	import { deviceLabel, MASK, splitWithheld } from '#lib/redaction.ts';

	interface Props {
		text: string;
		/** A by-id device name: shortened to its model and the shown tail of its serial. */
		device?: boolean;
	}

	let { text, device = false }: Props = $props();

	const parts = $derived(splitWithheld(device ? deviceLabel(text) : text));
</script>

{#each parts as part, index (index)}{#if part.withheld}<span
			class="rounded-sm bg-muted px-0.5 tracking-tighter text-faint"
			title="Withheld: this page is public"
		>
			<span aria-hidden="true">{MASK}</span><span class="sr-only">withheld</span>
		</span>{:else}{part.text}{/if}{/each}
