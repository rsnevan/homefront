use crate::config::Config;
use crate::db::Db;
use crate::ha::HaClient;
use std::sync::Arc;
use tokio::sync::RwLock;

/// Shared application state — cloned cheaply via Arc.
#[derive(Clone)]
pub struct AppState {
    pub inner: Arc<Inner>,
}

pub struct Inner {
    pub config: RwLock<Option<Config>>,
    pub db: Db,
    pub ha: RwLock<Option<HaClient>>,
}

impl AppState {
    pub async fn new(cfg: Option<Config>) -> anyhow::Result<Self> {
        let db = Db::new().await?;

        let ha = if let Some(ref c) = cfg {
            Some(HaClient::new(c.ha.clone()))
        } else {
            None
        };

        Ok(Self {
            inner: Arc::new(Inner {
                config: RwLock::new(cfg),
                db,
                ha: RwLock::new(ha),
            }),
        })
    }
}
