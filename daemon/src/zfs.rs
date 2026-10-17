//! ZFS pool state, from `zpool status -j --json-int` (ADR-018).
//!
//! SMART judges a disk by its own self-assessment; ZFS judges it by whether its reads succeed.
//! On 2026-10-08 ZFS faulted a disk whose SMART still said PASSED, and vigil, reading only
//! SMART, said Live for nine days (docs/incidents/2026-10-17-void-disk-fault.md).
//!
//! Read-only by construction: the subcommand is fixed here and config supplies only the
//! binary's path. `zpool status` reports what the kernel already holds and touches no disk, so
//! a sleeping drive stays asleep, the same promise as smartctl's `-n standby`.
//!
//! The JSON is OpenZFS's, not ours, so nothing about its shape is trusted (SECURITY.md §4.3).
//! A document that is not zpool's makes the whole poll unavailable; a pool or vdev it could
//! not describe is reported in a state that is not ONLINE. Neither is ever shown as healthy,
//! the rule an unreadable sensor follows too. Like smartctl's, the exit code is not used:
//! whether the output is usable is decided by the output.
//!
//! This reports what one poll saw. A scrub hidden behind a later resilver, an error count that
//! grew, a state that changed: noticing those needs history, and history is vigil-core's.

use crate::config::ZfsConfig;
use crate::disks::Disk;
use crate::process;
use crate::protocol::rfc3339_from_unix_seconds;
pub use crate::protocol::{PoolScan, PoolStatus, VdevStatus, ZfsStatus};
use serde_json::{Map, Value};
use std::collections::HashMap;
use std::time::Duration;

/// `--json-int` makes every count a number rather than "1.40T". `-j` alone would need the
/// strings parsed back, losing precision on the way.
const ZPOOL_STATUS_ARGS: [&str; 3] = ["status", "-j", "--json-int"];

/// The vdev groups `zpool status -j` writes beside the main tree. Log, special and dedup vdevs
/// are left out of the main tree and listed here, each group a map of vdevs like the tree.
const VDEV_GROUPS: [&str; 4] = ["dedup", "special", "logs", "l2cache"];

/// The state given to a pool or vdev zpool did not describe: anything but ONLINE, so it alerts.
const UNKNOWN_STATE: &str = "UNKNOWN";

/// Runs zpool and reads its answer. `disk_of` names the disk under a vdev's path.
pub fn poll(config: &ZfsConfig, disk_of: impl Fn(&str) -> Option<Disk>, as_of: &str) -> ZfsStatus {
    let output = process::run(&config.zpool_path, &ZPOOL_STATUS_ARGS, Duration::from_secs_f64(config.timeout_secs));
    parse(output.as_ref().map_or("", |output| output.stdout.as_str()), disk_of, as_of)
}

pub fn parse(json: &str, disk_of: impl Fn(&str) -> Option<Disk>, as_of: &str) -> ZfsStatus {
    let root: Value = serde_json::from_str(json).unwrap_or(Value::Null);
    let unavailable = || ZfsStatus { is_available: false, as_of: as_of.to_owned(), pools: Vec::new() };

    // Both, or this is not zpool status's document: "pools" alone could be anything's.
    if !root["output_version"].is_object() {
        return unavailable();
    }
    let Some(pools) = root["pools"].as_object() else {
        return unavailable();
    };

    let pools = sorted(pools).map(|(key, pool)| parse_pool(key, pool, &disk_of)).collect();
    ZfsStatus { is_available: true, as_of: as_of.to_owned(), pools }
}

fn parse_pool(key: &str, pool: &Value, disk_of: &impl Fn(&str) -> Option<Disk>) -> PoolStatus {
    let mut vdevs = Vec::new();
    // The tree's top is the root vdev, keyed by the pool's name, which carries the totals.
    let tree = pool["vdevs"].as_object();
    let root = tree.and_then(|tree| tree.values().find(|vdev| vdev["vdev_type"] == "root"));
    if let Some(tree) = tree {
        collect_leaves(tree, None, disk_of, &mut vdevs);
    }
    for group in VDEV_GROUPS {
        if let Some(group) = pool[group].as_object() {
            collect_leaves(group, None, disk_of, &mut vdevs);
        }
    }
    if let Some(spares) = pool["spares"].as_object() {
        merge_spares(spares, disk_of, &mut vdevs);
    }

    PoolStatus {
        name: text(&pool["name"]).unwrap_or_else(|| key.to_owned()),
        state: text(&pool["state"]).unwrap_or_else(|| UNKNOWN_STATE.to_owned()),
        error_count: count(&pool["error_count"]),
        size_bytes: root.and_then(|root| count(&root["total_space"])),
        allocated_bytes: root.and_then(|root| count(&root["alloc_space"])),
        scan: parse_scan(&pool["scan_stats"]),
        vdevs,
    }
}

/// Every leaf under `vdevs`, depth first. A vdev with children is a grouping (raidz2-0,
/// mirror-0, spare-5) and is named as its children's parent rather than listed itself: its
/// state is its children's, and a disk is what someone replaces.
fn collect_leaves(vdevs: &Map<String, Value>, parent: Option<&str>, disk_of: &impl Fn(&str) -> Option<Disk>, out: &mut Vec<VdevStatus>) {
    for (key, vdev) in sorted(vdevs) {
        if vdev["class"] == "hole" {
            continue;
        }

        match vdev["vdevs"].as_object().filter(|children| !children.is_empty()) {
            Some(children) => {
                // The root is the pool itself, so what sits directly under it has no parent.
                let name = (vdev["vdev_type"] != "root").then(|| text(&vdev["name"]).unwrap_or_else(|| key.to_owned()));
                collect_leaves(children, name.as_deref(), disk_of, out);
            }
            None => out.push(parse_leaf(key, vdev, parent, disk_of)),
        }
    }
}

fn parse_leaf(key: &str, vdev: &Value, parent: Option<&str>, disk_of: &impl Fn(&str) -> Option<Disk>) -> VdevStatus {
    let path = text(&vdev["path"]);
    let disk = path.as_deref().and_then(disk_of);

    VdevStatus {
        name: text(&vdev["name"]).unwrap_or_else(|| key.to_owned()),
        guid: count(&vdev["guid"]).map(|guid| guid.to_string()),
        device_name: disk.as_ref().map(|disk| disk.stable_name.clone()),
        port: disk.and_then(|disk| disk.port),
        path,
        parent: parent.map(str::to_owned),
        class: text(&vdev["class"]).unwrap_or_else(|| "normal".to_owned()),
        state: text(&vdev["state"]).unwrap_or_else(|| UNKNOWN_STATE.to_owned()),
        aux: text(&vdev["aux"]),
        read_errors: count(&vdev["read_errors"]),
        write_errors: count(&vdev["write_errors"]),
        checksum_errors: count(&vdev["checksum_errors"]),
        slow_ios: count(&vdev["slow_ios"]),
    }
}

/// A spare is listed twice once it is in use: inside its `spare-N` vdev, where its error
/// counts and its place in the tree are, and in `spares`, where its spare state (INUSE) is.
/// One disk, so one entry: the tree's, given the spare state. A spare not in the tree (AVAIL)
/// is listed from `spares` alone, with no error counts, because zpool keeps none for it.
fn merge_spares(spares: &Map<String, Value>, disk_of: &impl Fn(&str) -> Option<Disk>, vdevs: &mut Vec<VdevStatus>) {
    for (key, spare) in sorted(spares) {
        let spare = parse_leaf(key, spare, None, disk_of);

        match vdevs.iter_mut().find(|vdev| vdev.guid.is_some() && vdev.guid == spare.guid) {
            Some(in_tree) => {
                in_tree.state = spare.state;
                in_tree.class = spare.class;
            }
            None => vdevs.push(spare),
        }
    }
}

fn parse_scan(scan: &Value) -> Option<PoolScan> {
    let function = text(&scan["function"]).filter(|function| function != "NONE")?;

    Some(PoolScan {
        function,
        state: text(&scan["state"]).unwrap_or_else(|| UNKNOWN_STATE.to_owned()),
        start_time: count(&scan["start_time"]).and_then(rfc3339_from_unix_seconds),
        end_time: count(&scan["end_time"]).and_then(rfc3339_from_unix_seconds),
        to_examine_bytes: count(&scan["to_examine"]),
        examined_bytes: count(&scan["examined"]),
        issued_bytes: count(&scan["issued"]),
        processed_bytes: count(&scan["processed"]),
        errors: count(&scan["errors"]),
    })
}

/// A count as `--json-int` writes it. A string is accepted too, in case a vdev's value comes
/// back as text, but only one that is an exact integer: "1.40T" is not a count.
fn count(value: &Value) -> Option<u64> {
    value.as_u64().or_else(|| value.as_str()?.parse().ok())
}

fn text(value: &Value) -> Option<String> {
    value.as_str().map(str::to_owned)
}

/// Entries in key order. serde_json keeps insertion order in tests (a dev-dependency turns on
/// `preserve_order`) but sorts in the daemon, so sorting here makes the two agree.
fn sorted(map: &Map<String, Value>) -> impl Iterator<Item = (&str, &Value)> {
    let mut entries: Vec<_> = map.iter().map(|(key, value)| (key.as_str(), value)).collect();
    entries.sort_by_key(|(key, _)| *key);
    entries.into_iter()
}

/// What changed since the last poll, worth a log line: a pool's state, and zpool becoming
/// unreadable or readable again. Alerting on it is vigil-core's job; this is for the journal.
#[derive(Default)]
pub struct PoolLog {
    readable: Option<bool>,
    states: HashMap<String, String>,
}

/// One log line: a warning, or the all-clear that follows one.
#[derive(Debug, PartialEq)]
pub enum PoolChange {
    Warning(String),
    Cleared(String),
}

impl PoolLog {
    pub fn update(&mut self, status: &ZfsStatus) -> Vec<PoolChange> {
        let mut changes = Vec::new();

        if self.readable != Some(status.is_available) {
            if !status.is_available {
                changes.push(PoolChange::Warning("zpool status gave no usable answer: pool state is unknown.".to_owned()));
            } else if self.readable.is_some() {
                changes.push(PoolChange::Cleared("zpool status answers again.".to_owned()));
            }
            self.readable = Some(status.is_available);
        }

        // Unreadable says nothing about the pools, so they keep their last known state.
        if !status.is_available {
            return changes;
        }

        for pool in &status.pools {
            let previous = self.states.insert(pool.name.clone(), pool.state.clone());
            if previous.as_deref() == Some(pool.state.as_str()) {
                continue;
            }
            if pool.state != "ONLINE" {
                changes.push(PoolChange::Warning(format!("Pool {} is {}.", pool.name, pool.state)));
            } else if previous.is_some() {
                changes.push(PoolChange::Cleared(format!("Pool {} is ONLINE again.", pool.name)));
            }
        }

        changes
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const NOW: &str = "2026-10-08T05:00:00.000Z";
    const HEALTHY: &str = include_str!("../tests/fixtures/zfs/healthy.json");
    const DEGRADED: &str = include_str!("../tests/fixtures/zfs/degraded-spare-inuse.json");
    const RESILVERING: &str = include_str!("../tests/fixtures/zfs/resilvering.json");
    const SCRUB_ERRORS: &str = include_str!("../tests/fixtures/zfs/scrub-errors.json");

    const FAULTED_DISK: &str = "ata-ST4000VN008-2DR166_ZGY9ZQY6";
    const IN_USE_SPARE: &str = "ata-WDC_WD40EFRX-68N32N0_WD-WX10B77K";
    const IDLE_SPARE: &str = "ata-WDC_WD40EFRX-68N32N0_WD-WX121Q2D";

    /// Every vdev path in the fixtures is a by-id link; this names its disk the way the real
    /// resolver would, after the serial at the end of the name.
    fn fake_disk(path: &str) -> Option<Disk> {
        let name = path.strip_prefix("/dev/disk/by-id/")?.rsplit_once("-part")?.0;
        let serial = name.rsplit('_').next()?;
        Some(Disk { device: "sdx".to_owned(), stable_name: format!("naa.{serial}"), port: Some(format!("port-of-{serial}")) })
    }

    fn void(json: &str) -> PoolStatus {
        let status = parse(json, fake_disk, NOW);
        assert!(status.is_available);
        status.pools.into_iter().find(|pool| pool.name == "void").expect("void is in every fixture")
    }

    fn vdev<'a>(pool: &'a PoolStatus, name: &str) -> &'a VdevStatus {
        pool.vdevs.iter().find(|vdev| vdev.name == name).unwrap_or_else(|| panic!("no vdev {name}"))
    }

    #[test]
    fn a_healthy_host_reads_as_every_pool_online_with_its_disks_named_as_smart_names_them() {
        let status = parse(HEALTHY, fake_disk, NOW);

        assert_eq!((status.is_available, status.as_of.as_str()), (true, NOW));
        assert_eq!(
            status.pools.iter().map(|pool| (pool.name.as_str(), pool.state.as_str())).collect::<Vec<_>>(),
            [("rpool", "ONLINE"), ("void", "ONLINE")]
        );

        let void = &status.pools[1];
        assert_eq!(
            (void.size_bytes, void.allocated_bytes, void.error_count),
            (Some(32_000_000_000_000), Some(12_940_000_000_000), Some(0))
        );
        // Eight members and two spares: the leaves, never raidz2-0 or the root itself.
        assert_eq!(void.vdevs.len(), 10);
        assert!(void.vdevs.iter().filter(|vdev| vdev.class == "normal").all(|vdev| vdev.parent.as_deref() == Some("raidz2-0")));

        let disk = vdev(void, FAULTED_DISK);
        assert_eq!(disk.device_name.as_deref(), Some("naa.ZGY9ZQY6"));
        assert_eq!(disk.port.as_deref(), Some("port-of-ZGY9ZQY6"));
        assert_eq!(disk.guid.as_deref(), Some("9000000000000001106"));
        assert_eq!((disk.read_errors, disk.write_errors, disk.checksum_errors, disk.slow_ios), (Some(0), Some(0), Some(0), Some(0)));
        assert_eq!(disk.aux, None);

        let rpool = &status.pools[0];
        assert!(rpool.vdevs.iter().all(|vdev| vdev.parent.as_deref() == Some("mirror-0") && vdev.state == "ONLINE"));
    }

    #[test]
    fn idle_spares_are_available_and_carry_no_error_counts() {
        let void = void(HEALTHY);

        for name in [IN_USE_SPARE, IDLE_SPARE] {
            let spare = vdev(&void, name);
            assert_eq!((spare.class.as_str(), spare.state.as_str(), spare.parent.as_deref()), ("spare", "AVAIL", None));
            assert_eq!(spare.read_errors, None, "zpool keeps no counts for an idle spare");
        }
    }

    #[test]
    fn the_night_of_the_fault_reads_as_a_faulted_disk_and_a_spare_in_use() {
        let void = void(DEGRADED);

        assert_eq!(void.state, "DEGRADED");
        let faulted = vdev(&void, FAULTED_DISK);
        assert_eq!((faulted.state.as_str(), faulted.aux.as_deref(), faulted.read_errors), ("FAULTED", Some("ERR_EXCEEDED"), Some(14)));
        assert_eq!(faulted.parent.as_deref(), Some("spare-5"));

        // Listed once, though zpool lists it twice: in the tree, with its spare state.
        let spare = vdev(&void, IN_USE_SPARE);
        assert_eq!(void.vdevs.iter().filter(|vdev| vdev.name == IN_USE_SPARE).count(), 1);
        assert_eq!((spare.state.as_str(), spare.class.as_str(), spare.parent.as_deref()), ("INUSE", "spare", Some("spare-5")));
        assert_eq!(spare.read_errors, Some(0), "an in-use spare keeps the counts the tree gives it");
        assert_eq!(vdev(&void, IDLE_SPARE).state, "AVAIL");

        let scan = void.scan.expect("the resilver");
        assert_eq!((scan.function.as_str(), scan.state.as_str(), scan.errors), ("RESILVER", "FINISHED", Some(0)));
        assert_eq!(scan.start_time.as_deref(), Some("2026-10-08T04:46:54.000Z"));
        assert_eq!(scan.end_time.as_deref(), Some("2026-10-08T09:10:54.000Z"));
        assert_eq!(scan.processed_bytes, Some(1_539_316_278_886));
    }

    #[test]
    fn a_running_resilver_has_progress_and_no_end() {
        let scan = void(RESILVERING).scan.expect("the resilver");

        assert_eq!((scan.state.as_str(), scan.end_time), ("SCANNING", None));
        assert_eq!((scan.issued_bytes, scan.to_examine_bytes), (Some(980_000_000_000), Some(12_940_000_000_000)));
    }

    #[test]
    fn a_scrub_that_found_damage_shows_it_on_the_pool_and_on_the_disk() {
        let void = void(SCRUB_ERRORS);

        assert_eq!((void.state.as_str(), void.error_count), ("ONLINE", Some(2)));
        assert_eq!(void.scan.as_ref().and_then(|scan| scan.errors), Some(3));
        assert_eq!(vdev(&void, "ata-ST4000VN008-2DR166_ZGY9ZQ03").checksum_errors, Some(3));
    }

    #[test]
    fn anything_but_zpools_document_is_unavailable_never_an_empty_host() {
        let no_pools = r#"{"output_version":{"command":"zpool status","vers_major":0,"vers_minor":1}}"#;
        for json in ["", "{ not json", "no pools available", r#"{"pools":{}}"#, no_pools] {
            let status = parse(json, fake_disk, NOW);
            assert!(!status.is_available, "{json}");
            assert!(status.pools.is_empty());
        }

        let none_imported = r#"{"output_version":{"command":"zpool status","vers_major":0,"vers_minor":1},"pools":{}}"#;
        assert!(parse(none_imported, fake_disk, NOW).is_available);
    }

    #[test]
    fn a_pool_or_vdev_zpool_did_not_describe_is_never_online() {
        let json = r#"{"output_version":{},"pools":{"odd":{"vdevs":{"odd":{"vdev_type":"root","vdevs":{"d1":{"read_errors":"1.2K"}}}},
            "scan_stats":{"function":"NONE"}}}}"#;

        let pool = &parse(json, fake_disk, NOW).pools[0];

        assert_eq!((pool.name.as_str(), pool.state.as_str(), pool.scan.as_ref()), ("odd", UNKNOWN_STATE, None));
        let disk = &pool.vdevs[0];
        assert_eq!((disk.name.as_str(), disk.state.as_str(), disk.parent.as_deref()), ("d1", UNKNOWN_STATE, None));
        assert_eq!(disk.read_errors, None, "a human-readable count is not a count");
        assert_eq!((disk.path.as_deref(), disk.device_name.as_deref()), (None, None));
    }

    #[test]
    fn poll_reports_unavailable_when_zpool_is_missing() {
        let config = ZfsConfig { zpool_path: "definitely-not-zpool-9b2e".to_owned(), ..ZfsConfig::default() };

        assert!(!poll(&config, fake_disk, NOW).is_available);
    }

    #[test]
    fn the_log_reports_each_change_once_and_keeps_states_through_an_unreadable_poll() {
        let mut log = PoolLog::default();
        let unreadable = parse("", fake_disk, NOW);
        let warning = |text: &str| PoolChange::Warning(text.to_owned());
        let cleared = |text: &str| PoolChange::Cleared(text.to_owned());

        assert_eq!(log.update(&parse(HEALTHY, fake_disk, NOW)), [], "healthy at startup is not news");
        assert_eq!(log.update(&parse(DEGRADED, fake_disk, NOW)), [warning("Pool void is DEGRADED.")]);
        assert_eq!(log.update(&parse(RESILVERING, fake_disk, NOW)), []);
        assert_eq!(log.update(&unreadable), [warning("zpool status gave no usable answer: pool state is unknown.")]);
        assert_eq!(log.update(&unreadable), []);
        // Still DEGRADED once zpool answers again, so only the readability is news.
        assert_eq!(log.update(&parse(DEGRADED, fake_disk, NOW)), [cleared("zpool status answers again.")]);
        assert_eq!(log.update(&parse(HEALTHY, fake_disk, NOW)), [cleared("Pool void is ONLINE again.")]);
    }
}
