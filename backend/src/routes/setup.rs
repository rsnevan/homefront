use crate::state::AppState;
use axum::{extract::State, routing::post, Json, Router};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

pub fn router(state: AppState) -> Router {
    Router::new()
        .route("/setup/discover", post(discover))
        .route("/setup/test-connection", post(test_connection))
        .route("/setup/complete", post(complete))
        .with_state(state)
}

/// Step 1 — mDNS discovery (stub, full impl in next milestone)
async fn discover() -> Json<Value> {
    // TODO: fire mDNS scan for _home-assistant._tcp.local
    // and subnet sweep on port 8123
    Json(json!({
        "instances": [
            { "name": "Home Assistant", "url": "http://192.168.1.42:8123" }
        ]
    }))
}

#[derive(Deserialize)]
struct TestConnectionRequest {
    url: String,
    token: String,
}

#[derive(Serialize)]
struct TestConnectionResponse {
    ok: bool,
    version: Option<String>,
    error: Option<String>,
}

/// Step 2 — verify HA URL + token
async fn test_connection(
    Json(req): Json<TestConnectionRequest>,
) -> Json<TestConnectionResponse> {
    let client = reqwest::Client::builder()
        .danger_accept_invalid_certs(true)
        .timeout(std::time::Duration::from_secs(5))
        .build()
        .unwrap();

    let url = format!("{}/api/", req.url);
    match client.get(&url).bearer_auth(&req.token).send().await {
        Ok(resp) if resp.status().is_success() => {
            let body: Value = resp.json().await.unwrap_or_default();
            let version = body["version"].as_str().map(String::from);
            Json(TestConnectionResponse { ok: true, version, error: None })
        }
        Ok(resp) => Json(TestConnectionResponse {
            ok: false,
            version: None,
            error: Some(format!("HA returned status {}", resp.status())),
        }),
        Err(e) => Json(TestConnectionResponse {
            ok: false,
            version: None,
            error: Some(e.to_string()),
        }),
    }
}

#[derive(Deserialize)]
struct CompleteSetupRequest {
    ha_url: String,
    ha_token: String,
    owner_name: String,
    owner_username: String,
    owner_password: String,
    app_name: String,
    theme: String,
}

/// Step 4 — write config and create owner account
async fn complete(
    State(state): State<AppState>,
    Json(req): Json<CompleteSetupRequest>,
) -> Json<Value> {
    use crate::{auth as hf_auth, config::*};
    use uuid::Uuid;

    // Generate JWT secret
    let jwt_secret = Uuid::new_v4().to_string() + &Uuid::new_v4().to_string();

    let cfg = Config {
        app: AppConfig {
            name: req.app_name,
            theme: req.theme,
            domain: String::new(),
        },
        ha: HaConfig {
            url: req.ha_url,
            token: req.ha_token,
            verify_ssl: false,
        },
        auth: AuthConfig {
            jwt_secret: jwt_secret.clone(),
            session_days: 30,
        },
        features: FeaturesConfig::default(),
    };

    if let Err(e) = crate::config::write(&cfg) {
        return Json(json!({ "ok": false, "error": e.to_string() }));
    }

    // Hash password and insert owner user
    let hash = match hf_auth::hash_password(&req.owner_password) {
        Ok(h) => h,
        Err(e) => return Json(json!({ "ok": false, "error": e.to_string() })),
    };

    let user_id = Uuid::new_v4().to_string();
    let now = chrono::Utc::now().timestamp();

    let result = sqlx::query(
        "INSERT INTO users (id, username, display_name, password_hash, role, enabled, expires_at, created_at)
         VALUES (?, ?, ?, ?, 'owner', 1, NULL, ?)"
    )
    .bind(&user_id)
    .bind(&req.owner_username)
    .bind(&req.owner_name)
    .bind(&hash)
    .bind(now)
    .execute(&state.inner.db.pool)
    .await;

    match result {
        Ok(_) => Json(json!({ "ok": true, "restart_required": true })),
        Err(e) => Json(json!({ "ok": false, "error": e.to_string() })),
    }
}
