# Changelog

What changed for someone running vigil: settings, routes, alerts, and anything a deployment has
to do. The reasons are in `docs/DECISIONS.md` and the commits. Releases are tags
(`vMAJOR.MINOR.PATCH`, ADR-022); each one's full list is on its GitHub release.

## v1.3.1 (2026-11-24)

### Fixed

- The capacity forecast takes its rate from what a usual day adds, so one large copy is
  reported as the one-off it was and no longer reads as "full in three weeks".

## v1.3.0 (2026-11-24)

### To do when upgrading

- **DIMM sensors change their ids once more**, from `dimm:hwmonN` to `dimm:<i2c address>`
  (`dimm:0-001a`). Their history starts again under the new ids and then stays put.

### Added

- An Insights page: each fan's duty against the temperature it tracks, a metric by day and
  time of day, when each pool reaches 80 % and 90 %, and when a source was not reporting.
- `NUT_UPS_FALLBACK`: the UPS under a second name on upsd, read while the first is unreadable.

### Fixed

- A DIMM's sensor id no longer changes with every reboot.
- `daemon/scripts/integration-test.sh` runs as an ordinary user again (`instance_lock_path`).

## v1.2.0 (2026-11-24)

### To do when upgrading

- **`NTFY_URL` no longer does anything** (ADR-028). Alerts go out as texts (`SMS_URL`,
  `SMS_TOKEN`) or email (`SMTP_HOST`, `EMAIL_FROM`, `EMAIL_TO`). A deployment that had only
  ntfy notifies nobody until one of those is set.
- The dashboard asks for `/api/events?before=…` and `/api/status`; a proxy that allows paths
  by list needs both.

### Added

- Email alerts over SMTP, with STARTTLS and an optional login.
- A notification that fails is tried again after 30 seconds, 2 minutes and 10 minutes, in order.
- Alerts for a sensor over its limit (`TEMPERATURE_LIMITS`, with defaults per category), and
  for the host's memory nearly gone, its swap half used, or its tasks stalled on memory.
- `HEARTBEAT_URL`: a request every minute while vigil-core is working, for a service that
  alerts when they stop.
- `/api/status`: readiness and the running version. `/health` stays the liveness check.
- `/metrics`, in Prometheus' text format, and `core/openapi.json`, which describes the API.
- Each release carries an SBOM, `vigil.spdx.json`.

### Changed

- A release tag no longer runs the checks; it builds and publishes what master already holds.
- `/api/events` takes `limit`, `before` and `severity`, and the Events page can load older events.
- Every chart downloads as CSV, and the charts step to earlier windows (`/api/history?to=`).

### Removed

- ntfy.

### Fixed

- The database tests no longer fail once their fixed date is older than the raw retention.

## v1.1.0 (2026-11-19)

The host's load from `/proc`, live and recorded, with a System page (ADR-025). A time series
of every UPS reading (ADR-026). The SMS channel (ADR-027).

## v1.0.0 (2026-11-16)

The first semver release: the dashboard public and read-only, with per-client limits and
masked identifiers (ADR-023, ADR-024). Earlier releases were dated tags.
