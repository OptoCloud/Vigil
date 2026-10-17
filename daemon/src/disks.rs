//! What a disk is called: its stable name and its port.
//!
//! Two keys, because there are two questions (ADR-009). The WWN
//! (/sys/class/block/{dev}/device/wwid) is burned into the disk and follows it between bays, so
//! it is what health and history are keyed on. The port, its /dev/disk/by-path name, stays with
//! the bay whatever disk sits in it, so it is what cooling is keyed on. The live sdX letter is
//! neither: it shifts across reboots and whenever a disk moves.
//!
//! Every reader of a disk (drive temperatures, SMART, ZFS vdevs) names it through here, so the
//! same disk has the same name in every part of a snapshot.

use crate::sysfs::{self, SysFs};

/// A disk's stable name: its WWN; failing that its port, which at least does not reorder
/// across reboots the way sdX does; failing both, the sdX letter itself.
pub fn stable_name(sysfs: &dyn SysFs, block_root: &str, device: &str, port: Option<&String>) -> String {
    sysfs.read(&sysfs::join(&[block_root, device, "device", "wwid"])).or_else(|| port.cloned()).unwrap_or_else(|| device.to_owned())
}

/// The by-path name for a block device: the shortest link pointing at it, partitions
/// excluded. udev can publish more than one spelling for the same port (newer versions add
/// "ata-3.0" beside "ata-3"); the shortest is the stable, historical one.
pub fn port_of(device: &str, links: &[(String, String)]) -> Option<String> {
    links
        .iter()
        .filter(|(name, target)| target == device && !name.contains("-part"))
        .map(|(name, _)| name)
        .min_by(|a, b| a.len().cmp(&b.len()).then(a.cmp(b)))
        .cloned()
}

/// One whole disk, found from a device path.
#[derive(Debug, Clone, PartialEq)]
pub struct Disk {
    /// The live kernel name ("sde"). Not stable: for running a tool against it now, never for
    /// tracking it.
    pub device: String,
    /// What `driveHealth.deviceName` and the `drive:` sensors call this disk.
    pub stable_name: String,
    pub port: Option<String>,
}

/// Turns the device paths other tools report (ZFS's vdev paths) into the disk they are on.
/// Built once per poll: it lists the by-path links when it is made.
pub struct DiskResolver<'a> {
    sysfs: &'a dyn SysFs,
    /// `/sys/block`: whole disks only, each directory holding its partitions.
    disks_root: String,
    block_root: String,
    by_path: Vec<(String, String)>,
}

impl<'a> DiskResolver<'a> {
    pub fn new(sysfs: &'a dyn SysFs, sysfs_root: &str, by_path_dir: &str) -> Self {
        Self {
            sysfs,
            disks_root: format!("{sysfs_root}/block"),
            block_root: format!("{sysfs_root}/class/block"),
            by_path: sysfs.list_links(by_path_dir),
        }
    }

    /// The disk under a path such as "/dev/disk/by-id/wwn-0x5000c500a1b2c3d4-part1", "/dev/sde1"
    /// or "/dev/sde". None when it is not a block device at all, which is what a file-backed
    /// test pool's vdevs are.
    pub fn disk_of(&self, path: &str) -> Option<Disk> {
        let (dir, name) = path.rsplit_once('/')?;
        // udev's links name the device they point at as their target's last component.
        let device =
            self.sysfs.list_links(dir).into_iter().find(|(link, _)| link == name).map_or_else(|| name.to_owned(), |(_, target)| target);
        let device = self.whole_disk(&device)?;
        let port = port_of(&device, &self.by_path);

        Some(Disk { stable_name: stable_name(self.sysfs, &self.block_root, &device, port.as_ref()), device, port })
    }

    /// The disk itself, or the disk a partition belongs to. Read from sysfs rather than by
    /// trimming digits, which gets "nvme0n1p1" and "sda1" both right only by luck. A disk's
    /// directory also holds `queue`, `power` and the like, so a partition is told apart by the
    /// `partition` file the kernel gives only partitions.
    fn whole_disk(&self, device: &str) -> Option<String> {
        let disks = self.sysfs.list_dirs(&self.disks_root);
        let partition = |disk: &String| self.sysfs.file_exists(&sysfs::join(&[disk, device, "partition"]));

        disks
            .iter()
            .find(|disk| sysfs::file_name(disk) == device)
            .or_else(|| disks.iter().find(|disk| partition(disk)))
            .map(|disk| sysfs::file_name(disk).to_owned())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::sysfs::fake::FakeSysFs;

    const BY_ID: &str = "/dev/disk/by-id";
    const BY_PATH: &str = "/dev/disk/by-path";

    /// sde: a whole disk with a WWN and one partition. nvme0n1: no wwid file, no by-path link.
    fn host() -> FakeSysFs {
        FakeSysFs::new()
            .with_file("/sys/block/sde/sde1/partition", "1")
            .with_dir("/sys/block/sde/queue")
            .with_file("/sys/class/block/sde/device/wwid", "naa.5000c500a1b2c3d4")
            .with_file("/sys/block/nvme0n1/nvme0n1p1/partition", "1")
            .with_link(BY_ID, "wwn-0x5000c500a1b2c3d4", "sde")
            .with_link(BY_ID, "wwn-0x5000c500a1b2c3d4-part1", "sde1")
            .with_link(BY_PATH, "pci-0000:03:00.0-sas-phy4-lun-0", "sde")
            .with_link(BY_PATH, "pci-0000:03:00.0-sas-phy4-lun-0-part1", "sde1")
    }

    #[test]
    fn a_partition_link_resolves_to_its_disk_and_both_its_keys() {
        let sysfs = host();
        let disks = DiskResolver::new(&sysfs, "/sys", BY_PATH);

        let expected = Disk {
            device: "sde".to_owned(),
            stable_name: "naa.5000c500a1b2c3d4".to_owned(),
            port: Some("pci-0000:03:00.0-sas-phy4-lun-0".to_owned()),
        };
        assert_eq!(disks.disk_of("/dev/disk/by-id/wwn-0x5000c500a1b2c3d4-part1"), Some(expected.clone()));
        // The same disk however it is spelt: the whole-disk link, the kernel names.
        assert_eq!(disks.disk_of("/dev/disk/by-id/wwn-0x5000c500a1b2c3d4"), Some(expected.clone()));
        assert_eq!(disks.disk_of("/dev/sde1"), Some(expected.clone()));
        assert_eq!(disks.disk_of("/dev/sde"), Some(expected));
    }

    #[test]
    fn a_disk_without_a_wwid_or_port_is_named_as_drive_health_names_it() {
        let sysfs = host();
        let disk = DiskResolver::new(&sysfs, "/sys", BY_PATH).disk_of("/dev/nvme0n1p1").unwrap();

        assert_eq!((disk.device.as_str(), disk.stable_name.as_str(), disk.port), ("nvme0n1", "nvme0n1", None));
    }

    #[test]
    fn a_path_that_is_no_block_device_is_no_disk() {
        let sysfs = host();
        let disks = DiskResolver::new(&sysfs, "/sys", BY_PATH);

        // A file-backed vdev, a dangling name, a component of a disk that is not a partition.
        for path in ["/tmp/zt1", "/dev/disk/by-id/wwn-0xgone", "/dev/queue", "sde"] {
            assert_eq!(disks.disk_of(path), None, "{path}");
        }
    }
}
