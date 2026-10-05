# homefront documentation

homefront is the control room for a home built around a Windows HTPC: lights, the TV, the media library, the PC, cameras and the phone in your pocket. These pages cover living with it, installing it and looking after it.

| Page | For | What's in it |
| --- | --- | --- |
| [User guide](user-guide.md) | Everyone in the house | Every screen, every button, the phone, the watch and the car |
| [Install](install.md) | Whoever sets it up | From a bare HTPC to a finished home, in order |
| [Automations](automations.md) | Whoever tunes it | Every automation, script and helper, and how Home Assistant and homefront talk |
| [Remote access](remote-access.md) | Whoever sets it up | Tailscale, phones, more than one home, sharing with family |
| [Troubleshooting](troubleshooting.md) | Whoever gets the phone call | Symptoms, causes and fixes, plus known limits |

A printable version of all of it, the **homefront Handbook**, is in [`handbook/homefront-handbook.pdf`](handbook/homefront-handbook.pdf).

## The parts

| Part | Job |
| --- | --- |
| **homefront** | The dashboard, remote, TV player and the glue to Windows. One `.exe` on the HTPC. |
| **Home Assistant** | Devices (lights, TV, camera, router), automations, alerts, presence, voice. Runs as a virtual machine on the HTPC. |
| **Jellyfin** | The film and series library, and streaming. A Windows service on the HTPC. |
| **LGTV Companion** | Switches the LG TV on and off and blanks the screen, from the PC. |
| **Tailscale** | Private access from outside the house. Nothing is opened to the internet. |
| **Home Assistant app** | Alerts, presence, widgets, Apple Watch, CarPlay and Siri on the phone. |

Everything is free software. The house keeps working in layers: if homefront stops, Home Assistant's automations and alerts carry on; if the internet drops, everything at home still works except cloud-connected bulbs.
