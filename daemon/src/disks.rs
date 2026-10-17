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
