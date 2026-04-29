# ─── Stage 1: build backend ───────────────────────────────────────────────────
FROM rust:1.78-slim-bookworm AS backend-builder

RUN apt-get update && apt-get install -y \
    pkg-config \
    libssl-dev \
    musl-tools \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /build

# Cache dependencies — copy manifests first
COPY Cargo.toml Cargo.lock ./
COPY backend/Cargo.toml backend/
COPY frontend/Cargo.toml frontend/

# Create dummy source files to cache the dep build layer
RUN mkdir -p backend/src frontend/src && \
    echo 'fn main() {}' > backend/src/main.rs && \
    echo '' > frontend/src/lib.rs

RUN cargo build --release --package homefront-backend 2>/dev/null || true

# Now copy real source and build properly
COPY backend/ backend/
RUN touch backend/src/main.rs && \
    cargo build --release --package homefront-backend

# ─── Stage 2: build frontend (WASM) ───────────────────────────────────────────
FROM rust:1.78-slim-bookworm AS frontend-builder

RUN apt-get update && apt-get install -y \
    pkg-config \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Install wasm target + trunk
RUN rustup target add wasm32-unknown-unknown && \
    cargo install trunk

WORKDIR /build

COPY Cargo.toml Cargo.lock ./
COPY frontend/ frontend/
COPY backend/Cargo.toml backend/

RUN mkdir -p backend/src && echo 'fn main() {}' > backend/src/main.rs

WORKDIR /build/frontend
RUN trunk build --release

# ─── Stage 3: final image ─────────────────────────────────────────────────────
FROM debian:bookworm-slim AS final

RUN apt-get update && apt-get install -y \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy backend binary
COPY --from=backend-builder /build/target/release/homefront ./homefront

# Copy compiled frontend assets
COPY --from=frontend-builder /build/backend/static ./static

# Data volume for config + SQLite db
VOLUME ["/data"]

EXPOSE 3000

ENV RUST_LOG=homefront=info,tower_http=info

CMD ["./homefront"]
