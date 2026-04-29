use crate::{auth as hf_auth, state::AppState};
use axum::{extract::State, routing::post, Json, Router};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};

pub fn router(state: AppState) -> Router {
    Router::new()
        .route("/auth/login", post(login))
        .with_state(state)
}

#[derive(Deserialize)]
struct LoginRequest {
    username: String,
    password: String,
}

#[derive(Serialize)]
struct LoginResponse {
    token: String,
    role: String,
    display_name: String,
}

async fn login(
    State(state): State<AppState>,
    Json(req): Json<LoginRequest>,
) -> Json<Value> {
    let user = sqlx::query_as::<_, crate::db::User>(
        "SELECT * FROM users WHERE username = ? AND enabled = 1"
    )
    .bind(&req.username)
    .fetch_optional(&state.inner.db.pool)
    .await;

    let user = match user {
        Ok(Some(u)) => u,
        _ => return Json(json!({ "ok": false, "error": "invalid credentials" })),
    };

    // Check guest expiry
    if let Some(exp) = user.expires_at {
        if exp < chrono::Utc::now().timestamp() {
            return Json(json!({ "ok": false, "error": "guest access has expired" }));
        }
    }

    if !hf_auth::verify_password(&req.password, &user.password_hash) {
        return Json(json!({ "ok": false, "error": "invalid credentials" }));
    }

    let cfg = state.inner.config.read().await;
    let secret = cfg.as_ref().map(|c| c.auth.jwt_secret.clone()).unwrap_or_default();
    let days = cfg.as_ref().map(|c| c.auth.session_days).unwrap_or(30);
    drop(cfg);

    match hf_auth::create_token(&user.id, &user.username, &user.role, &secret, days) {
        Ok(token) => Json(json!({
            "ok": true,
            "token": token,
            "role": user.role,
            "display_name": user.display_name,
        })),
        Err(e) => Json(json!({ "ok": false, "error": e.to_string() })),
    }
}
