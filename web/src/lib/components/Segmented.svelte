<script lang="ts" generics="T extends string">
	// One choice out of a few, all visible: the time range, the theme, drive or bay. Buttons with
	// aria-pressed rather than a select, because seeing the other options is the point.
	import { Button } from '#lib/components/ui/button/index.ts';

	interface Props {
		label: string;
		options: readonly { key: T; label: string }[];
		value: T;
		onselect: (key: T) => void;
	}

	let { label, options, value, onselect }: Props = $props();
</script>

<div class="inline-flex flex-wrap rounded-lg border bg-card p-0.5" role="group" aria-label={label}>
	{#each options as option (option.key)}
		<Button
			variant={value === option.key ? 'secondary' : 'ghost'}
			size="sm"
			class={value === option.key ? 'font-semibold' : 'text-muted-foreground'}
			aria-pressed={value === option.key}
			onclick={() => onselect(option.key)}
		>
			{option.label}
		</Button>
	{/each}
</div>
