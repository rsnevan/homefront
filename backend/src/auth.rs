use chrono::Utc;
use jsonwebtoken::{decode, encode, DecodingKey, EncodingKey, Header, Validation};
use serde::{Deserialize, Serialize};

#[derive(Debug, Serialize, Deserialize, Clone)]
pub struct Claims {
    pub sub: String,       // user id
    pub username: String,
    pub role: String,      // "owner" | "guest"
    pub exp: i64,          // expiry unix timestamp
    pub iat: i64,          // issued at
}

pub fn create_token(
    user_id: &str,
    username: &str,
    role: &str,
    secret: &str,
    duration_days: u64,
) -> anyhow::Result<String> {
    let now = Utc::now().timestamp();
    let exp = now + (duration_days as i64 * 86_400);

    let claims = Claims {
        sub: user_id.to_string(),
        username: username.to_string(),
        role: role.to_string(),
        exp,
        iat: now,
    };

    let token = encode(
        &Header::default(),
        &claims,
        &EncodingKey::from_secret(secret.as_bytes()),
    )?;

    Ok(token)
}

pub fn validate_token(token: &str, secret: &str) -> anyhow::Result<Claims> {
    let data = decode::<Claims>(
        token,
        &DecodingKey::from_secret(secret.as_bytes()),
        &Validation::default(),
    )?;
    Ok(data.claims)
}

pub fn hash_password(password: &str) -> anyhow::Result<String> {
    Ok(bcrypt::hash(password, bcrypt::DEFAULT_COST)?)
}

pub fn verify_password(password: &str, hash: &str) -> bool {
    bcrypt::verify(password, hash).unwrap_or(false)
}
