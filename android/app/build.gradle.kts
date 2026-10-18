import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "app.homefront"
    compileSdk = 36

    defaultConfig {
        applicationId = "app.homefront"
        minSdk = 29
        targetSdk = 36
        versionCode = 1
        versionName = "1.0"
        // Your own HTPC's address goes in local.properties (not committed); gradle.properties has a generic one.
        val local = Properties().apply { rootProject.file("local.properties").takeIf { it.exists() }?.reader()?.use { load(it) } }
        val url = local.getProperty("homefront.defaultUrl") ?: (project.findProperty("homefront.defaultUrl") as String?) ?: "http://homefront.local"
        buildConfigField("String", "DEFAULT_URL", "\"$url\"")
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            // Sideloaded onto one phone: signed with this machine's debug key so updates install over the top.
            signingConfig = signingConfigs.getByName("debug")
        }
    }
    buildFeatures { buildConfig = true; compose = true }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin { compilerOptions { jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17) } }

dependencies {
    implementation("androidx.core:core-ktx:1.16.0")
    implementation("androidx.activity:activity-ktx:1.10.1")
    implementation("androidx.webkit:webkit:1.13.0")
    implementation("androidx.glance:glance-appwidget:1.1.1")
    implementation("androidx.glance:glance-material3:1.1.1")
    implementation("androidx.work:work-runtime-ktx:2.10.1")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
}
