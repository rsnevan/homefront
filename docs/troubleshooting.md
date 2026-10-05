# Troubleshooting

Support works remotely over Tailscale and SSH; most fixes take a few minutes and no visit.

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| homefront won't load | homefront isn't running in the desktop session | Sign in to the HTPC; start `homefront.exe` from the Startup shortcut. Check `data\homefront.log`. |
| "Reconnecting" banner | HTPC asleep, or the network dropped | Wake the HTPC; check its network cable. |
| Lights or TV don't respond | Home Assistant can't be reached, or the device is offline | Look at the status dots in homefront's side rail; check the device in Home Assistant. A Tuya bulb switched off at the wall shows as unavailable. |
| TV won't switch on | Deep standby: the TV's network is asleep | On the TV, turn on **Quick Start+** and **Turn on via Wi-Fi**. homefront retries for 25 seconds; if it still fails, the log says *TV didn't wake up*. |
| TV clock or player opens behind other windows | Windows blocked it from taking focus | Fixed in current builds (homefront keeps bringing it forward); update homefront. |
| Lights jump while watching | Two things changing the same lights | Check which automation changed them (Home Assistant → Logbook). Follow-what's-playing only dims and stands aside during the sunset fade. |
| Now playing is wrong or empty | The app doesn't report to Windows and isn't making sound | `/api/pc/audio` (from the HTPC) lists which apps are making sound. |
| No phone alerts | Notifications off, quiet hours, or a push hiccup | iPhone Settings → Notifications → Home Assistant; check the automation's trace in Home Assistant. A one-off push failure appears in Home Assistant's log as *Error sending notification*. |
| Scripts missing in Shortcuts, widgets or the watch | The Home Assistant app's local list is out of date | Open the app and let it load, close it fully, reopen Shortcuts; or Settings → Companion App → Debugging and reset its cache. Meanwhile use **Perform Action** with the script's name. |
| Tailscale keeps switching on at home | The Home Assistant app doesn't know the home Wi-Fi | Add the Wi-Fi's name under the app's Internal URL. Opening homefront from its icon also uses Tailscale, by design. |
| Can't reach the house when away | Tailscale off, or On Demand misconfigured | On Demand set to **Do nothing**; the subnet route approved in the admin console. |
| Paste does nothing | The page isn't on HTTPS | Open homefront from the Tailscale address; on the plain address, use the paste field that appears. |

## Logs and checks

- homefront: `data\homefront.log` next to `homefront.exe`.
- Home Assistant: Settings → System → Logs; each automation's **Traces** show exactly why it did or didn't run.
- Layout: `node tools/audit.mjs` against a test copy on port 8090. Test copies must run with automations off (`cinema.enabled`, prayer and camera notices `false`) so they can't touch the real lights.

## Known limits

- An "HTPC offline" alert is impossible while Home Assistant runs on the HTPC itself; that needs a separate small device.
- Spotify and VLC don't report to Windows, so their titles come from their windows; a paused Spotify shows the last song seen.
- LG TV notices are text only.
- The ONVIF motion sensor appears after the camera's first motion event.
- Tuya bulbs depend on Tuya's cloud; an internet outage stops light control until it returns.
- iPhones report location every few hundred metres while driving, so the 1 km welcome can arrive a little later than 1 km.
