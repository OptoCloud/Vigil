# Contributing

vigil runs one person's NAS, and it is published so that it can be read, reused and corrected.
A fix or a sharper test is welcome. A feature is worth an issue first: the answer is often in
`docs/DECISIONS.md` already, and the scope is narrow on purpose (`docs/CLEANUP.md` says what
hardware is supported, which is what is installed in that one host).

## Before you change anything

Read `docs/STYLE.md` and `docs/SECURITY.md`. They are short, they are about this codebase, and
every rule in them names what enforces it. `docs/ARCHITECTURE.md` lists the invariants: breaking
one is a design change, not a refactor.

## Working on it

No hardware is needed. `dev/` has stand-ins for vigild, upsd and the SMS gateway, and a
TimescaleDB; the README's Development section wires them together.

```bash
scripts/check.sh              # everything CI runs; it must pass before every commit
scripts/check.sh --rust       # or --dotnet, --web, --rules
```

The database tests run when `VIGIL_TEST_DATABASE_URL` points at a TimescaleDB, and CI runs them.

## A pull request

- One concern per commit, with the subject naming the behaviour (`docs/STYLE.md` §12).
- A wire type changes in three places in one commit: its producer, the regenerated contract
  file, and `web/src/lib/types.ts`. The tests say which one was missed.
- Work through the pre-merge checklist at the end of `docs/SECURITY.md`. A route that is not a
  `GET` is not a pull request, it is a conversation: the dashboard has no login (ADR-023).
- A decision that would look arbitrary in a year gets an entry in `docs/DECISIONS.md`.

vigil is AGPL-3.0-or-later, and a contribution is made under the same licence.
