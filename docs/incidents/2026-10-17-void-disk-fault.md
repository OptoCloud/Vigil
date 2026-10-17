# void: a disk faulted and nobody saw it for nine days

*orion, 2026-10-08 → 2026-10-17. Written 2026-10-17.*

## Summary

On **2026-10-08 at 04:46**, one unreadable sector on bay 5's IronWolf (ZGY9ZQY6) made ZFS
fault the disk. The hot spare took over automatically and resilvered with no errors.
Throughout, the pool kept full raidz2 protection (it could still lose any two disks), and
**no data was ever at risk**.

The pool showed **DEGRADED for nine days without anyone knowing**. The alert mail went
nowhere: the relay on `127.0.0.1:1025` has never had anything listening since the August
rebuild. The vigil dashboard kept showing OK, because it has no ZFS pool state at all, only
SMART, and SMART still said PASSED for the faulted disk.

It came to light on 2026-10-17, when the disks got loud at 03:00. That noise was unrelated:
Immich's nightly integrity check, which reads every photo.

## Timeline

| When | What |
|---|---|
| 10-08 04:46:00 | The kernel logs `critical medium error, dev sde, sector 219374520`: a single 380 KB read fails because of one unreadable sector. |
| 10-08 04:46:01 | zed logs **14** I/O error events in the same second. One is for the merged read, and one each for the 13 smaller reads ZFS had merged into it. |
| 10-08 04:46:16 | 14 errors is more than the default threshold of **10 in 600 s**, so ZGY9ZQY6 is **FAULTED**. zed emails root, and postfix gets `Connection refused` on port 1025. |
| 10-08 04:46:17 | Spare WX10B77K attaches, and the resilver starts at 04:46:54. |
| 10-08 09:10 | Resilver finished: 1.40 T in 4 h 24 m, 0 errors. The pool reads DEGRADED (a spare in use, the faulted disk still attached), but is fully redundant. |
| 10-08 → 10-17 | Silence. Vigil shows OK. The mail queue fills: zed's mail, 5× `[orion] backup check FAILED`, vzdump reports, and bounces for anything older than 5 days. |
| 10-16 03:03 | Unrelated, but noted: SATA link reset on rpool SSD `ata2` (S7EWNL…) with failed writes. CRC count 0, so most likely the cable or port. |
| 10-17 03:02–03:30 | Immich `IntegrityService` checksums all 74,013 assets. All 8 HDDs do about 50 random reads/s each, which is the noise. It finished with 0 missing files. |
| 10-17 03:35 | `zpool detach` of the faulted disk. The pool goes ONLINE, and WX10B77K becomes a permanent member. |
| 10-17 03:38 | WX10B77K moved live from 4-bay 4 into 8-bay bay 5. It was pulled **without `zpool offline` first**, so zed pulled in the last spare (WX121Q2D) and started a full 11.8 T resilver. |
| 10-17 03:39 | Detaching that spare cancelled the full resilver (0 B written). WX10B77K came back with a 1.18 MB catch-up in 3 s. |
| 10-17 03:41 → 11:10 | SMART extended self-test on ZGY9ZQY6: **completed without error**. |
| 10-17 ~17:00 | ZGY9ZQY6 labels cleared and re-added as a hot spare in 4-bay 2 (`ata-4`). vigil's config updated, deployed and committed. |

## The disk: how damaged is ZGY9ZQY6?

Lightly. One bad spot, which was rewritten or recovered, on an aging disk.

| | ZGY9ZQY6 (now a spare) | WFN0J519 (`scratch`, for comparison) |
|---|---|---|
| Model | IronWolf ST4000VN008, CMR | BarraCuda ST4000DM004, **SMR** |
| Power-on hours | 38,335 (≈ 4.4 years) | 61,418 (≈ 7 years) |
| Reallocated sectors | 0 | 1,048 |
| Pending / offline-uncorrectable sectors | 0 / 0 | 0 / 0 |
| Uncorrectable errors reported (187) | 3: this event, and a similar burst ≈ 13k hours earlier | 0 |
| Last full-surface test | **passed 2026-10-17** | never |
| Warranty | almost certainly expired (3-year model, more than 3 years of runtime) | — |

It isn't trusted as a pool member again, but as an idle spare it's fine. As a CMR NAS disk
it's much better suited to a resilver than any SMR disk.

## The fault threshold: decided to leave it

- zed faults a disk after `io_n` errors within `io_t` seconds. The default is **10 in 600 s**,
  and nothing on orion overrides it.
- As this event showed, **one bad sector inside a merged read can trip it by itself**. A
  merged read can be about 1 MB, which on this pool could mean 30 or more errors from a
  single sector.
- Raising it (`zpool set io_n=20 void raidz2-0`, set on the top-level vdev because a value on
  a leaf is lost when the disk is replaced) would have absorbed this event. The cost is that
  a disk that really is dying stays in the pool longer.
- **Decision: keep the default.** A false fault costs one spare resilver of about 4–5 h, with
  full raidz2 protection throughout, and there are two spares again. Sequential rebuild
  (`replace -s`) is not supported on raidz, so a resilver can't be made cheaper. A false
  fault **is** cheap to undo if someone hears about it quickly: `zpool clear` the disk, then
  `zpool detach` the spare. That brings back only the missed writes, as happened tonight.
  **So alerting is the real fix.**

## Current state (2026-10-17 evening)

- `void`: ONLINE, 8/8 disks, 0 errors. WX10B77K is in bay 5.
- Spares: WX121Q2D (4-bay 3, `ata-6`) and ZGY9ZQY6 (4-bay 2, `ata-4`), both AVAIL.
  4-bay 4 (`ata-5`) is empty.
- vigil: fans and drive zones cover all 11 HDDs. Committed in Hosts as
  `void: bay 5 swap, faulted disk back as a spare` (not pushed).


## What came of it

The vigil half is planned in `docs/CLEANUP.md` Phase 7, and two decisions from it are recorded
as ADR-018 (pool state comes from vigild, and zed's own push is kept alongside it) and ADR-019
(the page's verdict is the worst raised condition). The proposal written on the night was
adjusted where vigil already works differently. Phase 7 lists each adjustment.

These items are outside vigil and belong to the Hosts repo. They are kept here so they are not
lost:

- **Push path that does not depend on vigil or mail.** Do this first: it needs no vigil code.
  OpenZFS 2.4's zed can post to ntfy itself. In `machines/orion/host/etc/zfs/zed.d/zed.rc`:
  ```
  ZED_NTFY_TOPIC="<topic>"
  ZED_NTFY_URL="<the ntfy server vigil already posts to>"
  # ZED_NTFY_ACCESS_TOKEN="…"   if the topic needs auth: keep it out of git (vault template)
  ```
  Then `make apply-host` and `systemctl restart zfs-zed`. Test it on a scratch pool (Phase 7.8),
  never `void`.
- **Fix the mail relay** (Hosts TODO "Finish alarm delivery: mail"). Port 1025 has had nothing
  listening since the rebuild, and `mailq` holds about 38 deferred messages. When it works,
  read the 5 `[orion] backup check FAILED` mails, or run `/usr/local/bin/backup-check.sh` by
  hand.
- **Schedule SMART self-tests.** No disk has run one in about 15,000 hours: `smartd.conf` has no
  `-s` schedule. For example `DEVICESCAN … -s (S/../.././02|L/../../7/03)`: a short test daily
  and a long test weekly, on a staggered day and away from the monthly scrub night. Phase 7.6
  alerts on a stale or failed self-test, which needs this first.
- **The chipset SATA controller** (`0000:01:00.1`) logs IOMMU `IO_PAGE_FAULT`s. It is the same
  controller as the 10-16 `ata2` link reset on the rpool SSD. Check the cable, then BIOS and
  firmware.
- `[zfs]` in `vigild.toml` goes through `make plan-host` / `make apply-host` as usual once 7.1
  lands, and vigild has to be restarted.
