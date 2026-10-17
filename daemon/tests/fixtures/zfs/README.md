# `zpool status -j --json-int` fixtures

**Derived, not captured.** These were written from OpenZFS 2.4.4's own JSON writer
(`cmd/zpool/zpool_main.c`: `status_callback_json`, `vdev_stats_nvlist`, `spares_nvlist`,
`scan_status_nvlist`), because nothing that wrote them could reach orion. Every key and value
shape follows that code: the integers `--json-int` gives, a hot spare appearing both inside its
`spare-N` vdev and in `spares`, and timestamps as Unix seconds. The values model `void` on
2026-10-08 (`docs/incidents/2026-10-17-void-disk-fault.md`), with made-up serials and guids.

Replace each one with real output when it can be captured: `zpool status -j --json-int` on orion
for `healthy.json`, and the scratch pool from `docs/CLEANUP.md` step 7.8 for the others. The
parser's tests must still pass against the captures, and any difference is a bug in the
parser, not in the capture.

| File | State |
|---|---|
| `healthy.json` | `void` ONLINE with both spares AVAIL and a clean scrub; `rpool` a mirror |
| `degraded-spare-inuse.json` | the night of the fault: a disk FAULTED (ERR_EXCEEDED), the spare INUSE, the resilver finished |
| `resilvering.json` | the same fault, with the resilver still running |
| `scrub-errors.json` | ONLINE, but a scrub found checksum errors and two permanent data errors |
