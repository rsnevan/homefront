<p align="center">
  <img src="server/wwwroot/icons/icon.svg" width="72" alt="homefront logo">
</p>

<h1 align="center">homefront</h1>

<p align="center">
  A self-hosted control room for a living room built around a Windows HTPC.<br>
  Lights, the TV, the media library and the PC itself, from any browser on any device.
</p>

<p align="center">
  <img src="docs/screenshots/home.jpg" alt="homefront home screen: a live floor plan of the house with lit rooms, a now-playing card, scenes and TV controls">
</p>

## What it does

homefront runs on the HTPC that's plugged into the TV and becomes the one place to control everything around it. Open it on a phone, a laptop, a tablet or the TV itself.

- **The house as a live floor plan.** Each room glows with the real colour temperature and brightness of its bulbs. Tap a room for per-bulb brightness, warmth and colour. A lamplight-yellow dot on the wall marks the room where something is playing.
- **Lights that follow what's playing.** When a video plays on the HTPC (the built-in player, a browser tab, or VLC), the TV room dims. Pause brings the lights up a little; stopping puts every bulb back exactly as it was. Lights that were off stay off, so nothing switches on in the daytime.
- **A remote that shows the screen.** A live mirror of the TV. Tap anything on it to click it there, or switch to a touchpad with two-finger scrolling. Type into search boxes from your phone, send keys, control media and system volume.
- **Your Jellyfin library, played on the TV.** Browse, search and press *Play on TV*. A full-screen kiosk player opens on the HTPC and is controlled from your phone, resumes where you left off, reports progress back to Jellyfin and moves to the next episode on its own. Subtitles come on in your language by default; switch subtitle or audio tracks from the phone (picture-based DVD/PGS subtitles are drawn into the video).
- **Sleep timer.** "Everything off in 45 minutes": pause, TV off, lights fade out, with a one-minute warning on the TV.
- **Cameras.** Any camera in Home Assistant shows live in homefront, and motion while you're watching pops up on the TV with a snapshot.
- **Home Assistant, both ways.** homefront publishes the HTPC's state (playing, CPU, memory, free disk, sleep timer) as Home Assistant sensors, and Home Assistant scripts, Assist and Siri drive homefront by firing a `homefront_command` event (`{"command": "scene", "scene": "movie"}`, `sleep_timer`, `ambient`, `pause`, `open`). New Jellyfin arrivals fire `homefront_new_media` for phone notifications.
- **Open anything on the TV.** One tap opens Netflix, YouTube and friends in the HTPC's everyday browser, with your existing logins, or desktop apps like Spotify, IPTVnator and VLC (`app:spotify`, `app:iptvnator`, `app:vlc`). Paste any link to send it to the screen. On Android, *Share → homefront* does the same.
- **Now playing, whatever it is.** Shows whatever is playing on the HTPC right now (the most recent if several are), detected from which apps are actually making sound, so it works even for apps that don't report to Windows (Spotify, VLC, IPTVnator). Titles come from Windows' media session or the app's window. Artwork comes from the app when it provides it, otherwise songs are matched on iTunes or Deezer and videos against your Jellyfin library. No API keys.
- **Scenes.** Movie (TV on, HTPC input, cinema rooms dimmed warm), Evening, Bright, Lights off, Goodnight (pause, TV off, lights off).
- **TV control** for LG webOS TVs: power, volume, mute, input and screen-off-with-sound, through Home Assistant and [LGTV Companion](https://github.com/JPersson77/LGTVCompanion).
- **Ambient mode** turns the TV into a quiet wall clock with weather, the next prayer time, a miniature of the floor plan and what's playing. A row of things to continue watching (or what's new) sits underneath: arrow keys pick one, Enter plays it, Escape closes. It drifts slightly every minute.
- **Wake-up light.** Choose when you want to be up, the days and how long the fade is; the bedroom ramps from a dim warm glow to bright daylight. The settings live in Home Assistant helpers, so it runs even if homefront is down.
- **Lights left on.** Three minutes after you leave, your phone says which lights are still on, with *Turn everything off* and *Leave them on* buttons.
- **Weather and prayer times** with no API keys (Open-Meteo and Aladhan). Sehri and Iftar appear automatically during Ramadan. Optional, all off by default: a notice on the TV at prayer times, pausing playback, and an Iftar countdown on the TV fifteen minutes before Maghrib.
- **Guest passes.** Create a time-limited pass, show the QR code, and a guest is signed in on their phone with lights, TV and media. Settings and power stay with the owner. Turn a pass off and it stops working immediately.
- **Guided light setup.** Tuya / Smart Life bulbs are linked through Home Assistant from inside homefront: paste the Smart Life user code and scan the QR code it shows.

## Screenshots

| | |
|---|---|
| ![Remote with live screen mirror, key pad and now playing](docs/screenshots/remote.jpg) | ![Library details sheet with seasons and episodes](docs/screenshots/details.jpg) |
| **Remote.** Tap the mirror to click on the TV. | **Library.** Play on the TV, resume or start over. |
| ![Room sheet with brightness, warmth presets and per-bulb controls](docs/screenshots/room.jpg) | ![Ambient mode on the TV: a large clock, weather, prayer time and a mini floor plan](docs/screenshots/ambient.png) |
| **Rooms.** Every bulb, or the whole room at once. | **Ambient mode** on the TV. |

<p align="center">
  <img src="docs/screenshots/phone-home.jpg" width="260" alt="homefront on a phone">
  &nbsp;&nbsp;
  <img src="docs/screenshots/phone-remote.jpg" width="260" alt="The remote on a phone">
</p>

<details>
<summary>The whole home screen</summary>

![Full home screen](docs/screenshots/home-full.jpg)
</details>

## How it fits together

```
 phone / laptop / TV browser
            │  HTTP + one WebSocket (live state, pointer and keyboard input)
            ▼
 ┌─────────────────────── homefront.exe (on the HTPC, in the desktop session) ───────────────────────┐
 │  Windows: media session · Core Audio volume · SendInput · screen capture · Brave launcher/kiosk   │
 │  Home Assistant WebSocket client (lights, TV, Tuya setup)   Jellyfin client + HLS proxy           │
 │  LGTV Companion CLI (power, input, screen off)              Open-Meteo · Aladhan                  │
 └───────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Server:** .NET 10 minimal API, published as one self-contained `homefront.exe` (no runtime to install). It has to run in the logged-in desktop session, because that's where the screen, audio and media session live.
- **Front end:** Preact with htm and plain ES modules. No build step, and every dependency is vendored, so nothing loads from a CDN apart from the Inter font.
- **Live state:** the server keeps one WebSocket to Home Assistant and pushes changes to every open dashboard over its own socket.
- **Playback:** Jellyfin decides per title whether to copy the streams or transcode (Intel Quick Sync on the HTPC). The kiosk player plays the result with hls.js through a homefront proxy, so the browser only ever talks to homefront.

## Requirements

- A Windows 10/11 PC that stays logged in (the HTPC)
- [Home Assistant](https://www.home-assistant.io/) on the network, with your lights and the TV integrated
- Optional: [Jellyfin](https://jellyfin.org/) for the library and player
- Optional: [LGTV Companion](https://github.com/JPersson77/LGTVCompanion) for LG TV power, input and screen-off
- [Brave](https://brave.com/) on the HTPC for launching sites and the kiosk player (any Chromium browser works; set its path in `config.json`)

## Install

1. **Build** on any Windows machine with the .NET 10 SDK:
   ```powershell
   cd server
   dotnet publish -c Release -o ..\publish
   ```
2. **Copy** the `publish` folder to the HTPC, for example `C:\Users\<you>\homefront`.
3. **Run** `homefront.exe` once. It creates `data\config.json` and starts on port 80.
4. **Configure** `data\config.json`, then restart homefront:
   ```jsonc
   {
     "homeName": "Home",
     "ownerName": "Sam",
     "ha": {
       "urls": ["http://homeassistant.local:8123"],
       "token": "<Home Assistant long-lived access token>",
       "tvEntity": "media_player.lg_webos_tv"
     },
     "jellyfin": {
       "url": "http://127.0.0.1:8096",
       "apiKey": "<access token from /Users/AuthenticateByName>",
       "userId": "<your Jellyfin user id>"
     },
     "location": { "name": "Johannesburg", "lat": -26.2, "lon": 28.05 }
   }
   ```
   Everything else (rooms, shortcuts, prayer method, cinema rooms, guest passes, password) is editable in the app under **Settings**.
5. **Start at login:** put a shortcut to `homefront.exe` in `shell:startup`, and allow TCP 80 inbound on private networks in Windows Firewall.
6. Open `http://<htpc-name>` and sign in with **admin / homefront**. **Change the password in Settings straight away.**

### Rooms and lights

Rooms are matched by name: a bulb called *Kitchen 1* lands in Kitchen automatically. Anything else can be assigned in Settings. The first room is the large one on the floor plan and the default cinema room.

### Away from home

homefront is meant for a private network. To reach it from outside, use [Tailscale](https://tailscale.com/) rather than port forwarding. `tailscale serve --bg 80` gives it an HTTPS address on your tailnet, which also lets phones install it as a full app.

## Security

- Every API and the live socket require a signed session cookie (HMAC-SHA256, HttpOnly, SameSite=Lax). Passwords are stored as PBKDF2-SHA256 hashes.
- Requests from the HTPC itself are trusted, so its kiosk pages work without signing in. Requests forwarded by a reverse proxy (`X-Forwarded-*` or Tailscale headers) are never treated as local.
- Guests can't change settings, put the PC to sleep, or link lights. Turning off or deleting a pass revokes it immediately.
- Tokens for Home Assistant and Jellyfin live only in `data\config.json` on the HTPC, which is ignored by git.
- homefront can move the mouse and type on the HTPC. Don't expose it to the internet.

## Development

```
server/
  Program.cs          endpoints, auth gate, live socket, background loops
  HomeAssistant.cs    WebSocket client with reconnect and state cache
  Jellyfin.cs         library, images, playback source, progress reporting
  Automation.cs       rooms, scenes and the follow-what's-playing automation
  WinMedia.cs         Windows media session (+ VLC window fallback)
  WinSystem.cs        volume, input injection, screen capture, power, stats
  Apps.cs             browser launching, kiosk window, LGTV Companion CLI
  Feeds.cs            weather and prayer times
  wwwroot/            the app: index.html, app.css, js/ (Preact + htm, no build)
tools/
  build-icons.mjs     bundles the Lucide icons the UI uses into js/icons.mjs
  screenshots.mjs     regenerates docs/screenshots with Playwright and Edge
```

Front-end changes need no build: edit `server/wwwroot` and refresh. To regenerate the screenshots, run a local instance on port 8090 and `node tools/screenshots.mjs`.

## Credits

[Preact](https://preactjs.com/), [htm](https://github.com/developit/htm), [hls.js](https://github.com/video-dev/hls.js), [Lucide](https://lucide.dev/) icons, [QRCoder](https://github.com/codebude/QRCoder), [Open-Meteo](https://open-meteo.com/), [Aladhan](https://aladhan.com/prayer-times-api), [LGTV Companion](https://github.com/JPersson77/LGTVCompanion).

## License

MIT
