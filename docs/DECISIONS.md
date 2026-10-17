# Decisions

Short records of choices that are not obvious from the code, so they are not "simplified" away
later. Append; do not rewrite. A superseded decision stays, marked.

Format: context, decision, consequence. Short on purpose.

---

### ADR-001 — SSE, not WebSocket, for every stream

Data flows one way: daemon to consumer, core to browser. SSE is plain HTTP, any number of
consumers can subscribe at once, and clients reconnect on their own. A WebSocket would add a
handshake, a framing layer and a dependency for no gain.
*Consequence:* keepalive comments are how a dead consumer is noticed, so every stream needs a
keepalive interval and every reader a silence timeout greater than twice it.

### ADR-002 — vigild's API is a unix socket, never TCP

A root daemon with raw PWM and `ioctl` access must not be reachable from the network. A unix
socket cannot be, and access is filesystem permissions (mode, uid, gid from config). A
containerised consumer gets the socket's *directory* bind-mounted in, because the socket file
itself is recreated on every start.
*Consequence:* `daemon/` contains no TCP code path to misconfigure. The directory comes from
`tmpfiles.d`, not the unit's `RuntimeDirectory=`, which would get a new inode per restart and
silently break the bind mount.

### ADR-003 — No TLS stack in Rust; ntfy goes through `curl`

**Narrowed by ADR-016 on 2026-10-01: binds vigild only.** vigil-core is .NET now, where HTTPS
is part of the platform, and ntfy goes through `HttpClient`; `curl` is no longer a dependency.

HTTPS in Rust needs a TLS library with C in it, which would end the static musl build from a
machine with no cross toolchain. ntfy is the only outbound HTTPS need.
*Consequence:* `curl` is a runtime dependency of notifications, and each send runs on its own
thread with a hard time limit so a missing or slow `curl` cannot stall anything.

### ADR-004 — Plain threads and channels; no async runtime of ours

**Superseded by ADR-015 on 2026-10-01.** Kept because the parts about state ownership still hold.

vigild and vigil-core are threads, channels and blocking I/O. `postgres`'s internal tokio
runtime stays internal and must not leak into our code.
*Consequence:* no `async fn` in our crates. State lives behind one owner thread rather than
locks, which also fixes event ordering.

### ADR-005 — Static musl binaries, developed on Windows

The musl target ships its own C runtime objects, so Rust's bundled LLD links a fully static
Linux binary from Windows with no cross toolchain.
*Consequence:* tests must pass on any OS and must never need hardware, root or the network.
Linux-only code is `#[cfg(target_os = "linux")]` with a no-op fallback.

### ADR-006 — `panic = "unwind"` in release

An abort would skip `FanSafetyGuard`'s `Drop`, leaving every fan header stuck on manual PWM at
its last duty — the exact failure the guard exists to prevent.
*Consequence:* slightly larger binary. Not negotiable.

### ADR-007 — TimescaleDB owns rollups, compression and retention

vigil-core inserts raw samples and nothing else. Continuous aggregates build `*_1m` and `*_1h`;
background jobs compress and drop. The database does on its own schedule what would otherwise
be a scheduler in vigil-core.
*Consequence:* the extension must be created by a superuser before vigil-core can start, so it
checks and retries. A refresh over a window whose source rows retention has already dropped
**deletes** the rollups for that window — retention and refresh windows are a correctness pair.

### ADR-008 — vigil-web is LAN-open; authentication is a future feature

**Amended on 2026-10-01 (ADR-017).** vigil-web's server merged into vigil-core, so the
LAN-open process is now vigil-core, on one listener with no loopback-only second one. The
condition that makes it acceptable is unchanged and now has an enforcer: every route is a GET
(`ApiTests.EveryRouteIsAGet`). The first route that is not voids this decision.

*Decided 2026-10-01.* vigil-web binds `0.0.0.0:3000` with no authentication. The network is
trusted, and vigil-web writes nothing: no mutating route, no database write grant. Adding auth
or a reverse proxy was judged not worth the setup cost now.
*Consequence:* anything on the LAN can read host state and the event log. The decision is void —
stop and revisit — if vigil-web gains a mutating route, if vigil-core's planned action
endpoints land, or if the host becomes reachable from outside the LAN. vigil-core stays
loopback-only regardless. Tracked as a planned feature, not a bug.

### ADR-009 — A drive has two keys: its WWN and its port

The WWN is burned into the disk and follows it between bays: right for health and history. The
by-path port stays with the bay whatever disk is in it: right for cooling. Two questions, two
keys. `bay_occupants` records which disk sat in which bay and when.
*Consequence:* a drive's temperature is stored twice, as `drive:<wwn>` and `port:<by-path>`. A
swapped disk lands in the right cooling zone with no config change.

### ADR-010 — The tiny HTTP server stays hand-rolled

**Superseded by ADR-015 on 2026-10-01.** The reasoning was wrong: it weighed a hand-rolled
server against a hypothetical cost without checking what the ecosystem actually offers, and
without noticing that vigil-core already had tokio in its tree.

Both daemons serve three read-only routes over ~200 lines, already tested against a mock
stream. `axum` would bring an async runtime (ADR-004) and a large dependency tree to serve
`GET /status`.

### ADR-011 — Tailwind v4 plus shadcn-svelte, chrome only

*Decided 2026-10-01.* vigil-web moves to Tailwind CSS v4 and takes shadcn-svelte for generic
chrome (Card, Button, Badge, Table, Tooltip, Select, Separator, Switch). The bespoke visuals
stay hand-written: `LineChart` (gap detection, crosshair readout, data-table fallback),
`Sparkline`, the `StatTile` meter.
*Consequence:* shadcn's chart primitives are not adopted, so that behaviour is kept.
`$lib/components/ui/**` is vendored by the CLI: never hand-edited, upgraded through the CLI,
extended by wrapping. Existing role tokens map onto shadcn's names so its components inherit
the palette.

### ADR-012 — `scripts/check.sh`, not `just` or `make`

One command must run every check, and CI must run exactly that command so the two cannot
diverge. `just` is not installed here, and the project is also developed on Windows, where
neither `just` nor `make` is present but bash is (Git Bash, WSL).
*Consequence:* the script is POSIX-ish bash and skips, with a message, any tool that is not
installed — so it is useful locally and strict in CI (`CHECK_STRICT=1`).

### ADR-013 — The wire contract is enforced by a test, not a comment

`protocol/src/lib.rs` and `web/src/lib/types.ts` are the same contract in two languages. The
comment asking for both to be changed together is not an enforcer.
*Consequence:* `protocol/contract.json` is generated by a Rust test and read by a web test. A
one-sided rename fails CI. Adding a field means regenerating the contract in the same commit
(`cargo test -p vigil-protocol` writes it).

### ADR-014 — `npm audit` gates at moderate, not low

**Resolved on 2026-10-17.** SvelteKit 3 depends on `cookie ^2`, and `npm audit` reports no
vulnerabilities at any level. The gate stays at moderate: that threshold was the reason for
this record, but nothing now depends on it.

`cookie@0.6.0` reaches the tree through `@sveltejs/kit@2.70.3`, which is the latest 2.x
release; there is no upstream fix, and the only version `npm audit fix --force` offers is a
prerelease major of `adapter-node`. The advisory (out-of-bounds characters accepted in a cookie
name, path or domain) needs the application to set cookies from outside data. **vigil-web sets
no cookies at all** — no `cookies.set`, no `Set-Cookie`, no session — so it is not reachable.
*Consequence:* `scripts/check.sh` runs `npm audit --audit-level=moderate`. Low-severity
advisories are reported by `npm audit` on demand but do not fail the build. Anything moderate
or above fails. Revisit when kit ships a release with `cookie >= 0.7`.

### ADR-015 — Established libraries over our own, measured by maintenance

**Scope narrowed on 2026-10-01.** The Rust half now describes vigild only. vigil-core's
replacements are the .NET platform's own: ASP.NET Core for HTTP and SSE (writing and, via
`SseParser`, reading), `ILogger` with systemd's journal format, `HttpClient` for ntfy, Npgsql.
`rups` is still rejected, so NUT stays hand-rolled in C#. Node's `EventSource` no longer matters:
nothing server-side reads a stream in Node any more.

*Decided 2026-10-01, superseding ADR-004 and ADR-010.* vigil uses a well-maintained library
wherever one exists, and "well maintained" means a recent release from a stable org, judged on
actual registry data rather than reputation.

Checked on 2026-10-01:

| Taken | Why | Replaces |
|---|---|---|
| `tokio`, `hyper`, `axum` | tokio-rs and hyperium; 239M / 216M / 124M recent downloads | both hand-rolled HTTP servers and the `httpd/` crate that briefly shared them |
| `tracing`, `tracing-subscriber`, `tracing-journald` | tokio-rs | the hand-rolled logger, including its sd-daemon priority prefix |
| `time` | time-rs, 188M recent | `rfc3339_from_millis`, which was Howard Hinnant's calendar algorithm written out by hand |
| `sd-notify` | 3.0M recent | the sd_notify datagram, written by hand |
| `Intl.RelativeTimeFormat` | the platform | `relativeTime()`'s hand-written wording and plurals |

Rejected, with the bespoke code kept:

| Rejected | Why |
|---|---|
| `tiny_http` | the only blocking HTTP server in Rust, and no release since 2022-10 |
| `rups` | a NUT client, but 1,100 recent downloads and last released 2023 |
| `libmedium` | hwmon, 177k recent — and this is the safety-critical sysfs path, whose in-memory fake is what makes fan control testable without hardware |
| an SSE *client* crate | nothing maintained by a stable org exists; `eventsource-client` is 1.4M recent. The Rust parser stays, with its nine tests |
| Node's native `EventSource` | still behind `--experimental-eventsource` in Node 26, and vigil-web's stream to vigil-core is long-lived and load-bearing. `web/src/lib/server/sse.ts` stays; revisit when it ships unflagged |

No library exists for `mpt3.rs` (reimplemented from the kernel's own source), and fan curves,
hysteresis, zones and SMART parsing are domain logic, not wheels.

*Consequence:* `async` is now allowed, and both binaries run a tokio runtime for their API
while the control loop and the state thread stay synchronous and own their state as before —
the half of ADR-004 that was about ownership still stands. The static musl build still has to
work and there is still **no TLS stack** (ADR-003), which `cargo-deny` enforces.

### ADR-016: vigil-core is .NET, self-contained, not NativeAOT

*Decided 2026-10-01.* The Rust vigil-core and vigil-web's Node server are replaced by one
ASP.NET Core process (docs/CLEANUP.md phase 6). vigild stays Rust: it runs as root and drives
the fans, and keeping it small is a safety property there, not a preference.
vigil-core ships as a self-contained single-file `linux-x64` build (`core/deploy/package.sh`),
for the Debian guest it runs in, where the systemd unit applies as written:
the host needs no .NET runtime. NativeAOT was considered and not taken: it needs `clang`, which
is not installed where vigil is built, and ASP.NET's reflection paths are not all AOT-safe.
*Consequence:* the JIT writes the code it runs, so vigil-core's unit cannot set
`MemoryDenyWriteExecute`, and says so. The schema is re-applied idempotently on every database
(re)connect, ported from the Rust core; EF Core migrations, which the phase 6 plan named, are
deferred until something needs a migration history. ADR-003 and ADR-004 bind vigild only.
NativeAOT is a planned follow-up, not a rejection: docs/CLEANUP.md tracks what it needs.

### ADR-017: One listener on the LAN; the contract described by each producer

*Decided 2026-10-01.* With vigil-web merged in, vigil-core serves the dashboard, so it listens
on the LAN (`CORE_HOST` defaults to `0.0.0.0`; production uses port 3000 so bookmarks survive).
There is no second, loopback-only listener for future actions: actions are not planned to be
unauthenticated, so the second listener would only defer the authentication question. The
`CORE_ALLOW_NON_LOOPBACK` opt-in is gone; `CORE_HOST` must still be an IP address, because
Kestrel binds every interface for any other name.
The wire contract is split along the same lines as the processes: `daemon/contract.json` is
generated by vigild's tests, `core/contract.json` by vigil-core's, and the web checks its types
against both. vigil-core also checks its client types against the daemon file, and that its
50s silence timeout outlasts two of vigild's published keepalives.
*Consequence:* ADR-013's enforcement now runs in all three languages without one language
owning types it never sends. A new route that is not a GET fails `ApiTests.EveryRouteIsAGet`.

### ADR-018: ZFS pool state comes from vigild, and zed keeps its own push

*Decided 2026-10-17*, after `docs/incidents/2026-10-17-void-disk-fault.md`. SMART judges a disk
by its own self-assessment. ZFS judges it by whether its reads succeed, and it faulted a disk
whose SMART still said PASSED. vigil read only SMART, so the pool sat DEGRADED for nine days
while the dashboard said Live.

vigild reads pool state, because reading the host's hardware is its job and it is the one on
the host. vigil-core's unprivileged LXC has no `/dev/zfs`. vigild runs `zpool status -j` and
`zpool list -j`, which is OpenZFS's own JSON. It does not link libzfs, which would put a C
library's ABI inside the root daemon and end the static musl build (ADR-005).

vigil only reads pool state. It never clears, detaches, onlines or scrubs: those change the
hardware (invariant 1), and the dashboard has no authentication (ADR-008). Acting on a pool
stays a shell on the host.

zed posts to ntfy as well, through its own `ZED_NTFY_*` settings, so a fault reaches a phone
even when vigil and mail are both down. That night, the mail was the only push, and it was
broken.

*Consequence:* `zpool` joins `smartctl` and `nvidia-smi` as a vigild subprocess, under the same
rules (SECURITY.md §5). vigil's delay on a pool change is its `[zfs]` poll interval. One fault
can produce two notifications, zed's and vigil's, and that is accepted.

### ADR-019: The page's verdict is the worst raised condition, judged once in vigil-core

*Decided 2026-10-17.* The header said Live while a pool was DEGRADED. It judged only the
connections and `controlLoopHealthy`, so a stalled fan, a UPS on battery or a failed drive did
not change it either. From now on a green header means nothing is raised.

vigil-core's `Alerting` already decides what is wrong. The page receives the raised conditions
over the live stream and does not work them out again in TypeScript (STYLE.md §1.1). That way
the page and ntfy cannot disagree about what is wrong.

*Consequence:* a new condition shows on the page without any web change. Anything that should
hold the verdict for as long as it lasts has to be a condition, not a one-time event: a drive
failing SMART is therefore both. A status rule written only in the web is a review flag.

### ADR-020: The dashboard is several pages, and a disk has its own

*Decided 2026-10-17.* One page had outgrown itself: tiles, two charts, the UPS, fans, events and
a thirteen-row drive table, with ZFS still to come. It is now an overview at `/` with a page per
section (`/drives`, `/storage`, `/cooling`, `/power`, `/events`), and a page per disk at
`/drives/<name>`, where its SMART changes, temperature and ZFS membership are joined on the
name vigild gives it.

The section pages are prerendered like the overview was, and vigil-core serves `/drives` from
`drives.html`. A disk's name is only known at run time, so its page cannot be: it is the
build's fallback page (`spa.html`), rendered in the browser. vigil-core serves that file for
`/drives/{name}` only, through one mapped GET, rather than for every unknown path, because a
catch-all would answer anything with a 200 and hide a broken link as an empty page.

The root layout opens the one connection to `/api/live` and fetches the history; every page
reads that shared state, so moving between pages drops neither. `/api/drives/{name}/history`
is the one new route: a GET, bound to 200 rows, the name bound as a parameter. ADR-008 holds.

*Consequence:* a new page that needs a run-time name needs its own mapped route in
`Dashboard.MapDashboardPages` and a line here; nothing falls through to the fallback by itself.
