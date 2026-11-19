# `zpool status -j --json-int` fixtures

**Derived, not captured.** These were written from OpenZFS 2.4.4's own JSON writer
(`cmd/zpool/zpool_main.c`: `status_callback_json`, `vdev_stats_nvlist`, `spares_nvlist`,
`scan_status_nvlist`). Every key and value shape follows that code: the integers `--json-int`
gives, a hot spare appearing both inside its `spare-N` vdev and in `spares`, and timestamps as
Unix seconds. The values model a raidz2 pool `tank` with two hot spares, with made-up serials,
WWNs and guids.

Replace each one with real output when it can be captured: `zpool status -j --json-int` on a
healthy host for `healthy.json`, and a scratch pool (below) for the others. The parser's tests
must still pass against the captures, and any difference is a bug in the parser, not in the
capture. A capture's serials are masked by vigil-core, not by vigild: replace them before
committing one.

| File | State |
|---|---|
| `healthy.json` | `tank` ONLINE with both spares AVAIL and a clean scrub; `rpool` a mirror |
| `degraded-spare-inuse.json` | a disk FAULTED (ERR_EXCEEDED), the spare INUSE, the resilver finished |
| `resilvering.json` | the same fault, with the resilver still running |
| `scrub-errors.json` | ONLINE, but a scrub found checksum errors and two permanent data errors |

## A scratch pool, for the faulted states

On file vdevs, never on a pool that holds data:

```sh
truncate -s 1G /tmp/zt{1..5}
zpool create ztest raidz2 /tmp/zt1 /tmp/zt2 /tmp/zt3 /tmp/zt4 spare /tmp/zt5
zinject -d /tmp/zt1 -e io -T read -f 100 ztest     # I/O errors: zed faults zt1, the spare attaches
zpool offline ztest /tmp/zt2                        # DEGRADED without errors
zinject -c all; zpool destroy ztest
```

The same run checks a deployment end to end: for each step, zed's own push and vigil's alert
arrive, the snapshot shows the change within one `[zfs]` poll, the page goes red, and
everything recovers when the change is undone.
