use sqlx::{SqlitePool, sqlite::SqliteConnectOptions};
use std::str::FromStr;
use tracing::info;

pub const DB_PATH: &str = "/data/homefront.db";

#[derive(Clone)]
pub struct Db {
    pub pool: SqlitePool,
}

impl Db {
    pub async fn new() -> anyhow::Result<Self> {
        // Ensure /data exists
        std::fs::create_dir_all("/data").ok();

        let opts = SqliteConnectOptions::from_str(&format!("sqlite:{}", DB_PATH))?
            .create_if_missing(true);

        let pool = SqlitePool::connect_with(opts).await?;
        info!("database connected at {}", DB_PATH);

        // Run migrations
        sqlx::migrate!("./migrations").run(&pool).await?;

        Ok(Self { pool })
    }
}

// --- User model ---

#[derive(Debug, sqlx::FromRow)]
pub struct User {
    pub id: String,
    pub username: String,
    pub display_name: String,
    pub password_hash: String,
    pub role: String,         // "owner" | "guest"
    pub enabled: bool,
    pub expires_at: Option<i64>, // Unix timestamp, None = never expires
    pub created_at: i64,
}
