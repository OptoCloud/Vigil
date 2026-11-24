# Security guide

vigil runs as root on a host whose fans it controls, and it reads a database that holds every
temperature, drive and power reading of that host. The dashboard is public (ADR-023): anyone on
the internet can send vigil-core a request, so every request is hostile until shown otherwise
and everything served is published. Behind it nothing changed: vigild, the database and the UPS
are reachable only from the host and its LAN, and the rules for them are still about
**containment**, because the blast radius of the root daemon is not modest.

Every rule names an **Enforcer**.

## 1. Trust boundaries

```
             ┌─ root, host ──────────────┐
  sysfs PWM  │                           │
  /dev/mpt3ctl ─> vigild                 │   no network listener at all
  smartctl   │      │                    │
  zpool      │      │                    │
  nvidia-smi │      └─ unix socket ───────────┐  0660, uid/gid from config
             └───────────────────────────┘    │
                                              v
             ┌─ unprivileged LXC ─────────────────────────────┐
             │  vigil-core ──TCP──> upsd (anonymous reads)    │
             │     ├──> Postgres: owner role writes,          │
             │     │    SELECT-only role reads                │
             │     ├──> SMS gateway (HTTP), mail server (SMTP)│
             │     ├──> Cloudflare's IP lists (HTTPS, out)    │
             │     └── :3000, plain HTTP, NO AUTH, GET only ──┼──> TLS proxy ──> internet
             └────────────────────────────────────────────────┘
```

| Boundary | Who may cross | Authentication |
|---|---|---|
| sysfs PWM, `/dev/mpt3ctl` | vigild only, as root | filesystem (root-owned, mode 644) |
| vigild's status socket | any local consumer with group access | unix permissions: mode, uid, gid from `vigild.toml` |
| upsd | vigil-core | none needed: NUT reads are anonymous |
| Postgres | vigil-core | two roles, each with a password in the env file (§3.4) |
| vigil-core HTTP: the dashboard and `/api/*` | **anyone** | **none: see §2** |

### 1.1 Every listener states its bind address and who authenticates it

A new listener is not added without a row in the table above. If the answer to "who
authenticates this" is "nothing", then nothing it serves may change anything (§2).

vigil-core has exactly one listener (ADR-017), now public (ADR-023). `CORE_HOST` must be an IP address: Kestrel binds
every interface for any other name, so a typo would not fail, it would listen somewhere other
than intended.

*Enforcer:* review checklist; `VigilOptionsValidator` refuses a `CORE_HOST` that is not an
address, and `ListenAddressTests` pins it.

## 2. The dashboard is public and unauthenticated

**Decided, not overlooked** (ADR-023, which replaces ADR-008's "the LAN is trusted"). vigil-core
serves the dashboard and its API to anyone, with no authentication. Three rules make that
acceptable, and breaking any of them is a hard gate:

1. **Every route is a `GET` that changes nothing.** A route with any other method, or a `GET`
   that writes, acts or mutates, needs authentication first: stop and raise it.
2. **Everything served is published.** A value is fit for a stranger to read or it is masked
   before it is held (§7). Reviewing a new wire field includes asking whether it would be
   posted in public.
3. **Limits are per client as well as global** (§4.2), so one machine cannot take what every
   visitor shares.

What this does not give: privacy for anything on the page, or protection from a distributed
flood. Authentication is still the planned answer to the first and to any action.

### 2.1 The deployment's half

vigil-core cannot enforce these, so they are checked when it is deployed:

- **TLS is terminated in front.** vigil-core speaks plain HTTP on one listener; it is never
  port-forwarded as it is. A reverse proxy or tunnel carries HTTPS, and `CORE_HOST` binds the
  address that proxy reaches (loopback if they share a host).
- **`TRUSTED_PROXIES` names that proxy**, by address, by network, or as `cloudflare` for the
  ranges Cloudflare publishes, which vigil-core then keeps current (ADR-024). Unset, every
  visitor arrives from the proxy's address and shares one client's limits; vigil-core warns
  once when it sees this happening. Only the one hop a named proxy added is believed, so a
  client cannot choose its own address. A network of `/0` is refused: it believes anyone.
- **Only the proxy can reach the port.** vigil-core answers whoever connects, so a visitor who
  finds the host's own address skips the proxy and everything it does. Behind Cloudflare that
  means a tunnel, or a firewall rule admitting Cloudflare's ranges alone.
- **`ALLOWED_HOSTS` names the public host name.** Anything else gets a 400, which is what stops
  a hostile page reaching the dashboard under a name of its own (DNS rebinding) and scanners
  that arrive by address. A health check has to ask by a name on the list.

*Enforcer:* `ApiTests.EveryRouteIsAGet`; `AdmissionTests`; `VigilOptionsValidator` refuses a
`TRUSTED_PROXIES` that is not addresses. A `GET` that mutates and a field that should not be
public are the review checklist's job, and blocking questions, not style notes.

## 3. Privilege

### 3.1 vigild is the only component that changes anything

vigil-core reads hardware state and writes only history. vigild writes to sysfs and issues one
`ioctl`. This asymmetry is the main containment property of the design and nothing may erode
it: a feature that needs to change hardware belongs in vigild, behind its config validation,
not in vigil-core.

### 3.2 A root daemon does not listen on a port

vigild's API is a unix socket, reachable only by a local process with group access, and a
containerised consumer gets the socket's *directory* bind-mounted in. There is no TCP code path
to misconfigure.

*Enforcer:* `daemon/` has no `TcpListener`; `scripts/check.sh` greps for one.

### 3.3 Every unit takes the hardening baseline, or says why not

The baseline, strongest first:

```ini
NoNewPrivileges=true
ProtectSystem=strict         # "full" only where something genuinely needs a writable path
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
ProtectClock=true
ProtectHostname=true
RestrictAddressFamilies=…    # only the families that component actually uses
RestrictNamespaces=true
RestrictSUIDSGID=true
RestrictRealtime=true
SystemCallArchitectures=native
LockPersonality=true
MemoryDenyWriteExecute=true
CapabilityBoundingSet=       # empty for anything that does not run as root
```

**A directive may be omitted only with a comment in the unit saying why**, in the form
`# <Directive>: NOT set, <reason>`. `scripts/check.sh` accepts the comment and fails on a
silent omission, because several of these break a component outright and the next person needs
to know that before "fixing" the unit:

| Omitted | Where | What it would break |
|---|---|---|
| `ProtectKernelTunables` | `vigild` | Mounts `/sys` read-only. vigild's whole job is writing `/sys/class/hwmon/*/pwmN`; with it, taking a channel to manual fails at startup and **no fan is ever driven**. |
| `PrivateDevices` | `vigild` | Hides `/dev/mpt3ctl` (the HBA temperature ioctl) and the nvidia device nodes. |
| `MemoryDenyWriteExecute` | `vigild` | Applies to `smartctl` and `nvidia-smi` too: vendor binaries whose memory behaviour is not ours to promise. |
| `CapabilityBoundingSet` | `vigild` | Runs as root and needs `CAP_CHOWN` for the socket's uid/gid; `smartctl` may need `CAP_SYS_RAWIO` for `SG_IO` on some kernels. Narrowing it blind risks losing fan control or drive health, so it must be established on the target host. |

`RestrictAddressFamilies` is worth setting precisely rather than broadly. vigild gets
`AF_UNIX` alone: its API is a unix socket and there is deliberately no TCP code path in it, so
a future change that opened a port would fail at the sandbox as well as in review.

Check a change with `systemd-analyze verify`, which catches a misspelled directive or an
invalid value without deploying anything.

### 3.4 Database roles match what each path does

vigil-core and the dashboard are one process, so this boundary is no longer between processes:
one address space holds both credentials. What remains worth having is that the read path (the
history and event log queries the browser drives) uses a role that cannot write, so a bug in a
query cannot write, drop or truncate anything. `DATABASE_URL` is the schema owner;
`DATABASE_URL_READONLY` is a role granted `SELECT` only:

```sql
create role vigil_web login password '…';
grant connect on database vigil to vigil_web;
grant usage on schema public to vigil_web;
grant select on all tables in schema public to vigil_web;
alter default privileges in schema public grant select on tables to vigil_web;
```

The role keeps its old name so an existing grant still works. Unset, the read path shares the
owner's role and vigil-core warns once at startup. A code comment saying "read-only from here"
is not an enforcer.

*Enforcer:* `VigilDataSources` routes every read through `Reader`; the startup warning.

## 4. Untrusted input

Everything arriving over a socket, from a child process's stdout, from the database, or from an
env var is untrusted. "Local" is not "trusted": a wedged driver and a corrupt page are the
realistic cases here, not an attacker.

### 4.1 Every read is bounded

| Read | Bound |
|---|---|
| HTTP requests to vigild and vigil-core | hyper's and Kestrel's own header and body limits |
| child process stdout | 1 MiB, then the child is killed (`daemon/src/process.rs`) |
| `/proc` files, in vigild | 1 MiB each, refused whole past that (`host.rs`, `SysFs::read_at_most`) |
| vigild's response headers, in vigil-core | 16 KiB (`VigildClient`) |
| vigild's response, in vigil-core | 10s for the headers (`Limits.DaemonResponseTimeout`) |
| vigild's stream, in vigil-core | 50s of silence ends it (`IdleTimeoutStream`); 1 MiB per event (`ReadBudgetStream`, `Limits.MaxDaemonEventBytes`) |
| the SMS gateway's reply | 10s; headers only, 16 KiB; the body is never read (`SmsClient`) |
| the heartbeat's reply | 10s; headers only, 16 KiB; the body is never read (`Heartbeat`) |
| the mail server's replies | 30s for the whole conversation (`EmailClient.SendTimeout`); the server is one the owner named, and `SmtpClient` reads its replies a line at a time |
| Cloudflare's IP lists | 10s and 16 KiB each, at most 256 networks, none wider than a /8 or an IPv6 /16; refused whole otherwise (`CloudflareRanges`) |
| upsd `LIST VAR` response | 10s of silence; 64 KiB per answer (`ReadBudgetStream`), which must begin `BEGIN LIST VAR` |

A read with no bound is a memory-exhaustion path. Add the cap when you add the read, not after.
Silence and size are two bounds, not one: a peer that keeps sending is never silent.

*Enforcer:* review checklist: "what bounds this read?"; `VigildClientTests`, `SmsClientTests`
and `CloudflareRangesTests` pin the four above.

### 4.2 Every concurrent resource is capped

vigild caps consumers with `api.max_clients` (16), counted per open event stream: a limit on
requests does not do it, because a streaming request is over once its headers are sent.
vigil-core caps browser streams at `LiveHub.MaxSubscribers` (256), eight of them per client,
and drops a stream that falls 256 messages behind rather than buffering for it. Past a cap,
reject with `503` rather than queueing.

A client is an address (an IPv6 /64), taken from `X-Forwarded-For` only on a connection from
`TRUSTED_PROXIES` (`Admission`). Each may make 200 requests in a burst, refilled at 20 a second,
files included; past that it gets `429` with `Retry-After`.

A queue is a concurrent resource too. The runtime's input queue holds `Limits.RuntimeBacklog`
(1024) and makes a poller wait when full; `LiveHub` lets at most `MaxSubscribers` subscriptions
sit in it, so opening and closing streams cannot fill it. The routes that query the database
run `Limits.DatabaseQueryConcurrency` (4) at once with `DatabaseQueryQueue` (16) waiting, and
Kestrel holds `Limits.MaxConnections` (1024) connections, reads no request body, and gives a
connection 10s to send its headers.

These bound what one process can be made to hold and what one address can take. They are not a
defence against a flood from many addresses, which is the front proxy's job (§2).

*Enforcer:* `LiveHubTests`, `ApiTests.LiveRefusesPastTheCap`, `ApiTests.LiveRefusesOneClientPastItsShare`,
`ApiTests.AClientInALoopIsToldToSlowDownAndStillGetsTheSecurityHeaders`,
`ApiTests.EveryRouteThatQueriesTheDatabaseIsUnderTheAdmissionLimit`, and vigild's
`events_refuses_past_max_clients_and_frees_the_slot_when_a_stream_ends`.

### 4.3 Parsing never trusts shape

`smart.rs` is the standard: smartctl's exit code is explicitly *not* used as a success signal,
availability is decided by whether the JSON actually contains `smart_status`, and an
out-of-range number leaves one field empty rather than failing the poll. A malformed input
degrades one value; it never takes down a loop or panics.

In vigil-core the loop to protect is the runtime's. A snapshot is checked where it arrives
(`RespectNullableAnnotations`, then `SnapshotShape` for what that cannot see: a null list entry,
a number that became an infinity), because a value that only fails when it is serialized again
fails on that loop. A derived number is checked after the arithmetic, not only before it.

*Enforcer:* `VigildClientTests.ASnapshotTheRuntimeWouldChokeOnIsDiscardedAndTheStreamCarriesOn`,
`NutProtocolTests`, and the `answers_whatever_…` tests, which feed every parser its own
fixture with each value, line and number spoiled in turn (`daemon/src/mangle.rs`).

### 4.4 Config is validated before it is acted on

vigild validates its whole config and refuses to start with every problem listed, before a
single fan is touched. vigil-core validates its environment at startup the same way
(`VigilOptionsValidator`, run by `ValidateOnStart`). Extend this, do not weaken it: a config
value that reaches a syscall, a bind address or a SQL identifier is validated first.

## 5. Subprocesses

vigild runs `smartctl`, `zpool` and `nvidia-smi`. vigil-core runs nothing.
`zpool` only ever runs `status`: the subcommand is a constant in `zfs.rs`, and config supplies
only the binary's path (ADR-018).

- **Never through a shell.** Always `Command::new(program).args(...)` with argv. No `sh -c`, no
  string concatenation of a command line.
- **Always a hard timeout**, and kill the child on expiry: `process.rs` does this because a GPU
  that fell off the bus or a drive in error recovery blocks indefinitely.
- **Always `stdin(Stdio::null())`**, so a tool that prompts cannot hang waiting for input.
- **Paths come from config, not from `PATH`** where it matters (`nvidia_smi_path`,
  `smartctl_path`, `zpool_path`), so a `PATH` entry cannot substitute a different binary.
- **Bound the output** (§4.1).

*Enforcer:* `scripts/check.sh` greps for shell invocation; review checklist.

## 6. SQL

- Values are **always** bound parameters.
- Identifiers may be interpolated only from a compile-time constant or an exhaustive switch over
  a closed set, with a `sql-literal-ok:` comment saying which. `HistoryQueries` derives a table
  name from a validated `RangeKey`; `SchemaSetup` derives every name from its `AllSeries` table.
- Every statement is bounded: 15s (`Limits.DatabaseStatementTimeout`), except the schema
  setup's one-time work, which can legitimately take minutes. An unbounded query on the shared
  Postgres is a denial of service against the host's own monitoring.
- A refresh over a window whose source rows have been dropped by retention **deletes** the
  rollups for that window, which are then the only copy left. `SchemaSetup` documents this;
  treat retention and refresh windows as a correctness-critical pair.

*Enforcer:* `scripts/check.sh` fails on an interpolated SQL string in `core/Vigil.Core/Data`
with no `sql-literal-ok:` reason, and checks the statement timeout is set.

## 7. Secrets

- `DATABASE_URL` and `DATABASE_URL_READONLY` carry passwords. They are never logged, never in
  an error message, never in an HTTP response, and never committed. `PostgresUri`'s own errors
  never repeat the value at all: a URL that will not parse is the one whose password cannot be
  found to mask.
- So is every notification channel's address (ADR-021, ADR-028): a phone number, an SMS
  gateway's URL or token, a mailbox, a mail server's name and login. A channel reports that a
  send failed and why, never where it was sent: an `IDelivery` returns words fit for the log.
  `SMS_TOKEN` is a bearer token and `SMTP_PASSWORD` a password; `VigilOptionsValidator`
  repeats no SMS or email value in a problem, and `SmsClientTests` and `EmailClientTests`
  check that a failed send reports none. A mail server's reply can quote the recipient, so
  `EmailClient` repeats its status code and never its words.
- `HEARTBEAT_URL` is its check's only secret (whoever has it can keep a dead host looking
  alive), so it is treated like a channel's address; `HeartbeatTests` checks the log.
- `SMTP_PASSWORD` is sent only after STARTTLS: the validator refuses a password with
  `SMTP_STARTTLS=false`.
- upsd publishes each driver's configuration as `driver.parameter.*`, credentials included
  (snmp-ups's community and SNMPv3 passwords, the network drivers' logins), and the dashboard
  shows every variable. `NutProtocol` masks them as they are parsed, so nothing downstream can
  serve one; `NutProtocolTests` pins which names count.
- What identifies the hardware or its owner is not a secret, but it is not published either
  (ADR-023). A drive's serial is cut to its last four characters where it arrives from vigild
  and where it is read from the database (`SerialNumbers`), so the live stream, the event log
  and a notification cannot carry a whole one: a full serial with a model is what a warranty
  claim is filed with. The same serial is inside the name and path of every vdev on an
  `ata-MODEL_SERIAL` by-id link, and is cut there too. A UPS's serial, MAC address, contact,
  location and its driver's device address are redacted whole, with the credentials.
  `SerialNumbersTests` and `NutProtocolTests` pin both.
- Whatever is withheld reads `[REDACTED]` (`Redaction.Marker`), in place of the value or in
  front of the part that is shown, so it cannot be mistaken for a value the device reported.
- Secrets reach a process only through its systemd `EnvironmentFile`, `/etc/vigil-core.env`,
  which is `0640 root:vigil` and lives outside the repo.
- `.env.example` files hold placeholders and nothing real. `.env` is gitignored in `core/`.
- The dev Postgres password (`vigil`) is for `dev/docker-compose.yml` only, which binds
  `127.0.0.1:5433`. It never appears in a deploy file.

*Enforcer:* `scripts/check.sh` greps for committed credentials; review checklist.

## 8. The browser

- vigil-core sends `X-Content-Type-Options`, `Referrer-Policy`, `X-Frame-Options`,
  `Cross-Origin-Opener-Policy`, `Cross-Origin-Resource-Policy`, `Permissions-Policy`, and the
  CSP directives a `<meta>` tag cannot carry (`frame-ancestors`, `object-src`, `base-uri`,
  `form-action`), on every response, a refusal included.
- `Strict-Transport-Security` goes on a response to a request that arrived over HTTPS, which
  vigil-core knows from a trusted proxy's `X-Forwarded-Proto`. API answers are `no-store`.
- No CORS headers are sent, so another site's page cannot read the API from a visitor's browser.
- The script and style policy is the static build's own `<meta>` CSP, which SvelteKit writes in
  hash mode with the hash of each inline script it emits. A header `script-src` would be
  enforced alongside it and block those scripts, which is why the header does not carry one.
- No inline script of ours: the theme resolver is `static/theme.js`, and mode-watcher's own head
  script is switched off (`disableHeadScriptInjection`). No external script, style or font
  origins: vigil-core ships everything it serves.
- The build's fallback page is served for `/drives/<name>` alone, never as a catch-all, so an
  unknown path is still a 404 (ADR-020). It carries the same `<meta>` CSP as every other page.
- No `{@html}` with anything not from a literal in our source.

*Enforcer:* `ApiTests.HealthAnswersWithEverySecurityHeader`; `scripts/check.sh` checks the
headers and the build's hash mode are both still there.

## 9. Dependencies

- A new dependency needs a line in `docs/DECISIONS.md` saying what it replaces and why writing
  it ourselves is worse.
- **No TLS stack** in vigild: it needs C and would end the static musl cross-build (ADR-003).
  vigil-core is .NET, where HTTPS is part of the platform.
- `cargo-deny` gates vigild's advisories and licences; `npm audit` gates the web tree; NuGet's
  own audit gates vigil-core's packages, transitive ones included, as part of the build
  (`core/Directory.Build.props`). All three draw the line at moderate (ADR-014).
- A release publishes an SBOM beside its artifacts, exported from GitHub's dependency graph
  (every action in the workflows is GitHub's own), and CodeQL reads all three languages and
  the workflows weekly and on every change (`codeql.yml`). Its findings inform; they do not gate.
- Lockfiles are committed and updated deliberately, never as a side effect.

*Enforcer:* `cargo deny check`, `dotnet build` and `npm audit` in `scripts/check.sh`.

## 10. Pre-merge checklist

Blocking questions. Any "I don't know" stops the merge.

- [ ] Does this add a listener? What address does it bind, and who authenticates it?
- [ ] Does this add a route that is not a `GET`, or a `GET` that changes anything? (If yes, §2's decision is void: stop.)
- [ ] Does this add a field to anything served? Is it fit to publish to a stranger? (ADR-023)
- [ ] Does this add a read? What bounds it?
- [ ] Does this add a concurrent resource? What caps it?
- [ ] Does this add something that waits or retries? What ends it?
- [ ] Does this build SQL? Are all values bound? Where did any identifier come from?
- [ ] Does this spawn a process? Timeout, argv not shell, null stdin, bounded output?
- [ ] Does this widen vigild's privilege, or move a write out of vigild?
- [ ] Could any new log line, error or response contain a credential or a connection URL?
- [ ] Does this add a dependency? Is it in `docs/DECISIONS.md`? Does it pull TLS into vigild?
- [ ] Does this add `unsafe`? Is there a `// SAFETY:` line stating the invariant?
- [ ] Does this change a systemd unit? Is the hardening baseline intact?
