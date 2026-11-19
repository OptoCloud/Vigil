//! The wire types for the host's load: memory, CPU, pressure, disk and network (ADR-025).
//!
//! Everything that grows is published as the kernel's own cumulative counter, never as a rate.
//! A rate needs two readings and vigild keeps no history (invariant 4), so vigil-core divides
//! the difference between two snapshots by the time between their `asOf`s. Gauges (memory,
//! load) are published as read.
//!
//! Counters are plain JSON numbers. A browser holds integers exactly up to 2^53, which is 9 PB
//! of disk traffic or 2.8 million years of CPU ticks at 100 Hz, so unlike a vdev's guid they
//! need no text form.

use serde::{Deserialize, Serialize};

/// One reading of `/proc`. Every part is read on its own, and one that is missing or does not
/// parse is null (or empty) on its own: a kernel without PSI still reports its memory.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostStatus {
    /// When `/proc` was read (RFC 3339, UTC). Rates are taken over the time between two of
    /// these, not between two snapshots: the host is read on its own thread.
    pub as_of: String,
    /// `/proc/uptime`. A smaller value than last time means the host rebooted, and so that
    /// every counter below started again from zero.
    pub uptime_seconds: Option<f64>,
    pub memory: Option<MemoryStatus>,
    pub cpu: Option<CpuTicks>,
    pub load: Option<LoadAverage>,
    pub pressure: Option<Pressure>,
    /// Whole disks backed by a device. Partitions, zvols (a guest's disk, not hardware),
    /// device-mapper, md, loop, ram and nbd devices are left out.
    pub disks: Vec<DiskCounters>,
    /// Only the interfaces named in `[host] interfaces`, in that order (ADR-025).
    pub interfaces: Vec<InterfaceCounters>,
}

/// `/proc/meminfo`, in bytes, plus the ZFS ARC from `arcstats`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MemoryStatus {
    pub total_bytes: u64,
    /// MemAvailable: what could be handed to a new workload without swapping. Total minus
    /// this is "used", the way `free` counts it.
    pub available_bytes: u64,
    pub free_bytes: u64,
    pub buffers_bytes: u64,
    /// Cached plus SReclaimable, which is what `free` shows as cache.
    pub cached_bytes: u64,
    pub swap_total_bytes: u64,
    pub swap_free_bytes: u64,
    /// The ARC's current size. Null without ZFS. Linux counts the ARC as used memory rather
    /// than as cache, so on a ZFS host "used" is mostly this.
    pub arc_bytes: Option<u64>,
    /// The most the ARC may grow to (`c_max`).
    pub arc_max_bytes: Option<u64>,
}

/// The `cpu` line of `/proc/stat`: ticks spent in each state since boot, summed over every CPU.
/// Only ratios of their differences mean anything, so the tick rate never needs to be known.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CpuTicks {
    pub user: u64,
    pub nice: u64,
    pub system: u64,
    pub idle: u64,
    /// Idle while waiting on I/O: on a NAS, the share of time the disks are the bottleneck.
    pub iowait: u64,
    pub irq: u64,
    pub softirq: u64,
    /// Taken by a hypervisor. Zero on bare metal.
    pub steal: u64,
    /// The `cpuN` lines, so that a load average can be read against how many CPUs share it.
    pub logical_cpus: u32,
}

/// `/proc/loadavg`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LoadAverage {
    pub one: f64,
    pub five: f64,
    pub fifteen: f64,
}

/// `/proc/pressure/*` (PSI): how long tasks have been held up waiting for a resource.
/// Each is null when the kernel has no PSI, or has it switched off (`psi=0`).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Pressure {
    pub cpu: Option<PressureStall>,
    pub memory: Option<PressureStall>,
    pub io: Option<PressureStall>,
}

/// Cumulative microseconds stalled since boot.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PressureStall {
    /// At least one task was waiting.
    pub some_micros: u64,
    /// Every non-idle task was waiting at once. Null where the kernel has no `full` line
    /// (CPU pressure before Linux 5.13).
    pub full_micros: Option<u64>,
}

/// One whole disk from `/proc/diskstats`, named as `DriveHealth.deviceName` names it.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiskCounters {
    pub device_name: String,
    pub port: Option<String>,
    pub read_bytes: u64,
    pub written_bytes: u64,
    /// Milliseconds with at least one I/O in flight: its rate is the disk's busy fraction,
    /// what `iostat` calls %util.
    pub busy_millis: u64,
}

/// One interface from `/proc/net/dev`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InterfaceCounters {
    pub name: String,
    pub received_bytes: u64,
    pub transmitted_bytes: u64,
}
