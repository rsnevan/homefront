use crate::state::AppState;
use axum::{
    extract::{State, WebSocketUpgrade},
    response::IntoResponse,
    routing::get,
    Router,
};

pub fn router(state: AppState) -> Router {
    Router::new()
        .route("/ws", get(ws_handler))
        .with_state(state)
}

async fn ws_handler(
    ws: WebSocketUpgrade,
    State(state): State<AppState>,
) -> impl IntoResponse {
    ws.on_upgrade(move |socket| handle_socket(socket, state))
}

async fn handle_socket(
    mut socket: axum::extract::ws::WebSocket,
    state: AppState,
) {
    use axum::extract::ws::Message;
    use serde_json::json;

    // Subscribe to HA state broadcasts
    let ha = state.inner.ha.read().await;
    let mut rx = ha.as_ref().map(|h| h.tx.subscribe());
    drop(ha);

    // Send initial "connected" message
    let _ = socket.send(Message::Text(
        json!({ "type": "connected" }).to_string()
    )).await;

    loop {
        tokio::select! {
            // Forward HA state updates to this client
            update = async {
                if let Some(ref mut r) = rx {
                    r.recv().await.ok()
                } else {
                    None
                }
            } => {
                if let Some(update) = update {
                    let msg = json!({
                        "type": "state_changed",
                        "entity_id": update.entity_id,
                        "state": update.state,
                        "attributes": update.attributes,
                    });
                    if socket.send(Message::Text(msg.to_string())).await.is_err() {
                        break;
                    }
                }
            }
            // Handle messages from client (ping, commands)
            msg = socket.recv() => {
                match msg {
                    Some(Ok(Message::Close(_))) | None => break,
                    Some(Ok(Message::Ping(p))) => {
                        let _ = socket.send(Message::Pong(p)).await;
                    }
                    _ => {}
                }
            }
        }
    }
}
