<script lang="ts" module>
	import { tv, type VariantProps } from 'tailwind-variants';

	// Status is never colour alone: every badge carries an icon shape and a text label too, and
	// the status colours are reserved for it (STYLE.md §11).
	const badge = tv({
		slots: {
			root: 'inline-flex items-center gap-1.5 whitespace-nowrap text-foreground',
			icon: 'flex-none fill-faint',
			// The mark is cut out of the icon in the surface colour.
			mark: 'fill-none stroke-card [stroke-linecap:round] [stroke-linejoin:round] [stroke-width:1.5]'
		},
		variants: {
			level: {
				good: { icon: 'fill-status-good' },
				// The warning fill is a light yellow, so its mark needs dark ink to stay legible.
				warning: { icon: 'fill-status-warning', mark: 'stroke-on-warning' },
				critical: { icon: 'fill-status-critical' },
				info: {},
				unknown: {}
			}
		}
	});

	export type Level = NonNullable<VariantProps<typeof badge>['level']>;
</script>

<script lang="ts">
	interface Props {
		level: Level;
		label: string;
		class?: string;
	}

	let { level, label, class: className }: Props = $props();

	const styles = $derived(badge({ level }));
</script>

<span class={styles.root({ class: className })}>
	<svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true" class={styles.icon()}>
		{#if level === 'good'}
			<circle cx="6" cy="6" r="5" />
			<path class={styles.mark()} d="M3.5 6.2 5.3 8 8.6 4.4" />
		{:else if level === 'warning'}
			<path d="M6 1 11.5 11H.5Z" />
			<path class={styles.mark()} d="M6 4.6v3M6 9.2v.1" />
		{:else if level === 'critical'}
			<rect x="1" y="1" width="10" height="10" rx="2" />
			<path class={styles.mark()} d="M4 4l4 4M8 4 4 8" />
		{:else}
			<circle cx="6" cy="6" r="5" />
			<path class={styles.mark()} d="M6 5.4v3M6 3.4v.1" />
		{/if}
	</svg>
	{label}
</span>
