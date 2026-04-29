use leptos::*;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct AuthUser {
    pub token: String,
    pub role: String,
    pub display_name: String,
}

/// Provide auth context to the app
pub fn provide_auth() {
    let (user, set_user) = create_signal(load_from_storage());
    provide_context(user);
    provide_context(set_user);
}

pub fn use_auth() -> ReadSignal<Option<AuthUser>> {
    use_context::<ReadSignal<Option<AuthUser>>>()
        .expect("AuthUser context not found — wrap app in provide_auth()")
}

pub fn use_set_auth() -> WriteSignal<Option<AuthUser>> {
    use_context::<WriteSignal<Option<AuthUser>>>()
        .expect("WriteSignal<AuthUser> context not found")
}

fn load_from_storage() -> Option<AuthUser> {
    // Load persisted token from localStorage
    // gloo_storage::LocalStorage::get("hf_auth").ok()
    None // stub
}
