<script lang="ts">
	// mode-watcher owns the choice and writes it where static/theme.js reads it before the next
	// first paint; "system" follows the OS, live.
	import { setMode, userPrefersMode } from 'mode-watcher';
	import Segmented from './Segmented.svelte';

	const OPTIONS = [
		{ key: 'system', label: 'Auto' },
		{ key: 'light', label: 'Light' },
		{ key: 'dark', label: 'Dark' }
	] as const;
</script>

<Segmented label="Colour theme" options={OPTIONS} value={userPrefersMode.current} onselect={setMode} />
