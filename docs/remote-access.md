# Remote access

Nothing in the house is reachable from the internet. At home, phones talk to the HTPC over Wi-Fi; away, they go through [Tailscale](https://tailscale.com/), a private network that the phone switches on only when it's needed.

| What | At home | Away |
| --- | --- | --- |
| homefront | `https://<htpc>.<tailnet>.ts.net` (or `http://<htpc>`) | `https://<htpc>.<tailnet>.ts.net` |
| Home Assistant app | Internal URL, used on the home Wi-Fi | External URL `http://<htpc>.<tailnet>.ts.net:8123` |
| Jellyfin | `http://<htpc>:8096` | `http://<htpc>.<tailnet>.ts.net:8096` |

## On the HTPC

- `tailscale serve --bg 80` gives homefront a real HTTPS certificate, which lets phones install it as an app and paste from the clipboard.
- A Windows port proxy forwards 8123 to Home Assistant; its firewall rule only accepts the tailnet (100.64.0.0/10), so neither the home network nor the internet can use it.
- `tailscale set --advertise-routes=<home subnet>/24`, approved once in the admin console, makes home addresses work while Tailscale is on.

## On each phone

- Tailscale's **On Demand** set to **Do nothing** for Wi-Fi and cellular. Tailscale then switches itself on whenever an app looks up a tailnet name and stays off otherwise.
- The Home Assistant app's **Internal URL** lists the home Wi-Fi's name, so the app talks to the house directly when it's home.

Alerts never need the VPN: they arrive through Apple's or Google's push service. Home and away come from the router seeing the phone on the Wi-Fi as well as GPS.

### When the VPN switches on

- Opening homefront from its icon (it uses the Tailscale address, so it works everywhere).
- The Home Assistant app reporting your location while you're out. That's what makes the driving-home welcome work.
- Home Assistant widgets and watch complications, which can't always tell they're at home.

If you'd rather Tailscale only came on while driving, add **Tailscale → Connect** to the start of the *CarPlay connects* automation and **Tailscale → Disconnect** (after a 30-second wait) to the end of *CarPlay disconnects*.

## More than one home

Each home gets its own tailnet, owned by the household that lives there. Access between homes uses Tailscale **machine sharing**: the owner of a home shares just its HTPC with a named person (admin console → the machine → Share), and it appears in that person's tailnet beside their own devices.

| Who | Gets |
| --- | --- |
| The household | Their own home, through their own tailnet. Family members without Tailscale use homefront logins or guest passes at home. |
| Family elsewhere (e.g. you reaching your parents' home) | That home's HTPC, shared to them. Never the rest of the network behind it. |
| Whoever supports the install | Each home's HTPC, shared by its owner and revocable by them at any time. |

One connection reaches every home shared with you; nothing is shared back the other way. Sharing covers the HTPC itself, not the network behind it, which is enough because homefront and Home Assistant are both reached through the HTPC. A tailnet on the free plan holds three people; bigger households need Tailscale's paid personal plan.

## Known limit

If you're away on a network that uses the same address range as home (192.168.8.x is the default on many routers), home addresses point at that network instead. The Tailscale names keep working.
