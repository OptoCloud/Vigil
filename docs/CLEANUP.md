# Work plan

Opened 2026-11-24 from a review of the whole repository. The earlier plan (phases 0 to 8) is
done and in git history (`git log -- docs/CLEANUP.md`).

Each phase ends green and shippable: `scripts/check.sh` passes and the tree is deployable.
Every item names the evidence that produced it, and is ticked only when its enforcer (test,
lint or checklist line) is in place too. A box is ticked in the commit that does the work.

**Scope, decided 2026-11-24:** vigil supports the hardware installed in the host it runs on,
and nothing else. That host has an AMD CPU (`k10temp`), an NCT6798D, `jc42` DIMMs, SATA disks
on the board and on an `mpt3sas` HBA, an NVIDIA GPU and a NUT UPS. So NVMe, Intel `coretemp`,
other Super I/O chips, SAS-protocol SMART, other GPU and HBA vendors, IPMI, a configurable
sensor whitelist and other CPU architectures are out, and are not listed below.

## Phase 9: the docs say what the code does

- [x] 9.1 `scripts/check.sh` loses its PENDING mechanism. *Evidence:* `todo()` and the
      "tracked debt" header remain, and nothing has called them since phase 8.
- [x] 9.2 NativeAOT is described as it is. *Evidence:* the unit sets
      `MemoryDenyWriteExecute=true` (ADR-016, amended), while `README.md` "vigil-core and the
      dashboard", the exceptions table in `docs/SECURITY.md` §3.3 and a comment in
      `scripts/check.sh` still say .NET's JIT rules it out.
- [x] 9.3 No reference to a phase that has landed. *Evidence:* `docs/ARCHITECTURE.md`
      ("arrives in Phase 5"), `docs/STYLE.md` §11 ("From Phase 5"). The architecture diagram
      gains the SMS gateway.
- [x] 9.4 README's Status says vigil-core is deployed. *Evidence:* on the host, 2026-11-24,
      v1.1.0 is running in its guest and logs `database ready` against the real TimescaleDB;
      the README still says neither has happened.
- [x] 9.5 `HistoryResponse` is in `core/contract.json`. *Evidence:*
      `core/Vigil.Core/Protocol/History.cs` says "not yet", and a hand-written test pins its
      property names instead. *Enforcer:* the contract tests, as for every other wire type.
- [x] 9.6 The repository says how to contribute and how to report a vulnerability:
      `CHANGELOG.md`, `CONTRIBUTING.md`, `.github/SECURITY.md`, issue and pull request
      templates. *Evidence:* it is public and AGPL, and has none of them.

## Phase 10: alerting

Vigil reaches a person by text or by email, and by nothing else (decided 2026-11-24).

- [x] 10.0 ntfy is gone: `NtfyClient`, `NTFY_URL`, their tests and every line that describes
      them. *Evidence:* the owner's decision; recorded as ADR-028, which amends ADR-021.
- [x] 10.1 A temperature that is too high is a condition. *Evidence:* `Alerting/` judges no
      temperature but the UPS battery's; a drive at 60 °C with its fan at 100 % raises
      nothing. Limits per sensor category, from the environment, with hysteresis as the UPS
      battery's has.
- [x] 10.2 Host load is judged: memory nearly gone, swap in use, sustained memory or I/O
      pressure. *Evidence:* ADR-025 records and charts it; nothing raises it.
- [x] 10.3 A notification that fails is retried, bounded. *Evidence:* `SmsClient`
      logs a failed send and drops it, and a text exists for exactly the moment the network
      is unwell.
- [x] 10.4 An outbound heartbeat, so something outside the host notices a dead vigil-core or
      a dead host. *Evidence:* every alert vigil sends needs vigil-core alive to send it.
- [x] 10.5 An email channel over SMTP, beside the texts. *Evidence:* with ntfy gone, the SMS
      gateway is the only way out, and it does not exist until its modem does (ADR-027).
- [x] 10.6 The event log can be paged and filtered. *Evidence:* `/api/events` serves the
      newest 50 and nothing else; an event older than that is only in Postgres.

## Phase 11: operations

- [x] 11.1 `/health` stays the liveness check; a readiness route says whether vigild is
      reachable, whether the database is up and how old the last snapshot is, and names the
      running version. *Evidence:* `/health` answers `ok` whatever is broken.
- [x] 11.2 `/metrics` in Prometheus' text format, from the state the live stream already
      publishes. *Evidence:* there is no way to scrape vigil.
- [x] 11.3 The API is described by an OpenAPI document, generated and checked like the
      contract files. *Evidence:* the routes are a table in the README.
- [x] 11.4 Backing up and restoring the database is documented. *Evidence:* the hourly
      rollups are the only copy of old history (ADR-026) and nothing says how to keep them.
- [x] 11.5 CI runs the database tests against a TimescaleDB service and runs
      `daemon/scripts/integration-test.sh`. *Evidence:* `check.yml` sets no
      `VIGIL_TEST_DATABASE_URL`, so those tests skip there, and never runs the script. The
      script had gone stale the same way: it still expected the request cap that became a
      stream cap. Run
      against a TimescaleDB on 2026-11-24, two of them failed: their fixed date had aged past
      the raw retention a month before, and nothing had run them since (fixed with 10.x).
- [x] 11.6 Supply chain: NuGet packages are audited as cargo's and npm's are, the release
      carries an SBOM, and CodeQL runs. *Evidence:* `scripts/check.sh` has `cargo deny` and
      `npm audit` and nothing for NuGet.
- [x] 11.7 The parsers of untrusted text (`smartctl`, `zpool`, upsd, MPI2 replies, `/proc`)
      are tested with generated input as well as fixtures. *Evidence:* SECURITY.md §4.3 rests
      on them, and only examples tested them. *Enforcer:* `daemon/src/mangle.rs` and the
      `answers_whatever_…` tests; STYLE.md §7.
- [ ] 11.8 Test coverage is measured for all three languages and reported by CI. *Evidence:*
      nothing says which lines no test reaches. Split from 11.7: it needs `cargo-llvm-cov`,
      a .NET collector that works with Microsoft.Testing.Platform, and vitest's, each a
      tool to choose.

## Phase 12: the dashboard

- [x] 12.1 A chart's data can be downloaded as CSV.
- [x] 12.2 A chart can show a chosen span, not only the six fixed ranges.
- [ ] 12.3 The page installs as an app (a web manifest and icons). *Evidence:* there is
      nothing to install, and nothing to make icons from: `web/src/lib/assets/favicon.svg` is
      still SvelteKit's placeholder logo. Needs an icon of vigil's own first.
- [ ] 12.4 The built page is tested in a browser, end to end against the stand-ins in `dev/`.
      *Evidence:* vitest covers the pure modules; no component or page is ever rendered by a
      test.

## Phase 13: what only the host can settle

Each of these restarts or reconfigures the daemon that drives the fans, so each is done with
the owner, not unattended.

- [ ] 13.1 `CapabilityBoundingSet` for vigild. *Evidence:* `deploy/vigild.service` says
      "NOT set yet": it has to be found out on the host which capabilities vigild and
      `smartctl` need.
- [ ] 13.2 The `lsi-cooling` and `drive-cage` curves, against the load history that now
      exists (README, Status).
- [ ] 13.3 Why `pwm7_enable` falls back to Disabled (README, Status). Worked around; not
      understood.

## Needs a decision before it is work

Found by the same review. Each contradicts a recorded decision or the scope above, so it is
not started until the decision is revisited.

- **Silencing, acknowledging and maintenance windows.** Each is a mutating route, and the
  first one voids ADR-023: authentication comes first.
- **Reloading vigild's config on SIGHUP.** A restart takes a second and goes through the
  whole of invariant 2 (release every channel, validate, take them again). A reload would be
  a second path through the same safety code that only a reload exercises.
- **Schema migrations with a history.** ADR-016 defers them until something needs one, and
  ADR-026 exists so that nothing does.
- **A `.deb`, a container image, an install script.** ADR-022 gives installation to the
  deployment repository.
- **Translations.** The page has one owner and one language.
- **Any channel but text and email** (a webhook, Slack, Discord, push). Decided against on
  2026-11-24.
