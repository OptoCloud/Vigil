//! ZFS pool state, from `zpool status -j --json-int` (ADR-018).
//!
//! SMART judges a disk by its own self-assessment; ZFS judges it by whether its reads succeed.
//! A disk ZFS has faulted can still pass SMART, so a vigil that read only SMART would say Live
//! over a DEGRADED pool for as long as nobody looked.
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
mod tests;
