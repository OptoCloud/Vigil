<script lang="ts">
	// A page-wide notice: the connection is down, the UPS is on battery. The stripe colour repeats
	// the level, and the text always says it in words.
	interface Props {
		level: 'good' | 'warning' | 'critical' | 'info' | 'unknown';
		message: string;
		role?: 'status' | 'alert';
	}

	let { level, message, role = 'status' }: Props = $props();
</script>

<p
	class="rounded-lg border border-l-4 bg-card px-3.5 py-2.5 {level === 'critical' ? 'border-l-status-critical' : 'border-l-status-warning'}"
	{role}
>
	{message}
</p>
