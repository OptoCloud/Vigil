# Reporting a vulnerability

Report it privately, through **Security → Report a vulnerability** on this repository (GitHub's
private vulnerability reporting). Please do not open a public issue for it.

Useful to include: which component (`vigild`, `vigil-core` or the dashboard), the version or
commit, and what an attacker can do. A proof of concept helps and is not required.

This is one person's project, so there is no response-time promise; a report that lets a
stranger change anything on the host, or read what the dashboard masks, is dropped everything
for.

## What counts

vigil's threat model is `docs/SECURITY.md`. In short: `vigild` runs as root and listens only on
a unix socket; `vigil-core` serves a public, unauthenticated, read-only dashboard, and
everything it serves is deliberately published (ADR-023). So "the dashboard shows temperatures
without a login" is the design. These are vulnerabilities:

- any request that changes state, on the host or in the database;
- anything served that the design masks: a full drive serial, a UPS identifier, a credential,
  a notification channel's address;
- one client exhausting what every visitor shares, past the documented limits;
- anything that lets a consumer of `vigild`'s socket drive a fan or run a command.

Only the latest release is supported.
