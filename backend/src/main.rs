mod auth;
mod config;
mod db;
mod ha;
mod routes;
mod state;

use axum::{Router, middleware};
use std::net::SocketAddr;
use tower_http::{cors::CorsLayer, fs::ServeDir, trace::TraceLayer};
use tracing::info;
use tracing_subscriber::{layer::SubscriberExt, util::SubscriberInitExt};

#[tokio::main]
async fn main() {
    // Initialise tracing
    tracing_subscriber::registry()
        .with(tracing_subscriber::EnvFilter::new(
            std::env::var("RUST_LOG").unwrap_or_else(|_| "homefront=debug,tower_http=info".into()),
        ))
        .with(tracing_subscriber::fmt::layer())
        .init();

    info!("homefront starting…");

    // Detect setup mode — if config is missing or incomplete, serve setup wizard only
    let cfg = config::load();
    let setup_mode = cfg.is_none();

    if setup_mode {
        info!("no config found — entering setup mode");
    } else {
        info!("config loaded — entering normal mode");
    }

    // Initialise shared application state
    let state = state::AppState::new(cfg).await.expect("failed to init state");

    // Build router
    let app = Router::new()
        .nest("/api", routes::api_router(setup_mode, state.clone()))
        // Serve Leptos WASM frontend from /static
        .nest_service("/", ServeDir::new("static").append_index_html_on_directories(true))
        .layer(CorsLayer::permissive())
        .layer(TraceLayer::new_for_http());

    let port: u16 = std::env::var("PORT")
        .ok()
        .and_then(|p| p.parse().ok())
        .unwrap_or(3000);

    let addr = SocketAddr::from(([0, 0, 0, 0], port));
    info!("listening on http://{}", addr);

    let listener = tokio::net::TcpListener::bind(addr).await.unwrap();
    axum::serve(listener, app).await.unwrap();
}
