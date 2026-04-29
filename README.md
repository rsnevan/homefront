# homefront

A self-hosted home control dashboard for Home Assistant. Built in Rust — Axum on the backend, Leptos (WASM) on the frontend. Runs as a single Docker image. Designed to work as a TV wallpaper, a daily driver, and a guest control panel simultaneously.

---

## Overview

Most home dashboards are either locked to a vendor's cloud, require a Node.js runtime to maintain, or fall apart the moment you hand the URL to a guest. homefront is none of those things.

It connects directly to your Home Assistant instance over the WebSocket API, keeps a live entity state cache in memory, and pushes updates to all connected browsers in real time. Auth is JWT-based with two roles — owner and guest — where guest accounts can be toggled on/off and set to expire automatically. A guided setup wizard runs on first boot, so there is nothing to configure by hand before you can use it.

The frontend is compiled to WebAssembly and served as static files by the same binary that runs the API. The entire application ships as one Docker image.

---

## Features

- Real-time entity state sync via Home Assistant WebSocket API
- TV ambient mode — full-screen clock-forward display, no interaction required
- Guest accounts with configurable expiry and enable/disable toggle
- First-run setup wizard with mDNS and subnet-sweep HA discovery
- Single `config.toml` — every setting in one place, written by the wizard
- Self-contained Docker image — no external services required beyond HA itself
- Caddy reverse proxy included — HTTPS with Let's Encrypt in one line

---

## Stack

| Layer | Technology |
|---|---|
| Backend | Rust, Axum, Tokio |
| Frontend | Rust, Leptos, WebAssembly |
| Database | SQLite via sqlx |
| Auth | JWT (jsonwebtoken), bcrypt |
| Proxy | Caddy |
| Container | Docker, Docker Compose |
| HA integration | Home Assistant WebSocket API |

---

## Getting started

### Requirements

- Docker and Docker Compose on your server
- A running Home Assistant instance on the same network
- A long-lived access token from HA — generate one under Profile > Security > Long-lived access tokens

### Run

```bash
git clone https://github.com/rsnevan/homefront.git
cd homefront
docker compose up -d
```

Open `http://your-server-ip` in a browser. The setup wizard will handle the rest.

### Setup wizard

The wizard runs automatically when no `config.toml` is found. It will:

1. Scan your local network for Home Assistant instances via mDNS and port sweep
2. Verify your HA URL and access token by making a test API call
3. Create your owner account (username, password, display name)
4. Set your home name and preferred theme

On completion it writes `/data/config.toml` and you are done. All subsequent boots skip the wizard and go straight to the dashboard.

---

## Configuration

The wizard generates your config, but you can also write it manually. A reference file is included:

```bash
cp config.example.toml /data/config.toml
# edit as needed, then docker compose up -d
```

Key fields:

```toml
[app]
name = "My Home"     # Shown in the UI header
theme = "dark"       # dark | light | auto

[ha]
url = "http://192.168.1.42:8123"
token = "YOUR_TOKEN_HERE"

[auth]
jwt_secret = "long-random-string-change-this"
session_days = 30
```

The full reference with all available fields is in `config.example.toml`.

---

## Guest access

Owner accounts have full access and sessions that last `session_days` days with a remember-me cookie.

Guest accounts are created from the dashboard settings panel. Each guest account has:

- A username and password you set
- An optional expiry duration (e.g. 48 hours, 1 week, or never)
- An enable/disable toggle — flip it off to revoke access immediately without deleting the account

When a guest's token expires or their account is disabled, their next request returns a 401 and they are redirected to the login screen.

---

## Development

### Prerequisites (WSL2 or Linux)

```bash
# Rust
curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh
source ~/.cargo/env

# System dependencies
sudo apt update && sudo apt install -y build-essential pkg-config libssl-dev musl-tools

# Additional Rust targets
rustup target add wasm32-unknown-unknown
rustup target add x86_64-unknown-linux-musl

# Trunk — Leptos build tool
cargo install trunk
```

### Run locally

Backend (API server, port 3000):

```bash
cd backend
cargo run
```

Frontend (hot-reload dev server, port 8080):

```bash
cd frontend
trunk serve --proxy-backend=http://localhost:3000/api
```

The frontend dev server proxies `/api` requests to the backend automatically, so you can work on UI and logic simultaneously with full hot reload.

### Build the Docker image

```bash
docker build -t homefront:latest .
```

The Dockerfile uses a multi-stage build: one stage compiles the Axum backend to a static binary, a second stage compiles the Leptos frontend to WASM, and the final stage is a minimal Debian image containing only the binary and the compiled assets.

### Deploy to homelab

A local Docker registry is included in `docker-compose.yml` and runs on port 5000. Push from your dev machine, pull on the homelab:

```bash
# On your dev machine
docker tag homefront:latest <homelab-ip>:5000/homefront:latest
docker push <homelab-ip>:5000/homefront:latest

# On the homelab
docker compose pull && docker compose up -d
```

---

## Project structure

```
homefront/
├── backend/
│   ├── src/
│   │   ├── main.rs          # Entrypoint — setup vs normal mode detection
│   │   ├── config.rs        # Config loading and writing
│   │   ├── auth.rs          # JWT creation/validation, bcrypt
│   │   ├── ha.rs            # Home Assistant WebSocket client
│   │   ├── db.rs            # SQLite pool and user model
│   │   ├── state.rs         # Shared application state (Arc)
│   │   └── routes/
│   │       ├── mod.rs       # Route composition, setup vs normal split
│   │       ├── health.rs    # GET /api/health
│   │       ├── setup.rs     # Setup wizard API (discovery, test, complete)
│   │       ├── auth.rs      # POST /api/auth/login
│   │       ├── entities.rs  # GET /api/entities, POST /api/entities/:domain/:service
│   │       └── ws.rs        # WebSocket relay /api/ws
│   └── migrations/
│       └── 0001_users.sql
├── frontend/
│   ├── src/
│   │   ├── main.rs          # App root, Leptos router
│   │   ├── pages/
│   │   │   ├── dashboard.rs # Main control view
│   │   │   ├── tv.rs        # Ambient / TV mode
│   │   │   ├── login.rs     # Login screen
│   │   │   └── setup.rs     # Setup wizard UI
│   │   ├── components/      # Reusable UI components
│   │   └── stores/
│   │       ├── auth.rs      # Reactive auth state (JWT, role)
│   │       └── entities.rs  # Reactive entity state map
│   ├── index.html
│   └── Trunk.toml
├── docker/
│   └── Caddyfile
├── docker-compose.yml
├── Dockerfile
├── config.example.toml
└── Cargo.toml               # Workspace root
```

---

## Roadmap

- [ ] HA WebSocket auth and state_changed subscription
- [ ] Dashboard — room tiles, light controls, media controls, sensor readouts
- [ ] TV ambient mode
- [ ] Setup wizard — full mDNS discovery implementation
- [ ] Guest account management UI
- [ ] Mobile layout
- [ ] Light colour temperature and RGB control
- [ ] Scene support
- [ ] Per-room guest entity whitelisting

---

## Licence

MIT — see [LICENSE](LICENSE).