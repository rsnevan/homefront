pub mod auth;
pub mod entities;
pub mod health;
pub mod setup;
pub mod ws;

use crate::state::AppState;
use axum::Router;

pub fn api_router(setup_mode: bool, state: AppState) -> Router {
    if setup_mode {
        // Setup mode — only the wizard API is available
        Router::new()
            .merge(health::router())
            .merge(setup::router(state))
    } else {
        // Normal mode — full API
        Router::new()
            .merge(health::router())
            .merge(auth::router(state.clone()))
            .merge(entities::router(state.clone()))
            .merge(ws::router(state))
    }
}
