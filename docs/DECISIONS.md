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

**Superseded in part on 2026-11-16 (ADR-023).** The dashboard is now public, so "the network is
trusted" no longer holds and reachability from outside the LAN no longer voids anything. The
other condition stands unchanged: every route is a GET, and the first that is not stops work.

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

**Depended on again, 2026-11-11.** Two advisories with no upstream fix in the range their
dependents ask for:

- *minimatch* (moderate, ReDoS through glob-part amplification) is fixed only in 11.x, while
  eslint and typescript-eslint ask for `^10`. `web/package.json` overrides it to `^11`: both
  call only `Minimatch#match` and `minimatch()`, which 11 keeps. Drop the override when they
  move to 11 themselves.
- *deepmerge* (low, prototype poisoning) reaches the tree through
  `@sveltejs/vite-plugin-svelte`, which merges Vite config at build time. No deepmerge release
  fixes it, and none of it ships in the static build. Accepted below the gate.

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
NativeAOT is a planned follow-up, not a rejection.
*Amended 2026-11-11:* on .NET 11, an STS release supported to 2028-11-14, rather than the
.NET 10 LTS. Self-contained is what makes that safe: the runtime ships inside the build, so
the guest never has to have it, and moving to the next release is a rebuild. Its SDK also fixed
`dotnet test`'s discovery of xunit.v3 tests, which is what the tests run with again.
*Amended 2026-11-11, again:* NativeAOT now, and the unit sets `MemoryDenyWriteExecute=true`.
`package.sh` builds with `clang` where it is installed and in the .NET SDK image, pinned by
digest, anywhere else. JSON goes through a source-generated context, Npgsql through its slim
builder (base types, arrays, TLS), and the build has no trim or AOT warnings. The options are
bound by a source generator now, and that generator cannot set an `init` property: it skipped
every one without a word, and the first native build started on all defaults. `VigilOptions`
is therefore settable, and its tests bind through vigil-core's own `VigilEnvironment.Read`, so
they run the generated binder. Debugging the native binary needs `vigil-core.dbg`, which
`package.sh` leaves out of the package; publish again to get it.

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

*Decided 2026-10-17*, after a pool sat DEGRADED for days while the dashboard said Live. SMART
judges a disk by its own self-assessment. ZFS judges it by whether its reads succeed, and it
faulted a disk whose SMART still said PASSED. vigil read only SMART, so it never saw the fault.

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
*Added since:* `/system` (ADR-025) and `/insights`, both prerendered section pages. `/insights`
is the one page that does not read the shared history: it asks for a week or a month of its
own, so looking at a trend does not change the range every other page is showing.

### ADR-021: Notifications go out through channels, each with its own minimum severity

**Amended on 2026-11-24 (ADR-028).** ntfy is gone; the channels are texts and email, and a
channel now retries. The router, the minimum severities and the all-clears stand.

*Decided 2026-11-11:* push the all-clear of anything that was pushed. ntfy was the only way
vigil reached a person, called straight from the runtime. An SMS gateway on a cellular modem
is to be the second, for when the LAN or the
internet is the thing that is down, and a text for every warning would be noise.

So delivery is `INotifier` channels behind one `AlertRouter`. Each channel has a minimum
severity (ntfy: warning). The router remembers which channels it told about each kind, and
sends the all-clear (an info event of that kind) to exactly those channels, once. A channel
never throws and never blocks the runtime; it logs its own failures. What it remembers is in
memory, so a restart forgets: a condition still holding is raised and pushed again on start,
and one that cleared while vigil-core was down gets no all-clear.

*Consequence:* a new channel is a class and one registration in `VigilServices.cs`, with its
options validated like `NTFY_URL`. Its address is a secret in the way a private ntfy topic is:
a phone number, gateway URL or token is never logged and never put in an error or a response
(SECURITY.md). A rule's severity now decides who gets woken, not only what colour it is.

### ADR-022: A tag on master is a deploy

**Amended on 2026-11-24.** A tag no longer runs the checks. `release.yml` builds, attests,
publishes and hands off, and nothing else: `check.yml` runs on every push to master, so the
commit a tag names has its result already, and the owner decides at the moment of tagging
whether to release on it. *Consequence:* a tag on a commit whose checks failed, or have not
finished, is released all the same. The tag must still be on master, and the deployment's own
health check and rollback still stand between a bad build and the host.

*Decided 2026-11-11.* Releases were built by hand on a build guest and installed by a
runbook, so a fix reached the NAS whenever someone found an evening for it.

Now pushing a tag `vMAJOR.MINOR.PATCH` (semver, since 2026-11-16; dated `vYYYY.MM.DD` tags before
that) on a commit master contains runs
`.github/workflows/release.yml`: every check in `check.yml`, then vigild and `vigil-core.tar.gz`
built from the tagged tree, attested with GitHub's build provenance and published as the
release. When the `DEPLOY_REPOSITORY` secret names one, it dispatches `vigil-release` with the
tag to that repository, which owns what the host runs: its workflow verifies the attestations,
pins the two sha256s, commits, and asks the host to apply that commit. Without it, a tag only
publishes. The deployment is not described here: it is the deployment's, and this repository
is public.

The chain's trust is arranged so no single secret is worth much:

- `HOSTS_DISPATCH_TOKEN`, here, can only start the deployment repository's workflows.
- The deployment's credentials should reach the host only as a user whose login shell runs one
  command, which installs only what the deployment repository pins at a commit, after
  checking each sha256. A leaked key can then re-deploy a release that was already verified,
  nothing else.
- A new vigild is checked against the live config before it replaces the old one, and both
  components are health-checked after the restart and rolled back if that fails.

*Rejected:* vigil as a submodule of the deployment, bumped by each release. A submodule records
source, but the host runs built artifacts, and the deployment's pin already records exactly
which. A second record of "the version" could only disagree with the first. *Rejected:* CI with
root SSH to the host, simpler but a root shell on the NAS in GitHub's secret store.

### ADR-023: The dashboard is public, read-only, and treated as published

*Decided 2026-11-16.* The owner wants the dashboard reachable from the internet with no login.
ADR-008 accepted no authentication because the LAN was trusted; that reason is gone, so what
replaces it is stated here rather than left implied.

Three things make a public page acceptable, and each is a gate:

1. **Nothing it serves changes anything.** Unchanged from ADR-008, and now the only thing
   between a stranger and the host: every route is a GET (`ApiTests.EveryRouteIsAGet`). A
   mutating route, or a GET that acts, needs authentication first. Stop and raise it.
2. **Everything it serves is published.** There is no viewer to trust, so a value is either fit
   for anyone or it is not served. Credentials were already masked; drive serials are now cut
   to their last four characters, in the SMART field and in the by-id name of a vdev alike, and
   a UPS's serial, MAC, contact, location and device address are masked, all where they enter
   the process (`SerialNumbers`, `NutProtocol`). What is withheld reads `[REDACTED]`. A new field
   gets the question "would I post this?" before it gets a wire name.
3. **No one client can use up what everyone shares.** A request has no identity but its
   address, so that is what is limited: requests per client, live streams per client, on top
   of the global caps (`Admission`, `LiveHub`).

vigil-core still serves plain HTTP and still has one listener. TLS belongs to whatever is in
front of it, and vigil-core has to be told what that is (`TRUSTED_PROXIES`) before it believes
a forwarded address; `ALLOWED_HOSTS` names the host it answers to.

*Consequence:* the host's temperatures, drive health, pool state, UPS state and event log are
public, by decision. That includes whether the host has mains power. A flood from many
addresses is not vigil-core's to stop: the per-client limits make one machine harmless, and
anything larger is the front proxy's or the CDN's job. Authentication remains the planned
answer to anything that needs to be private or to act.
*Rejected:* a login. It would protect the data, but the owner wants the page public, and a
login nobody uses protects nothing. *Rejected:* TLS in Kestrel. Certificates need renewing,
and the proxy or tunnel that makes the host reachable already terminates TLS.
*Rejected:* a setting to publish full serials. The tail identifies a disk on its label, which
is the serial's whole job on the page; the full one is a `smartctl` away for whoever owns the host.

### ADR-024: `TRUSTED_PROXIES=cloudflare` follows Cloudflare's published ranges

*Decided 2026-11-16.* The dashboard sits behind Cloudflare. Its edge connects from about twenty
networks that Cloudflare changes without telling its customers' env files, and those networks
are whose `X-Forwarded-For` vigil-core has to believe to tell one visitor from another
(ADR-023). Typed into `TRUSTED_PROXIES` by hand, the list would be right until the day it was
not, and the symptom, every visitor behind a new range sharing one client's limits, would not
point at its cause.

So `TRUSTED_PROXIES` takes addresses, networks, or the word `cloudflare`, in any mix. The word
stands for Cloudflare's two published lists, which vigil-core fetches at start and daily. A
list that cannot be fetched, or is not plausibly Cloudflare's (not networks, too many, too
wide), changes nothing; until the first fetch the ranges compiled in are used, so starting
never waits on a third party. Nothing else is Cloudflare-specific: it appends the visitor to
`X-Forwarded-For` as any proxy does and sets `X-Forwarded-Proto`, so the same code serves nginx,
Caddy or a tunnel named by address.

*Consequence:* vigil-core makes one more outbound HTTPS request, to `www.cloudflare.com`, and
only when the word is used. What it fetches decides whose header is believed: someone who could
forge that list (which needs Cloudflare's certificate) could lie about a visitor's address and
so slip the per-client limits, and nothing more. The global caps do not depend on it.
*Not done:* Cloudflare's edge certificate needs nothing here. It is the certificate Cloudflare
shows visitors; vigil-core never sees it, and only needs to know a request arrived over HTTPS,
which `X-Forwarded-Proto` says. Encrypting the hop from Cloudflare to this host is a separate
choice (a tunnel, or an origin certificate and TLS in Kestrel, which ADR-023 leaves out).
*Rejected:* reading `CF-Connecting-IP`. It carries the same address as the last
`X-Forwarded-For` hop, and using it would be a second code path that only one vendor exercises.

### ADR-025: Host load comes from vigild as counters, and vigil-core turns them into rates

*Decided 2026-11-19.* The owner wants RAM, CPU, disk and network use recorded and charted
alongside the temperatures. vigil-core cannot read these itself, because it runs in an
unprivileged LXC whose `/proc` describes the container, not the host. So vigild reads them,
for the same reason it reads pool state (ADR-018): reading the host is its job, and it is the
process running on the host.

All of it comes from `/proc`: `meminfo`, `stat`, `loadavg`, `pressure/*`, `diskstats`,
`net/dev`, `uptime` and `spl/kstat/zfs/arcstats`. There is no subprocess, no new crate and no
new privilege, and nothing in it can wake a sleeping disk. A rate needs two readings, and
vigild keeps no history (invariant 4). So vigild publishes what the kernel counts, which is
cumulative bytes, ticks and stall microseconds, and vigil-core derives the rate from
consecutive snapshots. vigil-core already holds the previous snapshot in its runtime loop. A
rate is the difference over the time that actually passed, so a lost snapshot widens one
interval and does not distort it. A counter that goes backwards (after a reboot) produces no
rate for that interval, never a zero or a negative one: an unknown reading is unknown, the
same rule as invariant 3.

What is published (ADR-023): RAM, swap and ARC, CPU busy and iowait, load averages, pressure,
uptime, per-disk throughput and busy time keyed by the disk's stable name, and throughput for
the interfaces listed in `[host] interfaces`. That list is empty by default. It is an
allow-list because on a Proxmox host the interface names (`tap100i0`, `veth105i0`) give away
the guest IDs. No addresses, hostnames, mount points or per-process figures are published.

*Consequence:* the public page shows when the host is busy and when its network is in use, and
so roughly when its users are active, as UPS state already shows mains power. The owner accepted
this on 2026-11-19. Each snapshot grows by a few hundred bytes. vigil-core writes one host row
and one traffic row per disk, pool and interface every persist interval (10 s), each the average
since the row before, plus pool capacity once per `[zfs]` poll.
*Rejected:* vigild computing the rates from its own previous poll. That would be history in
vigild, however short, and every consumer would get rates over vigild's interval rather than
its own. *Rejected:* node_exporter or Proxmox's own metrics. They would be a second agent on
the host and a second source of truth, and vigil would still have to store and chart the data.

### ADR-026: A deployed continuous aggregate is never redefined; a later metric is a new series

*Decided 2026-11-19.* `SchemaSetup` creates a continuous aggregate only when it is missing, so an
edited definition would never reach a database that already has the view. Making it reach one
means dropping and rebuilding the view, and a rebuild reads only the raw rows still there: raw
history is kept 30 days, and the minute and hour rollups are the only copy of everything older.
A rebuild would silently delete up to a year of minutes and every hour since vigil began.

So the definitions are frozen once released. A metric that arrives later goes into a raw table of
its own with its own `_1m` and `_1h` (`ups_detail_samples`: battery temperature, output frequency
and current, and the number of monitors, which came after `ups_1m` shipped). Its query is read
beside the old one and merged into the same response, so the page cannot tell. This is also why
every rollup keeps maxima and minima no chart reads yet: a column cannot be added later.

*Enforcer:* `SchemaSetupTests.NoDeployedAggregateIsRedefined` pins a hash of every definition.
A hash may change only for a series no release has shipped, in the same commit.
*Rejected:* a hash of each definition stored in the database, with a rebuild when it differs.
It turns an edit into data loss on the next start, which is what this rule exists to prevent.
*Rejected:* adding columns to `ups_samples` and a second pair of aggregates over it. `Series`
names its aggregates after its raw table, and a table of their own costs nothing.

### ADR-027: Texts go through a gateway that vigil defines the contract for

*Decided 2026-11-11; built on 2026-11-19.* A cellular modem sits on the host and vigil-core
runs in a guest, so the two talk over HTTP, and vigil-core never drives the modem
(invariant 1). vigil writes the contract and the gateway implements it: `POST <SMS_URL>` with
`Authorization: Bearer <SMS_TOKEN>` and `{ "message": "…" }`, answered 2xx for accepted. It is in
the README beside the API, for whoever writes the gateway.

The gateway holds the phone numbers, so none is in vigil's config or its log. Texts carry
warnings and worse, with the all-clear for each, as ntfy does. The token is a secret like
`DATABASE_URL`, and the URL is treated as one (SECURITY.md §7).

*Consequence:* the channel can be built, tested and watched (`dev/mock-sms.mjs`) before the
gateway exists, and is simply not configured until it does.
*Rejected:* vigil-core sending through the modem with `mmcli`. It would put hardware control
in vigil-core and the modem in its guest. *Rejected:* phone numbers in vigil's config. One
list, in the gateway, is one place to change and one place they can leak from.

### ADR-028: Vigil reaches a person by text or by email, and nothing else

*Decided 2026-11-24* by the owner: ntfy is removed, and no push service, webhook or chat
integration takes its place. A text reaches a phone with no app and no account, and works when
the internet is what is down (ADR-027). An email reaches everything else. Each further channel
is one more address to keep secret and one more thing to find broken on the night it matters.

`NtfyClient` and `NTFY_URL` are deleted; a `NTFY_URL` left in an environment file is ignored.
Email is `EmailClient`, over the platform's `SmtpClient` (ADR-015): `SMTP_HOST`, `EMAIL_FROM`
and `EMAIL_TO`, with a login that is only ever sent after STARTTLS. It carries warnings and
worse with their all-clears, as texts do.

A channel is now an `IDelivery`, one attempt that reports why it failed, behind a
`RetryingNotifier` that queues, keeps order and tries again after 30 seconds, 2 minutes and
10 minutes. In order, so "resolved" never arrives before its problem. Both the retries and the
queue (64) are bounded.

*Consequence:* a deployment that used ntfy has no channel at all until `SMS_*` or `SMTP_*` is
set, and vigil-core does not warn about that: running with none is allowed. A notification
can arrive up to a quarter of an hour late, and one behind it later still. What is queued is
lost on a restart, as the router's memory is (ADR-021).
*Rejected:* a generic webhook, as the one channel that reaches everything. It is the same
decision as ntfy with a longer list of things on the other end. *Rejected:* MailKit. It is the
better SMTP library, and a dependency for one message a month to one server.
*Rejected:* retrying inside each channel. Two channels would each grow a loop, and neither
would keep its notifications in order.

### ADR-029: A UPS may be read by a second name when the first is lost

*Decided 2026-11-24.* For a month the UPS reading dropped out for hours at a time, 14 % of it in
all: the serial adapter its driver reads through kept falling off the USB bus, upsd answered
`DATA-STALE`, and vigil could not have seen a power cut. The same UPS was attached by its own
USB cable too, as a second entry in `ups.conf`, readable throughout.

`NUT_UPS_FALLBACK` names that second entry. When upsd answers an error for `NUT_UPS`, the same
connection asks for the fallback, and a reading from it is used under the first name, so
history and the page stay one series. Monitors are still counted on the first name, which is
the one upsmon logs in to. While the fallback is in use a warning (`ups-on-fallback`) says so
and why: the power state is known, the usual way of knowing it is not.

*Consequence:* what only the first driver reports is missing while the fallback answers, and
the two drivers may disagree by a little on what both report. A name goes into upsd's protocol
as written, so both are held to the characters a `ups.conf` section may have.
*Rejected:* averaging or preferring whichever is fresher. One is the configured source and
the other is what stands in for it; a reading has to be attributable to one of them.
*Rejected:* fixing only the host. The adapter was fixed too, but a monitor that goes blind
when one cable does was the fault in vigil.
