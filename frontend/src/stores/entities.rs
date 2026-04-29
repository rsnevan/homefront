use leptos::*;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct EntityState {
    pub entity_id: String,
    pub state: String,
    pub attributes: serde_json::Value,
}

pub type EntityMap = HashMap<String, EntityState>;

pub fn provide_entities() {
    let (entities, set_entities) = create_signal(EntityMap::new());
    provide_context(entities);
    provide_context(set_entities);
}

pub fn use_entities() -> ReadSignal<EntityMap> {
    use_context::<ReadSignal<EntityMap>>()
        .expect("EntityMap context not found")
}

pub fn use_set_entities() -> WriteSignal<EntityMap> {
    use_context::<WriteSignal<EntityMap>>()
        .expect("WriteSignal<EntityMap> context not found")
}
