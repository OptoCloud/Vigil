// A disk's name is only known once the disk is, so this page cannot be built ahead of time: it is
// the build's fallback page (spa.html), which vigil-core serves for /drives/<name> (ADR-020), and
// it reads the name from the URL in the browser.
export const prerender = false;
