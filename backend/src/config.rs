use serde::{Deserialize, Serialize};
use std::path::Path;
use tracing::warn;

pub const CONFIG_PATH: &str = "/data/config.toml";

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Config {
    pub app: AppConfig,
    pub ha: HaConfig,
    pub auth: AuthConfig,
    #[serde(default)]
    pub features: FeaturesConfig,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AppConfig {
    /// Display name shown in the UI (e.g. "My Home")
    pub name: String,
    /// Theme: "dark" | "light" | "auto"
    #[serde(default = "default_theme")]
    pub theme: String,
    /// Domain used by Caddy for TLS
    #[serde(default)]
    pub domain: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct HaConfig {
    /// Full URL to the HA instance e.g. http://192.168.1.42:8123
    pub url: String,
    /// Long-lived access token
    pub token: String,
    /// Whether to verify TLS — set false for self-signed certs
    #[serde(default = "default_false")]
    pub verify_ssl: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AuthConfig {
    /// Secret used to sign JWTs — generated on first run
    pub jwt_secret: String,
    /// How many days owner sessions last
    #[serde(default = "default_session_days")]
    pub session_days: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct FeaturesConfig {
    #[serde(default = "default_true")]
    pub discovery_enabled: bool,
    #[serde(default = "default_true")]
    pub guest_access: bool,
}

fn default_theme() -> String { "dark".into() }
fn default_session_days() -> u64 { 30 }
fn default_true() -> bool { true }
fn default_false() -> bool { false }

/// Load config from CONFIG_PATH.
/// Returns None if the file doesn't exist or required fields are missing.
pub fn load() -> Option<Config> {
    let path = Path::new(CONFIG_PATH);
    if !path.exists() {
        warn!("config file not found at {}", CONFIG_PATH);
        return None;
    }
    let contents = std::fs::read_to_string(path).ok()?;
    match toml::from_str::<Config>(&contents) {
        Ok(cfg) => Some(cfg),
        Err(e) => {
            warn!("failed to parse config: {}", e);
            None
        }
    }
}

/// Write a new config to disk (called by setup wizard on completion).
pub fn write(cfg: &Config) -> anyhow::Result<()> {
    if let Some(parent) = Path::new(CONFIG_PATH).parent() {
        std::fs::create_dir_all(parent)?;
    }
    let contents = toml::to_string_pretty(cfg)?;
    std::fs::write(CONFIG_PATH, contents)?;
    Ok(())
}
