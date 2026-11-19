# Cleanup plan

No phase is open. Phases 0 to 8 and their follow-ups are done; the plan as it stood, with the
evidence behind every item, is in git history (`git log -- docs/CLEANUP.md`).

A new phase is added here, agreed before it starts. Each phase ends green and shippable:
`scripts/check.sh` passes and the tree is deployable. **Review at every phase boundary**; the
next phase does not start until the previous one is read. Every item names the evidence that
produced it, and is ticked only when its enforcer (test, lint or checklist line) is in place too.

What only a deployment can settle (its hardware, its hosts) is tracked with that deployment,
not here.
