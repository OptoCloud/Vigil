<script lang="ts">
	import '../app.css';
	import { ModeWatcher } from 'mode-watcher';
	import { onMount } from 'svelte';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import favicon from '#lib/assets/favicon.svg';
	import { relativeTime } from '#lib/chart/scale.ts';
	import Banner from '#lib/components/Banner.svelte';
	import StatusBadge from '#lib/components/StatusBadge.svelte';
	import ThemePicker from '#lib/components/ThemePicker.svelte';
	import { Live, setLive } from '#lib/live.svelte.ts';
	import { connectionStatus } from '#lib/status.ts';
	import { summarizeUps } from '#lib/ups.ts';

	let { children } = $props();

	// One connection for every page: navigating keeps the stream, the event log and the history.
	const live = setLive(new Live());
	onMount(() => live.connect());

	const status = $derived(connectionStatus(live.stream, live.daemonConnected, live.snapshot, live.now));
	const upsSummary = $derived(summarizeUps(live.ups));

	const NAV = [
		{ route: '/', label: 'Dashboard' },
		{ route: '/drives', label: 'Drives' },
		{ route: '/storage', label: 'Storage' },
		{ route: '/cooling', label: 'Cooling' },
		{ route: '/power', label: 'Power' },
		{ route: '/events', label: 'Events' }
	] as const;

	/** A section is current on its own page and every page under it: Drives on a disk's page. */
	const current = (route: (typeof NAV)[number]['route']) =>
		route === '/' ? page.url.pathname === resolve('/') : page.url.pathname.startsWith(resolve(route));
</script>

<!-- Its own head script is off: static/theme.js does that job under the page's CSP (see app.html). -->
<ModeWatcher disableHeadScriptInjection />

<svelte:head>
	<link rel="icon" href={favicon} />
	<meta name="robots" content="noindex" />
</svelte:head>

<div class="mx-auto flex max-w-7xl flex-col gap-3.5 px-4 pt-5 pb-12">
	<header class="flex flex-wrap items-center justify-between gap-3">
		<div>
			<a href={resolve('/')} class="text-xl font-semibold tracking-tight">vigil</a>
			<p class="text-faint">
				{#if live.snapshot}
					{live.snapshot.sensors.length} sensors, {live.snapshot.fans.length} fans, updated {relativeTime(
						live.snapshot.timestampUtc,
						live.now
					)}
				{:else}
					No data yet
				{/if}
			</p>
		</div>
		<div class="flex flex-wrap items-center gap-3.5">
			<span class="font-semibold" title={status.detail}><StatusBadge level={status.level} label={status.label} /></span>
			<ThemePicker />
		</div>
	</header>

	<nav aria-label="Sections" class="-mx-1 flex flex-wrap gap-1 border-b pb-2">
		{#each NAV as item (item.route)}
			<a
				href={resolve(item.route)}
				aria-current={current(item.route) ? 'page' : undefined}
				class="rounded-md px-2.5 py-1 text-muted-foreground hover:bg-accent hover:text-foreground aria-[current=page]:bg-secondary aria-[current=page]:font-semibold aria-[current=page]:text-foreground"
			>
				{item.label}
			</a>
		{/each}
	</nav>

	{#if status.detail}
		<Banner level={status.level} message={status.detail} />
	{/if}
	{#if live.ups.enabled && upsSummary.banner}
		<Banner level={upsSummary.level} message={upsSummary.banner} role="alert" />
	{/if}

	<main class="flex flex-col gap-3.5">
		{@render children()}
	</main>
</div>
