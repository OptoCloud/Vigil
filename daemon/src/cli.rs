//! vigild's command line. Strict on purpose: vigild runs as root and drives the fans, so an
//! argument it does not understand must stop it, never be ignored. `vigild --version` used to
//! load the default config and start a second daemon beside the real one.

/// What vigild was asked to do.
#[derive(Debug, PartialEq, Eq)]
pub enum Command {
    Run {
        config: String,
    },
    /// Validate the config and report what each curve would read; touches no fan.
    Check {
        config: String,
    },
    Help,
    Version,
}

pub const DEFAULT_CONFIG_PATH: &str = "/etc/vigil/vigild.toml";

/// The release tag the build was made for (release.yml sets `VIGIL_VERSION`), or the crate's
/// version marked as a development build.
pub const VERSION: &str = match option_env!("VIGIL_VERSION") {
    Some(version) => version,
    None => concat!(env!("CARGO_PKG_VERSION"), "+dev"),
};

pub const USAGE: &str = "Usage: vigild [--config PATH] [--check]\n       vigild --version\n\n  --config PATH  config file (default /etc/vigil/vigild.toml)\n  --check        validate the config and exit without touching any fan\n  --version      print the version and exit";

/// Parses the arguments after the program name.
pub fn parse(args: impl IntoIterator<Item = String>) -> Result<Command, String> {
    let mut config = None;
    let mut check = false;
    let mut args = args.into_iter();
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--help" | "-h" => return Ok(Command::Help),
            "--version" | "-V" => return Ok(Command::Version),
            "--check" => check = true,
            "--config" => match args.next() {
                Some(path) if config.is_none() => config = Some(path),
                Some(_) => return Err("--config given twice".to_owned()),
                None => return Err("--config needs a path".to_owned()),
            },
            other => return Err(format!("unknown argument '{other}'")),
        }
    }

    let config = config.unwrap_or_else(|| DEFAULT_CONFIG_PATH.to_owned());
    Ok(if check { Command::Check { config } } else { Command::Run { config } })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parse_all(args: &[&str]) -> Result<Command, String> {
        parse(args.iter().map(|&arg| arg.to_owned()))
    }

    #[test]
    fn no_arguments_runs_the_default_config() {
        assert_eq!(parse_all(&[]), Ok(Command::Run { config: DEFAULT_CONFIG_PATH.to_owned() }));
    }

    #[test]
    fn check_and_config_in_either_order() {
        let expected = Ok(Command::Check { config: "/x.toml".to_owned() });
        assert_eq!(parse_all(&["--check", "--config", "/x.toml"]), expected);
        assert_eq!(parse_all(&["--config", "/x.toml", "--check"]), expected);
    }

    #[test]
    fn version_and_help_never_run_the_daemon() {
        assert_eq!(parse_all(&["--version"]), Ok(Command::Version));
        assert_eq!(parse_all(&["--check", "-h"]), Ok(Command::Help));
    }

    /// The bug this module exists for: an unknown flag started a second daemon.
    #[test]
    fn anything_unknown_is_refused() {
        for args in [&["-v"][..], &["--chek"], &["run"], &["--config"], &["--config", "a", "--config", "b"]] {
            assert!(parse_all(args).is_err(), "{args:?} was accepted");
        }
    }
}
