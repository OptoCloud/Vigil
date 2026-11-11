//! Drive SMART health via smartctl's JSON output.
//!
//! Always passes `-n standby`: smartctl then skips the query and returns immediately if
//! the drive is asleep, rather than spinning it up just to answer a health check. Health
//! polling must never be the thing that defeats a drive's spindown policy. It applies to the
//! whole command, so asking for the identity (`-i`) and the self-test log (`-l selftest`) in
//! the same run wakes nothing either.
//!
//! smartctl's exit code is a bitmask with several unrelated meanings (parse errors,
//! prefailure attributes, self-test failures, ...) and is deliberately not used as a
//! success signal: whether usable data came back is decided entirely by whether the JSON
//! contains "smart_status".
//!
//! This reports exactly what each poll saw and nothing more. Remembering a sleeping
//! drive's last good result, and noticing that a sector count changed since last time,
//! both need history, and history is the consumer's job.

use crate::config::DriveHealthConfig;
use crate::process;
pub use crate::protocol::DriveHealth;
use crate::sensors::ResolvedSensor;
use serde_json::Value;
use std::time::Duration;

// ATA attribute ids, as smartmontools' drivedb.h names them.
const ATA_ATTRIBUTE_REALLOCATED_SECTOR_COUNT: u64 = 5;
const ATA_ATTRIBUTE_REPORTED_UNCORRECT: u64 = 187;
const ATA_ATTRIBUTE_COMMAND_TIMEOUT: u64 = 188;
const ATA_ATTRIBUTE_CURRENT_PENDING_SECTOR_COUNT: u64 = 197;
const ATA_ATTRIBUTE_UDMA_CRC_ERROR_COUNT: u64 = 199;

/// What smartctl is asked for: identity, health, attributes and the self-test log, as JSON,
/// and nothing at all if the drive is asleep.
const SMARTCTL_ARGS: [&str; 7] = ["-i", "-H", "-A", "-l", "selftest", "-j", "-n"];

pub fn parse(stable_id: &str, json: &str, source_path: &str, as_of: &str) -> DriveHealth {
    let root: Value = serde_json::from_str(json).unwrap_or(Value::Null);

    let passed = root["smart_status"]["passed"].as_bool();

    // as_u64() is None for a negative, fractional or out-of-range number, so an odd value
    // just leaves that one field empty.
    let attribute = |wanted_id: u64| {
        root["ata_smart_attributes"]["table"]
            .as_array()?
            .iter()
            .find(|attribute| attribute["id"].as_u64() == Some(wanted_id))
            .and_then(|attribute| attribute["raw"]["value"].as_u64())
    };

    // The log is newest first. A test still running has a status but no verdict yet, so the
    // newest one that HAS a verdict is the last one that completed.
    let last_self_test = root["ata_smart_self_test_log"]["standard"]["table"]
        .as_array()
        .and_then(|tests| tests.iter().find_map(|test| Some((test["status"]["passed"].as_bool()?, test["lifetime_hours"].as_u64()))));

    DriveHealth {
        device_name: stable_id.to_owned(),
        port: None,
        passed,
        reallocated_sector_count: attribute(ATA_ATTRIBUTE_REALLOCATED_SECTOR_COUNT),
        pending_sector_count: attribute(ATA_ATTRIBUTE_CURRENT_PENDING_SECTOR_COUNT),
        power_on_hours: root["power_on_time"]["hours"].as_u64(),
        serial_number: root["serial_number"].as_str().map(str::to_owned),
        reported_uncorrect: attribute(ATA_ATTRIBUTE_REPORTED_UNCORRECT),
        command_timeout: attribute(ATA_ATTRIBUTE_COMMAND_TIMEOUT),
        udma_crc_error_count: attribute(ATA_ATTRIBUTE_UDMA_CRC_ERROR_COUNT),
        last_self_test_passed: last_self_test.map(|(passed, _)| passed),
        last_self_test_hours: last_self_test.and_then(|(_, hours)| hours),
        source_path: source_path.to_owned(),
        is_available: passed.is_some(),
        as_of: as_of.to_owned(),
    }
}

/// Polls every given drive, in parallel: one slow or hung drive then costs its own timeout
/// rather than delaying the other twelve.
pub fn poll_all(config: &DriveHealthConfig, drives: &[ResolvedSensor], as_of: &str) -> Vec<DriveHealth> {
    let timeout = Duration::from_secs_f64(config.timeout_secs);

    std::thread::scope(|scope| {
        let polls: Vec<_> = drives
            .iter()
            .filter_map(|drive| Some((drive, drive.device_name.as_deref()?)))
            .map(|(drive, device_name)| {
                scope.spawn(move || {
                    let device_path = format!("/dev/{device_name}");
                    let mut args = SMARTCTL_ARGS.to_vec();
                    args.extend(["standby", &device_path]);
                    let output = process::run(&config.smartctl_path, &args, timeout);
                    DriveHealth {
                        port: drive.port.clone(),
                        ..parse(&drive.label, output.as_ref().map_or("", |o| o.stdout.as_str()), &device_path, as_of)
                    }
                })
            })
            .collect();

        polls.into_iter().filter_map(|poll| poll.join().ok()).collect()
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    const NOW: &str = "2026-01-01T00:00:00Z";

    #[test]
    fn parses_healthy_ata_drive_with_attributes() {
        let json = r#"{
            "smart_status": { "passed": true },
            "ata_smart_attributes": { "table": [
                { "id": 5, "name": "Reallocated_Sector_Ct", "raw": { "value": 0 } },
                { "id": 197, "name": "Current_Pending_Sector", "raw": { "value": 0 } },
                { "id": 9, "name": "Power_On_Hours", "raw": { "value": 12345 } }
            ] },
            "power_on_time": { "hours": 8760 }
        }"#;

        let health = parse("naa.1", json, "/dev/sda", NOW);

        assert!(health.is_available);
        assert_eq!(health.passed, Some(true));
        assert_eq!(health.reallocated_sector_count, Some(0));
        assert_eq!(health.pending_sector_count, Some(0));
        assert_eq!(health.power_on_hours, Some(8760));
        assert_eq!((health.device_name.as_str(), health.source_path.as_str(), health.as_of.as_str()), ("naa.1", "/dev/sda", NOW));
    }

    #[test]
    fn parses_failing_drive_and_non_zero_sector_counts() {
        let json = r#"{
            "smart_status": { "passed": false },
            "ata_smart_attributes": { "table": [
                { "id": 5, "raw": { "value": 1048 } },
                { "id": 197, "raw": { "value": 8 } }
            ] }
        }"#;

        let health = parse("naa.1", json, "/dev/sda", NOW);

        assert_eq!(health.passed, Some(false));
        assert!(health.is_available);
        assert_eq!(health.reallocated_sector_count, Some(1048));
        assert_eq!(health.pending_sector_count, Some(8));
    }

    #[test]
    fn reads_the_warning_signs_the_serial_and_the_last_completed_self_test() {
        // A short test is running (status 249, no verdict yet); the extended one before it failed.
        let json = r#"{
            "serial_number": "ZDH8XK2P",
            "smart_status": { "passed": true },
            "ata_smart_attributes": { "table": [
                { "id": 187, "raw": { "value": 3 } },
                { "id": 188, "raw": { "value": 4295032833 } },
                { "id": 199, "raw": { "value": 12 } }
            ] },
            "ata_smart_self_test_log": { "standard": { "table": [
                { "type": { "string": "Short offline" }, "status": { "value": 249, "string": "Self-test routine in progress" }, "lifetime_hours": 20112 },
                { "type": { "string": "Extended offline" }, "status": { "value": 121, "passed": false }, "lifetime_hours": 20001 },
                { "type": { "string": "Short offline" }, "status": { "value": 0, "passed": true }, "lifetime_hours": 19900 }
            ] } },
            "power_on_time": { "hours": 20112 }
        }"#;

        let health = parse("naa.1", json, "/dev/sda", NOW);

        assert_eq!(health.serial_number.as_deref(), Some("ZDH8XK2P"));
        assert_eq!(
            (health.reported_uncorrect, health.command_timeout, health.udma_crc_error_count),
            (Some(3), Some(4_295_032_833), Some(12))
        );
        assert_eq!((health.last_self_test_passed, health.last_self_test_hours), (Some(false), Some(20001)));
    }

    #[test]
    fn a_drive_that_never_ran_a_self_test_has_none() {
        let json = r#"{ "smart_status": { "passed": true }, "ata_smart_self_test_log": { "standard": { "count": 0 } } }"#;

        let health = parse("naa.1", json, "/dev/sda", NOW);

        assert_eq!((health.last_self_test_passed, health.last_self_test_hours), (None, None));
        assert_eq!(health.serial_number, None);
    }

    #[test]
    fn scsi_drive_without_ata_attributes_still_reports_passed() {
        let json = r#"{ "smart_status": { "passed": true }, "power_on_time": { "hours": 100 } }"#;

        let health = parse("naa.1", json, "/dev/sdf", NOW);

        assert_eq!(health.passed, Some(true));
        assert_eq!(health.reallocated_sector_count, None);
        assert_eq!(health.power_on_hours, Some(100));
    }

    #[test]
    fn unavailable_when_smart_status_is_missing_malformed_or_empty() {
        // First case: what `-n standby` prints for a sleeping drive.
        for json in [r#"{ "smartctl": { "exit_status": 2 } }"#, "{ not json", "", r#"{ "smart_status": { "passed": "yes" } }"#] {
            let health = parse("naa.1", json, "/dev/sda", NOW);

            assert!(!health.is_available, "{json}");
            assert_eq!(health.passed, None);
        }
    }

    #[test]
    fn skips_numbers_that_do_not_fit_instead_of_failing() {
        let json = r#"{
            "smart_status": { "passed": true },
            "ata_smart_attributes": { "table": [
                { "id": 5.5, "raw": { "value": 1 } },
                { "id": 5, "raw": { "value": -1 } },
                { "id": 197, "raw": { "value": 1e40 } },
                "not an object"
            ] },
            "power_on_time": { "hours": 12.5 }
        }"#;

        let health = parse("naa.1", json, "/dev/sda", NOW);

        assert_eq!(health.passed, Some(true));
        assert_eq!(health.reallocated_sector_count, None);
        assert_eq!(health.pending_sector_count, None);
        assert_eq!(health.power_on_hours, None);
    }

    #[test]
    fn poll_all_reports_every_drive_unavailable_when_smartctl_is_missing() {
        let config = DriveHealthConfig { smartctl_path: "definitely-not-smartctl-4f1c".to_owned(), ..DriveHealthConfig::default() };
        let drive = |wwn: &str, device: Option<&str>| ResolvedSensor {
            id: format!("drive:{wwn}"),
            category: crate::sensors::SensorCategory::Drive,
            label: wwn.to_owned(),
            temp_input_path: String::new(),
            device_name: device.map(str::to_owned),
            port: None,
        };

        let results = poll_all(&config, &[drive("naa.1", Some("sda")), drive("naa.2", Some("sdb")), drive("dimm", None)], NOW);

        assert_eq!(results.len(), 2);
        assert!(results.iter().all(|r| !r.is_available));
        assert_eq!(results[1].device_name, "naa.2");
        assert_eq!(results[1].source_path, "/dev/sdb");
    }
}
