# Automations

homefront and Home Assistant split the work: Home Assistant owns the devices, timing and alerts, so they keep running if homefront stops; homefront owns everything only the HTPC can see (what's playing, the screen, the keyboard and mouse) and the dashboard.

## How they talk

| Direction | Name | Purpose |
| --- | --- | --- |
| Home Assistant → homefront | event `homefront_command` | Ask homefront to do something (table below) |
| homefront → Home Assistant | `binary_sensor.homefront_playing` | Something is playing; attributes `title`, `app`, `video` |
| homefront → Home Assistant | `sensor.homefront_htpc_cpu`, `_memory`, `_disk_<drive>_free` | HTPC health |
| homefront → Home Assistant | `sensor.homefront_sleep_timer` | Minutes left |
| homefront → Home Assistant | `sensor.homefront_heartbeat` | Updated every minute; an alert fires when it goes stale |
| homefront → Home Assistant | event `homefront_new_media` | New films and episodes, batched per show |

### `homefront_command`

| `command` | Extra data | Does |
| --- | --- | --- |
| `scene` | `scene`: `movie`, `evening`, `bright`, `latenight`, `off`, `goodnight` | Runs a scene |
| `sleep_timer` | `minutes` (none cancels) | Starts or cancels the sleep timer |
| `ambient` | | Opens the TV clock |
| `close_kiosk` | | Closes the TV clock or player |
| `open` | `url` (or `app:spotify` etc.) | Opens something on the TV |
| `pause` / `play` | | Pauses or resumes whatever is playing |
| `tv_on` | | Wakes the TV, retrying until it answers |
| `welcome` | | TV on, HTPC input, clock showing, unless something is already on screen |
| `cinema_resume` | | Follow-what's-playing takes over after the sunset fade hands over |

Example script:

```yaml
alias: Movie time
sequence:
  - event: homefront_command
    event_data: { command: scene, scene: movie }
```

## Scripts

These are what the phone, the watch, CarPlay, Siri and Assist see.

| Script | Does |
| --- | --- |
| Movie time | Scene: Movie |
| Evening lights / Bright lights / All lights off | Scenes |
| Late night lights | Scene: Late night |
| Goodnight | Scene: Goodnight |
| Morning lights | Bedroom to full daylight |
| Sleep timer 30 minutes / 1 hour | Sleep timer |
| Clock on the TV / Close the clock | The TV clock |
| Pause the TV | Pause |
| Car connected / Car disconnected | Run by the phone's CarPlay automations |
| Lights-left-on nudge | The actionable "lights are still on" notification |

## Helpers

| Helper | Used by |
| --- | --- |
| Wake-up light (on/off), Wake-up time, Wake-up days, Wake-up fade minutes | The wake-up light; edited on homefront's Lights page |
| Keep lights on while away | Set by *Leave them on*; cleared when you get home |
| Driving | Set by the car scripts |
| Sunset fading | On while the sunset fade runs; homefront leaves the lights alone meanwhile |
| Quiet hours (schedule, 23:00–07:00) | Holds back routine alerts |
| Almost home (zone, 1 km, passive) | The driving-home welcome |

## Automations

| Automation | Trigger | Does |
| --- | --- | --- |
| Sunset: fade the living room on | 20 min before sunset, TV on, living room dark | 30 one-minute steps from 5% to a warm 60%. If a video was being watched when it started, it runs to the end through pauses and new episodes; if nothing was and a video starts, it stops and sends `cinema_resume`. Any manual change stops it. |
| TV on after dark | TV switches on after sunset | Living room on |
| Arriving home after dark | You arrive, after sunset, living room dark | Living room to 60% warm |
| Wake-up light | The fade-start time on a chosen day | Bedroom ramps warm-to-daylight, one step a minute |
| Everyone left: Goodnight | Away for 10 minutes | Goodnight, unless *Leave them on* |
| Nudge: lights left on | Away for 3 minutes with lights on (and not already asked by the car) | The lights-left-on notification |
| Nudge: handle the reply | A button on that notification | *Turn everything off* runs Goodnight; *Leave them on* sets the helper |
| Back home: clear "keep lights on" | You arrive | Clears the helper |
| Driving home: lights on and the TV clock | Driving, about 1 km out, after dark, out for 10+ minutes | Living room and kitchen on, `welcome` |
| TV: switch on through the HTPC | Anything asks Home Assistant to turn the TV on | `tv_on` (Home Assistant's LG integration can't wake the TV by itself) |
| Alert: new on Jellyfin | `homefront_new_media` | Phone alert |
| Alert: a light isn't responding | A bulb unavailable for 10 minutes | Phone alert |
| Alert: homefront stopped responding | Heartbeat stale for 5 minutes | Phone alert |
| Alert: camera motion | Motion on the camera | Phone alert with a snapshot; away always, home outside quiet hours |

## Inside homefront

**Follow what's playing** (Lights page) dims the cinema rooms while a video plays and lifts them a little when paused. It saves the lights the moment playback starts and restores them when it stops. It never brightens: each light goes to whichever is lower, its own level or the cinema level. A change you make during playback becomes the new state to restore. While the sunset fade runs, it stays out of the way.

**What counts as video:** homefront's own player; browser tabs from video sites (a tab that reports an album, such as YouTube Music or Spotify Web, is music); and IPTVnator, VLC, Jellyfin, Plex, Kodi, Stremio, mpv, MPC, PotPlayer and the Windows media players. Music apps, games and calls never touch the lights.

**Sleep timer:** one minute before the end, a notice on the TV; at the end, Goodnight.

**TV notices** (LG only): motion, the sleep-timer warning and, if switched on, prayer times. They're text only: the TV doesn't answer notices that carry a picture.
