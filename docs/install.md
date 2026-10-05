# Install

A full home takes about a day, plus half an hour of the household's time for the steps only they can do (approving sign-ins, scanning QR codes, installing apps on their phones). Do the steps in order; each one leans on the one before.

## What you need

| Item | Required | Notes |
| --- | --- | --- |
| HTPC: Windows 10/11 Pro, 4 cores, 8 GB RAM, wired Ethernet | Yes | Pro for Hyper-V. Intel graphics give Jellyfin hardware transcoding (Quick Sync). It stays on and logged in. |
| TV on the HTPC's HDMI | Yes | LG webOS gets power, input, volume and on-screen notices. Other TVs get everything except TV control. |
| Router with DHCP reservations | Yes | Fixed addresses for the HTPC, Home Assistant, the TV and cameras. |
| Smart bulbs | Optional | Anything Home Assistant supports. Tuya / Smart Life brands link from inside homefront. |
| Camera | Optional | ONVIF-capable. |
| Phone with the Home Assistant app | Recommended | Alerts, presence, widgets, watch, CarPlay. |

## 1. Survey the home

Write down the TV model, router, bulbs and the app they use, cameras, the HTPC's specs and where the media lives. Note anything already installed so nothing gets removed by accident.

## 2. Prepare the HTPC

1. Windows 11 Pro on wired Ethernet, automatic sign-in, sleep off.
2. If the TV's HDMI port can't do 4K at 60 Hz, set the output to 1080p at 60 Hz.
3. Install OpenSSH Server for remote support: `winget install Microsoft.OpenSSH.Preview`.
4. Install Tailscale and sign in to the household's tailnet (see [Remote access](remote-access.md)). Enable HTTPS certificates in the Tailscale admin console.

## 3. Router

Reserve fixed addresses for the HTPC, the Home Assistant virtual machine (by its MAC address, once it exists), the TV and each camera.

## 4. TV

1. Install [LGTV Companion](https://github.com/JPersson77/LGTVCompanion) on the HTPC and pair it; accept the prompt on the TV.
2. On the TV, turn on **Quick Start+** (Settings → General → Devices → TV Management) and **Turn on via Wi-Fi** (Settings → General → Devices → External Devices → TV On With Mobile). The second also covers a network cable. Without them, the TV may not wake from deep standby.

## 5. Jellyfin

1. `winget install Jellyfin.Server`. It installs as a Windows service.
2. Run the setup wizard and add the libraries.
3. Dashboard → Playback → Transcoding: **Intel Quick Sync**.
4. Allow TCP 8096 for private networks in Windows Firewall.

If the service won't start after pointing it at a new drive, give the `NETWORK SERVICE` account read access to the media folders.

## 6. Home Assistant

1. Download the Home Assistant OS VHDX and create a Hyper-V virtual machine: Generation 2, 2 vCPU, 2 GB RAM, Secure Boot off, connected to an external switch, checkpoints off, start automatically.
2. Open Home Assistant, create the owner account, set the home location and units.
3. Add integrations:

| Integration | For |
| --- | --- |
| LG webOS TV | TV state, volume, inputs |
| Tuya (via the Smart Life app's QR code) or the bulbs' own integration | Lights |
| ONVIF | Cameras (needs an ONVIF account set in the camera's own app) |
| The router's integration (e.g. Huawei LTE) | Presence from the Wi-Fi |
| Mobile app (automatic when the phone app signs in) | Alerts, GPS presence, widgets |

4. Profile → Security → create a **long-lived access token** for homefront.

## 7. homefront

1. Build: `cd server` then `dotnet publish -c Release -o ..\publish`.
2. Copy the `publish` folder to the HTPC, e.g. `C:\Users\<you>\homefront`.
3. Run `homefront.exe` once to create `data\config.json`; fill in the Home Assistant URL and token, the Jellyfin URL, API key and user id, and the location.
4. Allow TCP 80 for private networks in Windows Firewall.
5. Put a shortcut to `homefront.exe` in the Startup folder (`shell:startup`). It must run in the signed-in desktop session, not as a service, because that's where the screen, audio and media sessions are.
6. Open homefront, sign in with **admin / homefront** and change the password in Settings straight away.
7. Settings: name the home and the owner, check the rooms, the cinema rooms and the shortcuts.

## 8. Automations

Create the Home Assistant scripts, helpers and automations in [Automations](automations.md), and expose the scripts to Assist (Settings → Voice assistants → Expose) so Siri, the watch and CarPlay can use them.

## 9. Remote access

On the HTPC:

```powershell
tailscale serve --bg 80                                   # homefront over HTTPS on the tailnet
tailscale set --advertise-routes=<home subnet>/24         # the home network, approved once in the admin console
netsh interface portproxy add v4tov4 listenport=8123 connectaddress=<home assistant ip> connectport=80
# then a firewall rule for TCP 8123 that only accepts 100.64.0.0/10 (the tailnet)
```

Set Home Assistant's internal URL to its home address and its external URL to `http://<htpc>.<tailnet>.ts.net:8123`.

## 10. Phones

1. Install the Home Assistant app and sign in. Allow notifications and location **Always** with precise location.
2. In the app: Settings → Companion App → the server → **Internal URL** → add the home Wi-Fi's name.
3. Install Tailscale, sign in, and set On Demand to **Do nothing** for Wi-Fi and cellular.
4. Open homefront at `https://<htpc>.<tailnet>.ts.net` and add it to the home screen.
5. Set up widgets, Control Centre, the watch, Siri and CarPlay ([User guide](user-guide.md#your-phone-watch-and-car)).

## 11. Check

- Run the layout audit on a test copy: `node tools/audit.mjs` (134 page states, 9 screen sizes).
- Trigger a test notification, a motion alert and each scene.
- Play something from the library with subtitles; check the remote, the TV clock and a TV notice.
- Switch the TV off, wait five minutes, switch it on from homefront.

## 12. Hand over

Replace every default password, walk the household through the [User guide](user-guide.md), and write down what was installed and where.
