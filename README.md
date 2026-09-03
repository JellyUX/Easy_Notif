# Easy Notif

<p align="center">
  <img src="https://github.com/JellyUX/Easy_Notif/actions/workflows/ci.yml/badge.svg" alt="Build">
  <img src="https://img.shields.io/github/v/release/JellyUX/Easy_Notif" alt="Version">
  <img src="https://img.shields.io/badge/Jellyfin-10.11.10%2B-orange" alt="Jellyfin">
  <img src="https://img.shields.io/badge/license-GPL--3.0-green" alt="License">
</p>

An internal **email notification service** for your Jellyfin server. It replaces ad-hoc messaging
with three things:

- a **new-media newsletter** on a schedule you choose (weekly on a given day, monthly, every N days),
  with cover art and links back to each title;
- a **personalised weekly recap** for each user: what they watched this week and their running total
  for the year;
- **manual announcements** written from the dashboard, plain text or a full HTML template.

Each user picks which categories they want and sets their own contact address. The plugin reads the
library and playback data only. It never changes anything.

Part of the [JellyUX](https://github.com/JellyUX) plugin family.

---

## Prerequisites

- **Jellyfin 10.11.10 or newer** (10.11.x line).
- **[File Transformation plugin](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)**
  - required, used to inject the per-user settings panel into the web client. Without it the panel
  does not appear; the rest of the plugin still works.
- A **[Resend](https://resend.com)** account and a domain you can add DNS records to, for sending
  the emails.

## Installation

1. Jellyfin dashboard: **Plugins > Repositories > Add**, paste one of:
   ```
   https://raw.githubusercontent.com/JellyUX/Easy_Notif/main/manifest.json
   ```
   or, for the whole JellyUX suite:
   ```
   https://raw.githubusercontent.com/JellyUX/.github/main/manifest.json
   ```
2. **Plugins > Catalog**, install **Easy Notif**, restart Jellyfin.
3. Open **Dashboard > Plugins > Easy Notif** and fill in the Settings tab (Resend API key, sender
   address, public server URL).

## License

GPL-3.0. See [LICENSE.md](LICENSE.md).
