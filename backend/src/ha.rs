use crate::config::HaConfig;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::collections::HashMap;
use tokio::sync::{broadcast, RwLock};
use std::sync::Arc;
use tracing::{error, info};

/// A single HA entity state
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EntityState {
    pub entity_id: String,
    pub state: String,
    pub attributes: HashMap<String, Value>,
    pub last_changed: String,
}

/// Broadcast channel message — sent to all connected WebSocket clients
#[derive(Debug, Clone, Serialize)]
pub struct StateUpdate {
    pub entity_id: String,
    pub state: String,
    pub attributes: HashMap<String, Value>,
}

#[derive(Clone)]
pub struct HaClient {
    pub config: HaConfig,
    /// In-memory entity state cache
    pub states: Arc<RwLock<HashMap<String, EntityState>>>,
    /// Broadcast channel for real-time updates to UI clients
    pub tx: broadcast::Sender<StateUpdate>,
}

impl HaClient {
    pub fn new(config: HaConfig) -> Self {
        let (tx, _) = broadcast::channel(256);
        Self {
            config,
            states: Arc::new(RwLock::new(HashMap::new())),
            tx,
        }
    }

    /// Fetch all current entity states from HA REST API and populate cache
    pub async fn fetch_all_states(&self) -> anyhow::Result<()> {
        let url = format!("{}/api/states", self.config.url);
        let client = reqwest::Client::new();
        let resp = client
            .get(&url)
            .bearer_auth(&self.config.token)
            .send()
            .await?
            .json::<Vec<EntityState>>()
            .await?;

        let mut cache = self.states.write().await;
        for entity in resp {
            cache.insert(entity.entity_id.clone(), entity);
        }
        info!("loaded {} entity states from HA", cache.len());
        Ok(())
    }

    /// Call a HA service (e.g. turn on a light)
    pub async fn call_service(
        &self,
        domain: &str,
        service: &str,
        data: Value,
    ) -> anyhow::Result<()> {
        let url = format!("{}/api/services/{}/{}", self.config.url, domain, service);
        let client = reqwest::Client::new();
        client
            .post(&url)
            .bearer_auth(&self.config.token)
            .json(&data)
            .send()
            .await?;
        Ok(())
    }

    /// Subscribe to HA WebSocket state_changed events and relay to broadcast channel.
    /// This runs as a long-lived background task.
    pub async fn subscribe_events(self) {
        let ws_url = self.config.url
            .replace("http://", "ws://")
            .replace("https://", "wss://");
        let ws_url = format!("{}/api/websocket", ws_url);

        info!("connecting to HA WebSocket at {}", ws_url);

        loop {
            match tokio_tungstenite::connect_async(&ws_url).await {
                Ok((mut ws, _)) => {
                    info!("HA WebSocket connected");
                    // Auth + subscribe handled here in the real implementation
                    // See: https://developers.home-assistant.io/docs/api/websocket
                    let _ = self.handle_ws(&mut ws).await;
                }
                Err(e) => {
                    error!("HA WebSocket connection failed: {} — retrying in 5s", e);
                }
            }
            tokio::time::sleep(std::time::Duration::from_secs(5)).await;
        }
    }

    async fn handle_ws<S>(&self, _ws: &mut S) -> anyhow::Result<()>
    where
        S: Unpin,
    {
        // Full WebSocket auth/subscribe flow implemented in next milestone.
        // Stub: keeps the loop alive without panicking.
        tokio::time::sleep(std::time::Duration::from_secs(30)).await;
        Ok(())
    }
}
