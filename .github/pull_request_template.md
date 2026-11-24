**What this changes, and why**

**How it was checked** (`scripts/check.sh` passes; anything verified on real hardware)

- [ ] I went through the pre-merge checklist at the end of `docs/SECURITY.md`.
- [ ] No route that is not a `GET` (ADR-023).
- [ ] A changed wire type is changed in its producer, its contract file and `web/src/lib/types.ts`.
- [ ] A decision that would look arbitrary later is in `docs/DECISIONS.md`.
