use leptos::*;
use leptos_meta::*;
use leptos_router::*;

mod components;
mod pages;
mod stores;

use pages::{dashboard::DashboardPage, login::LoginPage, setup::SetupPage, tv::TvPage};

#[component]
pub fn App() -> impl IntoView {
    provide_meta_context();

    view! {
        <Stylesheet id="homefront" href="/styles/app.css"/>
        <Title text="homefront"/>
        <Router>
            <Routes>
                <Route path="/setup" view=SetupPage/>
                <Route path="/login" view=LoginPage/>
                <Route path="/tv" view=TvPage/>
                <Route path="/" view=DashboardPage/>
            </Routes>
        </Router>
    }
}

fn main() {
    console_error_panic_hook::set_once();
    mount_to_body(App);
}
