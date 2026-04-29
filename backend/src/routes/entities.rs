use crate::state::AppState;
use axum::{
    extract::{Path, State},
    routing::{get, post},
    Json, Router,
};
use serde::Deserialize;
use serde_json::{json, Value};

pub fn router(state: AppState) -> Router {
    Router::new()
        .route("/entities", get(get_all_states))
        .route("/entities/:entity_id", get(get_state))
        .route("/entities/:domain/:service", post(call_service))
        .with_state(state)
}

async fn get_all_states(State(state): State<AppState>) -> Json<Value> {
    let ha = state.inner.ha.read().await;
    match ha.as_ref() {
        Some(ha) => {
            let states = ha.states.read().await;
            Json(json!(states.values().collect::<Vec<_>>()))
        }
        None => Json(json!([])),
    }
}

async fn get_state(
    State(state): State<AppState>,
    Path(entity_id): Path<String>,
) -> Json<Value> {
    let ha = state.inner.ha.read().await;
    match ha.as_ref() {
        Some(ha) => {
            let states = ha.states.read().await;
            match states.get(&entity_id) {
                Some(s) => Json(json!(s)),
                None => Json(json!({ "error": "entity not found" })),
            }
        }
        None => Json(json!({ "error": "HA not connected" })),
    }
}

#[derive(Deserialize)]
struct ServiceCall {
    data: Option<Value>,
}

async fn call_service(
    State(state): State<AppState>,
    Path((domain, service)): Path<(String, String)>,
    Json(body): Json<ServiceCall>,
) -> Json<Value> {
    let ha = state.inner.ha.read().await;
    match ha.as_ref() {
        Some(ha) => {
            let data = body.data.unwrap_or(json!({}));
            match ha.call_service(&domain, &service, data).await {
                Ok(_) => Json(json!({ "ok": true })),
                Err(e) => Json(json!({ "ok": false, "error": e.to_string() })),
            }
        }
        None => Json(json!({ "ok": false, "error": "HA not connected" })),
    }
}
