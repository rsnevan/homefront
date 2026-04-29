CREATE TABLE IF NOT EXISTS users (
    id           TEXT PRIMARY KEY NOT NULL,
    username     TEXT UNIQUE NOT NULL,
    display_name TEXT NOT NULL,
    password_hash TEXT NOT NULL,
    role         TEXT NOT NULL DEFAULT 'guest',
    enabled      INTEGER NOT NULL DEFAULT 1,
    expires_at   INTEGER,
    created_at   INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_users_username ON users(username);
