// The colour theme, resolved before first paint (see app.html): the stored choice, or the OS
// preference for "system", becomes a concrete `dark` class on <html>, which is all app.css
// keys the palette on. mode-watcher owns the choice after load; this reads the key it writes.
// It reads one key of localStorage and sets one class; nothing from outside reaches it.
(() => {
	let mode = 'system';
	try {
		mode = localStorage.getItem('mode-watcher-mode') ?? 'system';
	} catch {
		// Private mode, or site data blocked: the OS preference decides instead.
	}
	const dark = mode === 'dark' || (mode !== 'light' && matchMedia('(prefers-color-scheme: dark)').matches);
	document.documentElement.classList.toggle('dark', dark);
	document.documentElement.style.colorScheme = dark ? 'dark' : 'light';
})();
