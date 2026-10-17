//! The wire types vigild serves on its socket's `/status` and `/events`: one snapshot of the
//! host, defined once.
//!
//! Field names are the JSON contract (camelCase on the wire). vigil-core's client types mirror
//! these, and `contract.rs` pins them in `daemon/contract.json` so the mirror is checked by a
//! test rather than held together by a comment (docs/STYLE.md §1.3). Treat a field rename here
//! as a breaking change to vigil-core.

use serde::{Deserialize, Serialize};
use time::OffsetDateTime;

/// How long `/events` may stay silent before vigild sends a keepalive comment.
///
/// A consumer's read timeout must be more than twice this, so that one keepalive lost to a slow
/// hop is not taken for a dead daemon. vigil-core's is 50s; it reads this value from
/// `daemon/contract.json` and its tests assert the ratio, so changing it here fails there.
pub const STREAM_KEEPALIVE_SECONDS: u64 = 15;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SensorCategory {
    Cpu,
    BoardAmbient,
    Drive,
    Gpu,
    Memory,
    Hba,
}

/// A single point-in-time reading. `id` is a stable logical name ("cpu", "drive:{wwn}"),
/// never a raw hwmonN path.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SensorReading {
    pub id: String,
    pub category: SensorCategory,
    pub label: String,
    pub celsius_or_null: Option<f64>,
    pub source_path: String,
    pub is_available: bool,
    /// Drives only: the by-path name of the port the drive is plugged into. Null for every
    /// other sensor, and for a drive when no by-path link points at it.
    #[serde(default)]
    pub port: Option<String>,
}

impl SensorReading {
    pub fn new(id: &str, category: SensorCategory, label: &str, celsius: Option<f64>, source_path: &str) -> Self {
        Self {
            id: id.to_owned(),
            category,
            label: label.to_owned(),
            celsius_or_null: celsius,
            source_path: source_path.to_owned(),
            is_available: celsius.is_some(),
            port: None,
        }
    }

    pub fn with_port(mut self, port: Option<String>) -> Self {
        self.port = port;
        self
    }
}

/// Values accepted by nct6775/nct6798's pwmN_enable sysfs attribute.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum PwmMode {
    /// Fans jump to full speed. Never written by vigild.
    Disabled = 0,
    Manual = 1,
    ThermalCruise = 2,
    SpeedCruise = 3,
    /// NCT6775F only; listed so a read-back of it isn't reported as unknown.
    SmartFanIII = 4,
    /// BIOS "Smart Fan IV": the multi-slope curve mode the board ships in.
    SmartFanIV = 5,
}

impl PwmMode {
    pub fn from_raw(raw: &str) -> Option<Self> {
        match raw.parse::<u8>().ok()? {
            0 => Some(Self::Disabled),
            1 => Some(Self::Manual),
            2 => Some(Self::ThermalCruise),
            3 => Some(Self::SpeedCruise),
            4 => Some(Self::SmartFanIII),
            5 => Some(Self::SmartFanIV),
            _ => None,
        }
    }

    /// True for the modes where the chip itself regulates the fan.
    pub fn is_automatic(self) -> bool {
        !matches!(self, Self::Disabled | Self::Manual)
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FanStatus {
    pub id: String,
    pub duty_percent: u8,
    pub rpm: Option<u32>,
    /// None if pwmN_enable couldn't be read or held a value vigild doesn't know.
    pub mode: Option<PwmMode>,
    /// Reading 0 RPM for several consecutive polls while being driven.
    pub stalled: bool,
}

/// `passed` mirrors smartctl's normalized overall-health flag, which works the same way
/// for ATA and SCSI/SAS drives. Everything else is ATA-attribute-specific and is simply
/// absent for a drive that doesn't report it (SAS drives, attribute 197 on most SSDs).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriveHealth {
    /// Stable drive identity (WWN), NOT the live sdX letter.
    pub device_name: String,
    /// The by-path name of the port the drive was plugged into for this poll, if known.
    #[serde(default)]
    pub port: Option<String>,
    pub passed: Option<bool>,
    pub reallocated_sector_count: Option<u64>,
    pub pending_sector_count: Option<u64>,
    pub power_on_hours: Option<u64>,
    /// The live /dev/sdX path smartctl was run against for this read. Not stable, informational only.
    pub source_path: String,
    /// False when the drive was asleep (so deliberately not queried), or smartctl was
    /// missing, timed out, or printed something unusable.
    pub is_available: bool,
    /// When this poll ran (RFC 3339, UTC). Health is polled on a much slower cycle than
    /// the snapshot it is embedded in.
    pub as_of: String,
}

/// What ZFS says about every imported pool, from one `zpool status` poll. Polled on its own
/// cycle, like drive health, and embedded unchanged in every snapshot until the next poll.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ZfsStatus {
    /// False when zpool could not be run, timed out, or printed something that is not its JSON.
    /// `pools` is then empty, and that means "unknown", never "no pools".
    pub is_available: bool,
    /// When this poll ran (RFC 3339, UTC).
    pub as_of: String,
    pub pools: Vec<PoolStatus>,
}

/// One pool. States are zpool's own words, kept as text rather than an enum: a state a later
/// ZFS adds must reach vigil-core as "not ONLINE", not make the whole snapshot unreadable.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PoolStatus {
    pub name: String,
    /// ONLINE, DEGRADED, FAULTED, OFFLINE, REMOVED, UNAVAIL or SUSPENDED; "UNKNOWN" when zpool
    /// gave none, so a pool it could not describe is never taken for a healthy one.
    pub state: String,
    /// Permanent data errors: blocks ZFS could not repair from redundancy.
    pub error_count: Option<u64>,
    /// Raw space across every disk, parity included, as `zpool list` counts it.
    pub size_bytes: Option<u64>,
    pub allocated_bytes: Option<u64>,
    /// The latest scrub or resilver, whichever ran last. Null when the pool was never scanned.
    pub scan: Option<PoolScan>,
    /// Every leaf vdev (a disk, a partition or a file), hot spares included.
    pub vdevs: Vec<VdevStatus>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PoolScan {
    /// SCRUB, RESILVER or ERRORSCRUB.
    pub function: String,
    /// SCANNING, FINISHED or CANCELED.
    pub state: String,
    pub start_time: Option<String>,
    /// Null while the scan is running.
    pub end_time: Option<String>,
    pub to_examine_bytes: Option<u64>,
    pub examined_bytes: Option<u64>,
    /// What zpool's own progress figure counts: examined is the metadata walk, issued the I/O.
    pub issued_bytes: Option<u64>,
    /// Repaired by a scrub, or written by a resilver.
    pub processed_bytes: Option<u64>,
    pub errors: Option<u64>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VdevStatus {
    /// zpool's name for it, e.g. "wwn-0x5000c500a1b2c3d4-part1".
    pub name: String,
    /// Text, because a u64 outgrows the numbers a browser can hold exactly.
    pub guid: Option<String>,
    pub path: Option<String>,
    /// The disk it is on, named as `DriveHealth.device_name` names it, so ZFS and SMART for one
    /// disk can be put side by side. Null for a vdev that is not on a disk (a file).
    pub device_name: Option<String>,
    pub port: Option<String>,
    /// The vdev it sits in ("raidz2-0", "spare-5"), or null at the top of the pool.
    pub parent: Option<String>,
    /// normal, special, dedup, log, l2cache or spare.
    pub class: String,
    /// ONLINE, DEGRADED, FAULTED, OFFLINE, REMOVED, UNAVAIL; for a hot spare, AVAIL or INUSE.
    pub state: String,
    /// Why it is not ONLINE, in zpool's words: ERR_EXCEEDED is "faulted for too many errors".
    pub aux: Option<String>,
    pub read_errors: Option<u64>,
    pub write_errors: Option<u64>,
    pub checksum_errors: Option<u64>,
    pub slow_ios: Option<u64>,
}

/// The full read-only view of vigild's state. This is the contract consumers build on:
/// treat a field rename here as a breaking change.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    pub timestamp_utc: String,
    pub sensors: Vec<SensorReading>,
    pub fans: Vec<FanStatus>,
    pub drive_health: Vec<DriveHealth>,
    /// Null when `[zfs]` is off, before its first poll, and from a daemon that predates it.
    #[serde(default)]
    pub zfs: Option<ZfsStatus>,
    /// False when any channel couldn't be driven this poll, or (judged at read time) when
    /// this snapshot is older than the deadman timeout, i.e. the loop has hung.
    pub control_loop_healthy: bool,
}

// ---------------------------------------------------------------- time

/// Exactly three subsecond digits and a literal Z, which is the format already on the wire
/// and pinned by daemon/contract.json. `time`'s own Rfc3339 emits as many subsecond digits
/// as the value needs, so it would produce "...:27Z" for a whole second and change the
/// contract; this description does not.
const RFC3339_MILLIS: &[time::format_description::FormatItem<'_>] =
    time::macros::format_description!("[year]-[month]-[day]T[hour]:[minute]:[second].[subsecond digits:3]Z");

/// Current time as RFC 3339 UTC with millisecond precision, e.g. "2026-09-21T20:39:27.482Z".
pub fn now_rfc3339() -> String {
    format_utc(OffsetDateTime::now_utc())
}

/// Unix seconds as RFC 3339, the way every other time is on the wire. None for zero, which is
/// how ZFS says "never", and for a value no calendar holds.
pub fn rfc3339_from_unix_seconds(seconds: u64) -> Option<String> {
    let seconds = i64::try_from(seconds).ok().filter(|seconds| *seconds > 0)?;
    OffsetDateTime::from_unix_timestamp(seconds).ok().map(format_utc)
}

fn format_utc(at: OffsetDateTime) -> String {
    // The only way this fails is a format description that cannot represent the value, and
    // RFC3339_MILLIS can represent every instant OffsetDateTime holds.
    at.format(RFC3339_MILLIS).unwrap_or_else(|_| "1970-01-01T00:00:00.000Z".to_owned())
}

#[cfg(test)]
mod contract;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats_known_instants() {
        let at = |seconds: i128, millis: i128| {
            format_utc(OffsetDateTime::from_unix_timestamp_nanos((seconds * 1_000 + millis) * 1_000_000).unwrap())
        };

        assert_eq!(at(0, 0), "1970-01-01T00:00:00.000Z");
        assert_eq!(at(951_782_400, 0), "2000-02-29T00:00:00.000Z"); // leap day
        assert_eq!(at(1_790_023_167, 482), "2026-09-21T20:39:27.482Z");
        assert_eq!(at(4_102_444_799, 999), "2099-12-31T23:59:59.999Z");
    }

    #[test]
    fn unix_seconds_become_rfc3339_and_zero_means_never() {
        assert_eq!(rfc3339_from_unix_seconds(1_791_434_760).as_deref(), Some("2026-10-08T04:46:00.000Z"));
        assert_eq!(rfc3339_from_unix_seconds(0), None);
        assert_eq!(rfc3339_from_unix_seconds(u64::MAX), None);
    }

    #[test]
    fn a_snapshot_round_trips_and_tolerates_a_daemon_without_ports() {
        let json = r#"{"timestampUtc":"t","sensors":[{"id":"cpu","category":"cpu","label":"Tctl","celsiusOrNull":40.5,"sourcePath":"p","isAvailable":true}],
            "fans":[{"id":"f","dutyPercent":50,"rpm":null,"mode":"smartFanIV","stalled":false}],"driveHealth":[],"controlLoopHealthy":true}"#;
        let snapshot: Snapshot = serde_json::from_str(json).unwrap();

        assert_eq!(snapshot.sensors[0].port, None);
        assert_eq!(snapshot.zfs, None);
        assert_eq!(snapshot.fans[0].mode, Some(PwmMode::SmartFanIV));
        assert_eq!(serde_json::from_str::<Snapshot>(&serde_json::to_string(&snapshot).unwrap()).unwrap(), snapshot);
    }
}
