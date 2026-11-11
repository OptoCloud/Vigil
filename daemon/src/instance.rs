//! One vigild at a time. Two would fight over every PWM, and the second's exit would unlink
//! the first's socket. The lock is taken before any fan or the socket is touched, and the
//! kernel drops it with the process however it ends, so a crash never leaves it stale.

use std::fs::{File, OpenOptions, TryLockError};
use std::path::Path;

/// On the tmpfs every Linux host has at /run, which the unit's ProtectSystem=full leaves writable.
pub const LOCK_PATH: &str = "/run/vigild.lock";

/// Takes the lock at `path`, or says why not. Keep the returned file open for as long as the
/// daemon runs: closing it releases the lock.
pub fn claim(path: &Path) -> Result<File, String> {
    let file = OpenOptions::new()
        .create(true)
        .truncate(false)
        .write(true)
        .open(path)
        .map_err(|e| format!("Cannot open the instance lock {}: {e}", path.display()))?;
    match file.try_lock() {
        Ok(()) => Ok(file),
        Err(TryLockError::WouldBlock) => {
            Err(format!("Another vigild is running (it holds {}). Refusing to drive the fans beside it.", path.display()))
        }
        Err(TryLockError::Error(e)) => Err(format!("Cannot lock {}: {e}", path.display())),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_second_instance_is_refused_until_the_first_exits() {
        let dir = std::env::temp_dir().join(format!("vigild-instance-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("vigild.lock");

        let first = claim(&path).unwrap();
        let second = claim(&path).unwrap_err();
        assert!(second.contains("Another vigild is running"), "{second}");

        drop(first);
        assert!(claim(&path).is_ok());
        std::fs::remove_dir_all(&dir).unwrap();
    }
}
