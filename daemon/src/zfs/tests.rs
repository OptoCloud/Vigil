//! zfs.rs's tests, in a file of their own to keep both under the ceiling (docs/STYLE.md §2.1).

use super::*;

const NOW: &str = "2026-10-08T05:00:00.000Z";
const HEALTHY: &str = include_str!("../../tests/fixtures/zfs/healthy.json");
const DEGRADED: &str = include_str!("../../tests/fixtures/zfs/degraded-spare-inuse.json");
const RESILVERING: &str = include_str!("../../tests/fixtures/zfs/resilvering.json");
const SCRUB_ERRORS: &str = include_str!("../../tests/fixtures/zfs/scrub-errors.json");

const FAULTED_DISK: &str = "ata-ST4000VN008-2DR166_ZA1B2C3D";
const IN_USE_SPARE: &str = "ata-WDC_WD40EFRX-68N32N0_WD-WX1A2B3C";
const IDLE_SPARE: &str = "ata-WDC_WD40EFRX-68N32N0_WD-WX4D5E6F";

/// Every vdev path in the fixtures is a by-id link; this names its disk the way the real
/// resolver would, after the serial at the end of the name.
fn fake_disk(path: &str) -> Option<Disk> {
    let name = path.strip_prefix("/dev/disk/by-id/")?.rsplit_once("-part")?.0;
    let serial = name.rsplit('_').next()?;
    Some(Disk { device: "sdx".to_owned(), stable_name: format!("naa.{serial}"), port: Some(format!("port-of-{serial}")) })
}

fn tank(json: &str) -> PoolStatus {
    let status = parse(json, fake_disk, NOW);
    assert!(status.is_available);
    status.pools.into_iter().find(|pool| pool.name == "tank").expect("tank is in every fixture")
}

fn vdev<'a>(pool: &'a PoolStatus, name: &str) -> &'a VdevStatus {
    pool.vdevs.iter().find(|vdev| vdev.name == name).unwrap_or_else(|| panic!("no vdev {name}"))
}

#[test]
fn answers_whatever_zpool_prints_in_place_of_any_field() {
    // The fixture with the most in it: a spare in use, a faulted disk, a resilver running.
    for variant in crate::mangle::json_variants(DEGRADED) {
        // No panic is the test. And a pool that is listed is never without a state: one
        // that could not be read must not pass for ONLINE.
        let status = parse(&variant, fake_disk, NOW);

        assert_eq!(status.as_of, NOW);
        assert!(status.pools.iter().all(|pool| !pool.name.is_empty()), "{variant}");
        if !status.is_available {
            assert!(status.pools.is_empty(), "unavailable means unknown, not a list: {variant}");
        }
    }
}

#[test]
fn a_healthy_host_reads_as_every_pool_online_with_its_disks_named_as_smart_names_them() {
    let status = parse(HEALTHY, fake_disk, NOW);

    assert_eq!((status.is_available, status.as_of.as_str()), (true, NOW));
    assert_eq!(
        status.pools.iter().map(|pool| (pool.name.as_str(), pool.state.as_str())).collect::<Vec<_>>(),
        [("rpool", "ONLINE"), ("tank", "ONLINE")]
    );

    let tank = &status.pools[1];
    assert_eq!((tank.size_bytes, tank.allocated_bytes, tank.error_count), (Some(32_000_000_000_000), Some(12_940_000_000_000), Some(0)));
    // Eight members and two spares: the leaves, never raidz2-0 or the root itself.
    assert_eq!(tank.vdevs.len(), 10);
    assert!(tank.vdevs.iter().filter(|vdev| vdev.class == "normal").all(|vdev| vdev.parent.as_deref() == Some("raidz2-0")));

    let disk = vdev(tank, FAULTED_DISK);
    assert_eq!(disk.device_name.as_deref(), Some("naa.ZA1B2C3D"));
    assert_eq!(disk.port.as_deref(), Some("port-of-ZA1B2C3D"));
    assert_eq!(disk.guid.as_deref(), Some("9000000000000001106"));
    assert_eq!((disk.read_errors, disk.write_errors, disk.checksum_errors, disk.slow_ios), (Some(0), Some(0), Some(0), Some(0)));
    assert_eq!(disk.aux, None);

    let rpool = &status.pools[0];
    assert!(rpool.vdevs.iter().all(|vdev| vdev.parent.as_deref() == Some("mirror-0") && vdev.state == "ONLINE"));
}

#[test]
fn idle_spares_are_available_and_carry_no_error_counts() {
    let tank = tank(HEALTHY);

    for name in [IN_USE_SPARE, IDLE_SPARE] {
        let spare = vdev(&tank, name);
        assert_eq!((spare.class.as_str(), spare.state.as_str(), spare.parent.as_deref()), ("spare", "AVAIL", None));
        assert_eq!(spare.read_errors, None, "zpool keeps no counts for an idle spare");
    }
}

#[test]
fn the_night_of_the_fault_reads_as_a_faulted_disk_and_a_spare_in_use() {
    let tank = tank(DEGRADED);

    assert_eq!(tank.state, "DEGRADED");
    let faulted = vdev(&tank, FAULTED_DISK);
    assert_eq!((faulted.state.as_str(), faulted.aux.as_deref(), faulted.read_errors), ("FAULTED", Some("ERR_EXCEEDED"), Some(14)));
    assert_eq!(faulted.parent.as_deref(), Some("spare-5"));

    // Listed once, though zpool lists it twice: in the tree, with its spare state.
    let spare = vdev(&tank, IN_USE_SPARE);
    assert_eq!(tank.vdevs.iter().filter(|vdev| vdev.name == IN_USE_SPARE).count(), 1);
    assert_eq!((spare.state.as_str(), spare.class.as_str(), spare.parent.as_deref()), ("INUSE", "spare", Some("spare-5")));
    assert_eq!(spare.read_errors, Some(0), "an in-use spare keeps the counts the tree gives it");
    assert_eq!(vdev(&tank, IDLE_SPARE).state, "AVAIL");

    let scan = tank.scan.expect("the resilver");
    assert_eq!((scan.function.as_str(), scan.state.as_str(), scan.errors), ("RESILVER", "FINISHED", Some(0)));
    assert_eq!(scan.start_time.as_deref(), Some("2026-10-08T04:46:54.000Z"));
    assert_eq!(scan.end_time.as_deref(), Some("2026-10-08T09:10:54.000Z"));
    assert_eq!(scan.processed_bytes, Some(1_539_316_278_886));
}

#[test]
fn a_running_resilver_has_progress_and_no_end() {
    let scan = tank(RESILVERING).scan.expect("the resilver");

    assert_eq!((scan.state.as_str(), scan.end_time), ("SCANNING", None));
    assert_eq!((scan.issued_bytes, scan.to_examine_bytes), (Some(980_000_000_000), Some(12_940_000_000_000)));
}

#[test]
fn a_scrub_that_found_damage_shows_it_on_the_pool_and_on_the_disk() {
    let tank = tank(SCRUB_ERRORS);

    assert_eq!((tank.state.as_str(), tank.error_count), ("ONLINE", Some(2)));
    assert_eq!(tank.scan.as_ref().and_then(|scan| scan.errors), Some(3));
    assert_eq!(vdev(&tank, "ata-ST4000VN008-2DR166_ZA1B2C03").checksum_errors, Some(3));
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
    assert_eq!(log.update(&parse(DEGRADED, fake_disk, NOW)), [warning("Pool tank is DEGRADED.")]);
    assert_eq!(log.update(&parse(RESILVERING, fake_disk, NOW)), []);
    assert_eq!(log.update(&unreadable), [warning("zpool status gave no usable answer: pool state is unknown.")]);
    assert_eq!(log.update(&unreadable), []);
    // Still DEGRADED once zpool answers again, so only the readability is news.
    assert_eq!(log.update(&parse(DEGRADED, fake_disk, NOW)), [cleared("zpool status answers again.")]);
    assert_eq!(log.update(&parse(HEALTHY, fake_disk, NOW)), [cleared("Pool tank is ONLINE again.")]);
}
