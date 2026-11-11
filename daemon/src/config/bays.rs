//! `[[bays]]`: where each port is in the case, so the page can say which disk to pull.
//!
//! Nothing here changes hardware: a wrong bay misleads a person, not a fan. It is still
//! validated at startup with everything else, because a mislabelled bay is the one mistake
//! that gets a healthy disk pulled out of a degraded pool.

use super::BayConfig;
use std::collections::HashSet;

/// Every problem with the bays, appended to `errors`.
pub(super) fn validate(bays: &[BayConfig], errors: &mut Vec<String>) {
    let mut ports = HashSet::new();
    let mut slots = HashSet::new();

    for bay in bays {
        let name = format!("Bay {} {}", bay.cage, bay.slot);
        if bay.port.trim().is_empty() {
            errors.push(format!("{name}: port must not be empty."));
        } else if bay.port.starts_with("port:") {
            // Zones write "port:" because their members are sensor ids too; a bay holds only a port.
            errors.push(format!("{name}: port is the by-path name alone; drop the \"port:\" prefix ({}).", bay.port));
        } else if super::is_wildcard(&bay.port) {
            errors.push(format!("{name}: port must name one port, not a wildcard ({}).", bay.port));
        } else if !ports.insert(bay.port.as_str()) {
            errors.push(format!("Port '{}' is in more than one bay.", bay.port));
        }

        if bay.cage.trim().is_empty() {
            errors.push(format!("The bay on port '{}' has an empty cage.", bay.port));
        }

        if bay.slot < 1 {
            errors.push(format!("{name}: slot numbers start at 1, as they are printed on the cage."));
        } else if !slots.insert((bay.cage.as_str(), bay.slot)) {
            errors.push(format!("{name} is listed twice."));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn bay(port: &str, cage: &str, slot: u32) -> BayConfig {
        BayConfig { port: port.to_owned(), cage: cage.to_owned(), slot }
    }

    fn problems(bays: &[BayConfig]) -> Vec<String> {
        let mut errors = Vec::new();
        validate(bays, &mut errors);
        errors
    }

    /// The deployed bays, pinned: the spares are where `zpool status` and the cage say they
    /// are, so the page never sends anyone to the wrong slot.
    #[test]
    fn the_orion_config_names_all_twelve_bays() {
        let config = crate::config::Config::parse(include_str!("../../../deploy/vigild.toml")).unwrap();
        let slot = |port: &str| config.bays.iter().find(|bay| bay.port == port).map(|bay| (bay.cage.as_str(), bay.slot));

        assert_eq!(config.bays.len(), 12);
        assert!(problems(&config.bays).is_empty());
        assert_eq!(slot("pci-0000:03:00.0-sas-phy0-lun-0"), Some(("8-bay", 1)));
        assert_eq!(slot("pci-0000:03:00.0-sas-phy7-lun-0"), Some(("8-bay", 8)));
        assert_eq!(slot("pci-0000:01:00.1-ata-4"), Some(("4-bay", 2)));
        assert_eq!(slot("pci-0000:01:00.1-ata-6"), Some(("4-bay", 3)));
        assert_eq!(slot("pci-0000:01:00.1-ata-5"), Some(("4-bay", 4)));
    }

    #[test]
    fn accepts_distinct_ports_and_slots() {
        assert!(problems(&[bay("pci-0000:01:00.1-ata-3", "4-bay", 1), bay("pci-0000:01:00.1-ata-4", "4-bay", 2)]).is_empty());
    }

    #[test]
    fn rejects_a_port_or_a_slot_used_twice() {
        let errors = problems(&[bay("pci-0000:01:00.1-ata-3", "4-bay", 1), bay("pci-0000:01:00.1-ata-3", "4-bay", 1)]);

        assert_eq!(errors, ["Port 'pci-0000:01:00.1-ata-3' is in more than one bay.", "Bay 4-bay 1 is listed twice."]);
    }

    #[test]
    fn rejects_what_a_zone_would_accept_but_a_bay_cannot_mean() {
        let errors = problems(&[
            bay("port:pci-0000:01:00.1-ata-3", "4-bay", 1),
            bay("pci-0000:03:00.0-sas-*", "8-bay", 1),
            bay("", "8-bay", 2),
            bay("pci-0000:01:00.1-ata-6", " ", 0),
        ]);

        assert_eq!(errors.len(), 5, "{errors:?}");
        assert!(errors[0].contains("drop the \"port:\" prefix"));
        assert!(errors[1].contains("not a wildcard"));
    }
}
