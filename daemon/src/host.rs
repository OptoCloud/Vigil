//! What the kernel says about the host's load: memory and the ZFS ARC, CPU time, load,
//! pressure, uptime, and the traffic counters of disks and named interfaces (ADR-025).
//!
//! The parsers are pure functions over the text of one `/proc` file each, the way `zfs.rs`
//! parses `zpool`'s JSON. `read` puts them together through the `SysFs` boundary, so tests
//! serve the files from `FakeSysFs`. Nothing here keeps a previous reading: counters go out as
//! the kernel counts them, and vigil-core turns two of them into a rate.
//!
//! None of these files touch a disk, so reading them every poll never wakes a sleeping drive.
//!
//! Over the 400-line ceiling (STYLE.md §2.1) because half of it is the tests, each a kernel
//! layout or a broken file that must not be misread. The parsers are one job: splitting them
//! per file would scatter seven small functions with one caller each.

use crate::conditions::ConditionLog;
use crate::config::HostConfig;
use crate::disks::DiskResolver;
use crate::protocol::{CpuTicks, DiskCounters, HostStatus, InterfaceCounters, LoadAverage, MemoryStatus, Pressure, PressureStall};
use crate::sysfs::{self, SysFs};
use tracing::{info, warn};

/// The most any one `/proc` file may hold before it is refused. `/proc/diskstats` is the
/// only one that grows, by about 150 bytes per block device, and a Proxmox host's zvols and
/// their partitions are all block devices; 1 MiB is several thousand of them. The same cap
/// as a child process's output (`process.rs`).
const PROC_READ_LIMIT: usize = 1024 * 1024;

/// `/proc/diskstats` counts in 512-byte sectors whatever the disk's own sector size
/// (the kernel's Documentation/admin-guide/iostats.rst, "sectors read").
const DISKSTATS_SECTOR_BYTES: u64 = 512;

/// `/proc/meminfo` says "kB" and means KiB.
const MEMINFO_UNIT_BYTES: u64 = 1024;

/// Reads the host once. Paths come from config: `proc_root` for `/proc`, and the resolver for
/// what each disk is called.
pub fn read(proc: &dyn SysFs, disks: &DiskResolver<'_>, config: &HostConfig, as_of: &str) -> HostStatus {
    let file = |name: &str| proc.read_at_most(&sysfs::join(&[&config.proc_root, name]), PROC_READ_LIMIT);
    let arc = file("spl/kstat/zfs/arcstats").map(|text| parse_arcstats(&text)).unwrap_or_default();
    let pressure = |name: &str| file(&format!("pressure/{name}")).and_then(|text| parse_pressure(&text));
    let pressure = Pressure { cpu: pressure("cpu"), memory: pressure("memory"), io: pressure("io") };

    HostStatus {
        as_of: as_of.to_owned(),
        uptime_seconds: file("uptime").and_then(|text| parse_uptime(&text)),
        memory: file("meminfo").and_then(|text| parse_meminfo(&text)).map(|memory| MemoryStatus {
            arc_bytes: arc.size,
            arc_max_bytes: arc.max,
            ..memory
        }),
        cpu: file("stat").and_then(|text| parse_cpu(&text)),
        load: file("loadavg").and_then(|text| parse_loadavg(&text)),
        pressure: (pressure.cpu.is_some() || pressure.memory.is_some() || pressure.io.is_some()).then_some(pressure),
        disks: file("diskstats").map(|text| disk_counters(&text, disks)).unwrap_or_default(),
        interfaces: file("net/dev").map(|text| interface_counters(&text, &config.interfaces)).unwrap_or_default(),
    }
}

/// Logs a part of the host that stopped being readable, once, and again when it is back.
/// A host without ZFS or PSI is not a problem, so only what every Linux has is watched, and
/// the interfaces config names.
pub fn log_changes(log: &mut ConditionLog, status: &HostStatus, config: &HostConfig) {
    let parts = [("meminfo", status.memory.is_none()), ("stat", status.cpu.is_none()), ("loadavg", status.load.is_none())];
    for (part, missing) in parts {
        if log.changed(&format!("host-unreadable:{part}"), missing) {
            if missing {
                warn!("{}/{part} could not be read or parsed; it is left out of the snapshot until it can.", config.proc_root);
            } else {
                info!("{}/{part} is readable again.", config.proc_root);
            }
        }
    }

    for name in &config.interfaces {
        let missing = !status.interfaces.iter().any(|interface| &interface.name == name);
        if log.changed(&format!("host-interface-missing:{name}"), missing) {
            if missing {
                warn!("Interface '{name}' from [host] interfaces is not in {}/net/dev.", config.proc_root);
            } else {
                info!("Interface '{name}' is back.");
            }
        }
    }
}

/// None when any of the fields every kernel since 3.14 writes is missing.
fn parse_meminfo(text: &str) -> Option<MemoryStatus> {
    let field = |name: &str| {
        text.lines().find_map(|line| {
            let (key, rest) = line.split_once(':')?;
            let kib: u64 = rest.split_whitespace().next()?.parse().ok()?;
            (key == name).then(|| kib.saturating_mul(MEMINFO_UNIT_BYTES))
        })
    };

    Some(MemoryStatus {
        total_bytes: field("MemTotal")?,
        available_bytes: field("MemAvailable")?,
        free_bytes: field("MemFree")?,
        buffers_bytes: field("Buffers")?,
        cached_bytes: field("Cached")?.saturating_add(field("SReclaimable").unwrap_or(0)),
        swap_total_bytes: field("SwapTotal")?,
        swap_free_bytes: field("SwapFree")?,
        arc_bytes: None,
        arc_max_bytes: None,
    })
}

#[derive(Debug, Default, PartialEq)]
struct ArcSize {
    size: Option<u64>,
    max: Option<u64>,
}

/// `name type data` rows after a two-line header.
fn parse_arcstats(text: &str) -> ArcSize {
    let field = |name: &str| {
        text.lines().find_map(|line| {
            let mut columns = line.split_whitespace();
            (columns.next()? == name).then_some(())?;
            columns.nth(1)?.parse().ok()
        })
    };
    ArcSize { size: field("size"), max: field("c_max") }
}

/// The aggregate `cpu` line. Its first seven columns are older than any kernel vigild runs
/// on; steal arrived in 2.6.11 and is read as zero if absent. Guest time is already counted
/// in user and nice, so the columns after steal are not read.
fn parse_cpu(text: &str) -> Option<CpuTicks> {
    let line = text.lines().find(|line| line.split_whitespace().next() == Some("cpu"))?;
    let ticks: Vec<u64> = line.split_whitespace().skip(1).map(str::parse).collect::<Result<_, _>>().ok()?;
    let tick = |index: usize| ticks.get(index).copied();
    let logical_cpus =
        text.lines().filter(|line| line.strip_prefix("cpu").is_some_and(|rest| rest.starts_with(|c: char| c.is_ascii_digit()))).count();

    Some(CpuTicks {
        user: tick(0)?,
        nice: tick(1)?,
        system: tick(2)?,
        idle: tick(3)?,
        iowait: tick(4)?,
        irq: tick(5)?,
        softirq: tick(6)?,
        steal: tick(7).unwrap_or(0),
        logical_cpus: u32::try_from(logical_cpus).ok()?,
    })
}

fn parse_loadavg(text: &str) -> Option<LoadAverage> {
    let mut columns = text.split_whitespace().map(|column| column.parse::<f64>().ok().filter(|value| value.is_finite() && *value >= 0.0));
    Some(LoadAverage { one: columns.next()??, five: columns.next()??, fifteen: columns.next()?? })
}

fn parse_uptime(text: &str) -> Option<f64> {
    text.split_whitespace().next()?.parse().ok().filter(|seconds: &f64| seconds.is_finite() && *seconds >= 0.0)
}

/// `some avg10=0.00 avg60=0.00 avg300=0.00 total=12345`, then the same for `full`.
fn parse_pressure(text: &str) -> Option<PressureStall> {
    let total = |kind: &str| {
        text.lines().find_map(|line| {
            let mut columns = line.split_whitespace();
            (columns.next()? == kind).then_some(())?;
            columns.find_map(|column| column.strip_prefix("total="))?.parse().ok()
        })
    };
    Some(PressureStall { some_micros: total("some")?, full_micros: total("full") })
}

/// Columns: major, minor, name, then reads, reads merged, sectors read, ms reading, writes,
/// writes merged, sectors written, ms writing, in flight, ms doing I/O, and more after.
fn disk_counters(text: &str, disks: &DiskResolver<'_>) -> Vec<DiskCounters> {
    text.lines()
        .filter_map(|line| {
            let columns: Vec<&str> = line.split_whitespace().collect();
            let counter = |index: usize| columns.get(index)?.parse::<u64>().ok();
            let (sectors_read, sectors_written, busy_millis) = (counter(5)?, counter(9)?, counter(12)?);
            let disk = disks.hardware_disk(columns.get(2)?)?;

            Some(DiskCounters {
                device_name: disk.stable_name,
                port: disk.port,
                read_bytes: sectors_read.saturating_mul(DISKSTATS_SECTOR_BYTES),
                written_bytes: sectors_written.saturating_mul(DISKSTATS_SECTOR_BYTES),
                busy_millis,
            })
        })
        .collect()
}

/// Two header lines, then `name: rx bytes, packets, errs, drop, fifo, frame, compressed,
/// multicast, tx bytes, ...`. Old kernels write no space after the colon, so the name is split
/// off at the colon rather than by whitespace.
fn interface_counters(text: &str, wanted: &[String]) -> Vec<InterfaceCounters> {
    let mut found: Vec<InterfaceCounters> = text
        .lines()
        .filter_map(|line| {
            let (name, rest) = line.split_once(':')?;
            let name = name.trim();
            if !wanted.iter().any(|wanted| wanted == name) {
                return None;
            }
            let columns: Vec<u64> = rest.split_whitespace().map(str::parse).collect::<Result<_, _>>().ok()?;
            Some(InterfaceCounters { name: name.to_owned(), received_bytes: *columns.first()?, transmitted_bytes: *columns.get(8)? })
        })
        .collect();

    found.sort_by_key(|interface| wanted.iter().position(|name| name == &interface.name));
    found
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::sysfs::fake::FakeSysFs;

    // In the kernel's own layout: the stat, pressure and net/dev headers are copied from a
    // Linux 7.3 host, the rest written to match, with the values chosen to be told apart.
    const MEMINFO: &str = "MemTotal:       65777012 kB
MemFree:         2048000 kB
MemAvailable:   20480000 kB
Buffers:          102400 kB
Cached:         10240000 kB
SwapCached:            0 kB
SwapTotal:       8388604 kB
SwapFree:        8388000 kB
SReclaimable:     512000 kB
HugePages_Total:       0";

    const ARCSTATS: &str = "9 1 0x01 147 39984 3975934217 1095381208441
name                            type data
hits                            4    123456789
size                            4    34359738368
c                               4    34359738368
c_max                           4    33678233600";

    const STAT: &str = "cpu  14199 8798 13471 547156 1626 763 566 0 0 0
cpu0 1185 767 1144 45377 128 83 98 0 0 0
cpu1 1427 756 1076 45306 139 53 62 0 0 0
intr 1234 0 0
ctxt 99999
btime 1790000000";

    const PRESSURE_IO: &str = "some avg10=0.00 avg60=0.02 avg300=0.07 total=2132541
full avg10=0.00 avg60=0.02 avg300=0.06 total=1901145";

    const DISKSTATS: &str = "   8       0 sda 9000 10 4000000 300 8000 20 6000000 900 0 1500 1200 0 0 0 0 0 0
   8       1 sda1 8000 10 3900000 290 7900 20 5900000 890 0 1400 1180 0 0 0 0 0 0
 230       0 zd0 100 0 2000 10 100 0 2000 10 0 20 20 0 0 0 0
   7       0 loop0 5 0 10 0 0 0 0 0 0 1 0 0 0 0 0
 259       0 nvme0n1 160 0 5752 39 0 0 0 0 0 29 39";

    const NET_DEV: &str = "Inter-|   Receive                                                |  Transmit
 face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
    lo:  153982    2591    0    0    0     0          0         0   153982    2591    0    0    0     0       0          0
 vmbr0: 9876543210 100 0 0 0 0 0 0 1234567890 90 0 0 0 0 0 0
tap100i0:5000 10 0 0 0 0 0 0 6000 12 0 0 0 0 0 0
enp5s0:  42 1 0 0 0 0 0 0 43 1 0 0 0 0 0 0";

    fn config(interfaces: &[&str]) -> HostConfig {
        HostConfig { interfaces: interfaces.iter().map(|name| (*name).to_owned()).collect(), ..HostConfig::default() }
    }

    fn host() -> FakeSysFs {
        FakeSysFs::new()
            .with_file("/proc/meminfo", MEMINFO)
            .with_file("/proc/spl/kstat/zfs/arcstats", ARCSTATS)
            .with_file("/proc/stat", STAT)
            .with_file("/proc/loadavg", "0.46 0.66 0.36 1/1159 26639")
            .with_file("/proc/uptime", "487.25 5471.57")
            .with_file("/proc/pressure/cpu", "some avg10=0.01 avg60=0.03 avg300=0.06 total=2161596")
            .with_file("/proc/pressure/io", PRESSURE_IO)
            .with_file("/proc/diskstats", DISKSTATS)
            .with_file("/proc/net/dev", NET_DEV)
            .with_dir("/sys/block/sda/device")
            .with_file("/sys/block/sda/sda1/partition", "1")
            .with_file("/sys/class/block/sda/device/wwid", "naa.5000c500a1b2c3d4")
            .with_dir("/sys/block/nvme0n1/device")
            .with_dir("/sys/block/zd0/queue")
            .with_dir("/sys/block/loop0/loop")
            .with_link("/dev/disk/by-path", "pci-0000:01:00.1-ata-3", "sda")
    }

    #[test]
    fn answers_whatever_proc_holds_in_place_of_any_file() {
        // /proc is the kernel's, and a newer kernel may lay a file out differently: every
        // file here, cut short, missing a line, or with a number that is not one.
        let files = [
            ("/proc/meminfo", MEMINFO),
            ("/proc/spl/kstat/zfs/arcstats", ARCSTATS),
            ("/proc/stat", STAT),
            ("/proc/loadavg", "0.46 0.66 0.36 1/1159 26639"),
            ("/proc/uptime", "487.25 5471.57"),
            ("/proc/pressure/io", PRESSURE_IO),
            ("/proc/diskstats", DISKSTATS),
            ("/proc/net/dev", NET_DEV),
        ];

        for (path, original) in files {
            for variant in crate::mangle::text_variants(original) {
                let sysfs = host().with_file(path, &variant);
                let disks = DiskResolver::new(&sysfs, "/sys", "/dev/disk/by-path");

                // No panic is the test; what was not touched is still read.
                let status = read(&sysfs, &disks, &config(&["vmbr0"]), "2026-11-19T12:00:00.000Z");

                assert_eq!(status.as_of, "2026-11-19T12:00:00.000Z");
                if path != "/proc/loadavg" {
                    assert_eq!(status.load, Some(LoadAverage { one: 0.46, five: 0.66, fifteen: 0.36 }), "{path}: {variant:?}");
                }
            }
        }
    }

    #[test]
    fn reads_every_part_of_the_host() {
        let sysfs = host();
        let disks = DiskResolver::new(&sysfs, "/sys", "/dev/disk/by-path");
        let status = read(&sysfs, &disks, &config(&["enp5s0", "vmbr0"]), "2026-11-19T12:00:00.000Z");

        let memory = status.memory.unwrap();
        assert_eq!(memory.total_bytes, 65_777_012 * 1024);
        assert_eq!(memory.available_bytes, 20_480_000 * 1024);
        assert_eq!(memory.cached_bytes, (10_240_000 + 512_000) * 1024, "cache counts SReclaimable, as free does");
        assert_eq!(memory.swap_free_bytes, 8_388_000 * 1024);
        assert_eq!((memory.arc_bytes, memory.arc_max_bytes), (Some(34_359_738_368), Some(33_678_233_600)));

        let cpu = status.cpu.unwrap();
        assert_eq!((cpu.user, cpu.idle, cpu.iowait, cpu.steal, cpu.logical_cpus), (14_199, 547_156, 1_626, 0, 2));
        assert_eq!(status.load, Some(LoadAverage { one: 0.46, five: 0.66, fifteen: 0.36 }));
        assert_eq!(status.uptime_seconds, Some(487.25));

        let pressure = status.pressure.unwrap();
        assert_eq!(pressure.cpu, Some(PressureStall { some_micros: 2_161_596, full_micros: None }));
        assert_eq!(pressure.io, Some(PressureStall { some_micros: 2_132_541, full_micros: Some(1_901_145) }));
        assert_eq!(pressure.memory, None);
    }

    #[test]
    fn disks_are_whole_hardware_disks_named_as_drive_health_names_them() {
        let sysfs = host();
        let disks = DiskResolver::new(&sysfs, "/sys", "/dev/disk/by-path");
        let status = read(&sysfs, &disks, &config(&[]), "t");

        // sda1 (a partition), zd0 (a zvol) and loop0 are dropped.
        assert_eq!(
            status.disks,
            vec![
                DiskCounters {
                    device_name: "naa.5000c500a1b2c3d4".to_owned(),
                    port: Some("pci-0000:01:00.1-ata-3".to_owned()),
                    read_bytes: 4_000_000 * 512,
                    written_bytes: 6_000_000 * 512,
                    busy_millis: 1_500,
                },
                DiskCounters { device_name: "nvme0n1".to_owned(), port: None, read_bytes: 5_752 * 512, written_bytes: 0, busy_millis: 29 },
            ]
        );
    }

    #[test]
    fn only_the_named_interfaces_are_published_in_config_order() {
        let interfaces = interface_counters(NET_DEV, &["enp5s0".to_owned(), "vmbr0".to_owned(), "eth9".to_owned()]);

        assert_eq!(
            interfaces,
            vec![
                InterfaceCounters { name: "enp5s0".to_owned(), received_bytes: 42, transmitted_bytes: 43 },
                InterfaceCounters { name: "vmbr0".to_owned(), received_bytes: 9_876_543_210, transmitted_bytes: 1_234_567_890 },
            ]
        );
        // A guest's tap device, written the old way with no space after the colon, only when asked for.
        assert_eq!(interface_counters(NET_DEV, &[]), vec![]);
        assert_eq!(interface_counters(NET_DEV, &["tap100i0".to_owned()])[0].transmitted_bytes, 6_000);
    }

    #[test]
    fn a_host_without_zfs_or_psi_still_reports_the_rest() {
        let sysfs = host();
        sysfs.remove("/proc/spl/kstat/zfs/arcstats");
        sysfs.remove("/proc/pressure/cpu");
        sysfs.remove("/proc/pressure/io");
        let disks = DiskResolver::new(&sysfs, "/sys", "/dev/disk/by-path");
        let status = read(&sysfs, &disks, &config(&[]), "t");

        let memory = status.memory.unwrap();
        assert_eq!((memory.arc_bytes, memory.arc_max_bytes), (None, None));
        assert_eq!(status.pressure, None);
        assert!(status.cpu.is_some() && status.load.is_some());
    }

    #[test]
    fn a_file_that_does_not_parse_leaves_only_its_own_part_null() {
        let sysfs = host();
        sysfs.set("/proc/meminfo", "MemTotal: 1 kB\nMemFree: 1 kB"); // truncated before MemAvailable
        sysfs.set("/proc/stat", "cpu  1 2 three 4 5 6 7");
        sysfs.set("/proc/loadavg", "0.5 NaN 0.1 1/2 3");
        sysfs.set("/proc/uptime", "");
        let disks = DiskResolver::new(&sysfs, "/sys", "/dev/disk/by-path");
        let status = read(&sysfs, &disks, &config(&[]), "t");

        assert_eq!((status.memory, status.cpu, status.load, status.uptime_seconds), (None, None, None, None));
        assert!(status.pressure.is_some());
        assert_eq!(status.disks.len(), 2);
    }

    #[test]
    fn truncated_rows_are_skipped_not_misread() {
        let sysfs = host();
        let disks = DiskResolver::new(&sysfs, "/sys", "/dev/disk/by-path");

        assert_eq!(disk_counters("   8       0 sda 9000 10 4000000", &disks), vec![]);
        assert_eq!(interface_counters("vmbr0: 1 2 3", &["vmbr0".to_owned()]), vec![]);
        assert_eq!(parse_pressure("some avg10=0.00"), None);
        assert_eq!(parse_arcstats("name type data\nsize 4 x"), ArcSize::default());
        // An old kernel's cpu line without steal still parses.
        assert_eq!(parse_cpu("cpu  1 2 3 4 5 6 7\ncpu0 1 2 3 4 5 6 7").map(|cpu| (cpu.steal, cpu.logical_cpus)), Some((0, 1)));
    }

    #[test]
    fn a_file_over_the_read_limit_is_refused() {
        let sysfs = host();
        sysfs.set("/proc/diskstats", &"x".repeat(PROC_READ_LIMIT + 1));
        let disks = DiskResolver::new(&sysfs, "/sys", "/dev/disk/by-path");

        assert_eq!(read(&sysfs, &disks, &config(&[]), "t").disks, vec![]);
    }

    #[test]
    fn a_missing_part_and_interface_are_logged_once_each_way() {
        let config = config(&["vmbr0"]);
        let mut log = ConditionLog::default();
        let healthy = HostStatus {
            as_of: "t".to_owned(),
            uptime_seconds: None,
            memory: parse_meminfo(MEMINFO),
            cpu: parse_cpu(STAT),
            load: parse_loadavg("1 1 1"),
            pressure: None,
            disks: vec![],
            interfaces: interface_counters(NET_DEV, &config.interfaces),
        };
        let broken = HostStatus { memory: None, interfaces: vec![], ..healthy.clone() };

        log_changes(&mut log, &healthy, &config);
        log_changes(&mut log, &broken, &config);
        assert!(log.is_active("host-unreadable:meminfo") && log.is_active("host-interface-missing:vmbr0"));
        log_changes(&mut log, &healthy, &config);
        assert!(!log.is_active("host-unreadable:meminfo") && !log.is_active("host-interface-missing:vmbr0"));
    }
}
