<script lang="ts">
	// The card every section sits in: a title, optionally a link to its own page or controls on
	// the right, then the content. A wrapper over shadcn's Card, which stays unedited (ADR-011).
	import type { Snippet } from 'svelte';
	import * as Card from '#lib/components/ui/card/index.ts';

	interface Props {
		title: string;
		/** A line under the title: the model, the pool's size. */
		subtitle?: string;
		/** Where "View all" goes: the page this panel summarises. */
		more?: { href: string; label: string };
		actions?: Snippet;
		children: Snippet;
		class?: string;
	}

	let { title, subtitle, more, actions, children, class: className = '' }: Props = $props();
</script>

<Card.Root class="min-w-0 gap-3 {className}">
	<Card.Header class="flex flex-wrap items-center justify-between gap-2">
		<div class="min-w-0">
			<Card.Title class="text-sm font-semibold text-muted-foreground"><h2>{title}</h2></Card.Title>
			{#if subtitle}<p class="truncate text-xs text-faint">{subtitle}</p>{/if}
		</div>
		<div class="flex items-center gap-3">
			{@render actions?.()}
			{#if more}
				<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- resolved by the caller, which knows the route -->
				<a class="text-xs text-muted-foreground underline-offset-4 hover:text-foreground hover:underline" href={more.href}>{more.label}</a>
			{/if}
		</div>
	</Card.Header>
	<Card.Content class="min-w-0">
		{@render children()}
	</Card.Content>
</Card.Root>
