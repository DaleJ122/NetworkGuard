# NetworkGuard

A DNS-based network traffic monitor for parental oversight. Runs as a Docker container on your home server and monitors all devices on your WiFi — no per-device app installation needed.

## How It Works

NetworkGuard acts as your network's DNS server. When any device on your WiFi tries to visit a website, the DNS query passes through NetworkGuard first. It logs the request, checks it against blocklists, and alerts you if a flagged site is accessed.

```
[All WiFi Devices] → DNS query → [NetworkGuard] → [Upstream DNS (8.8.8.8)] → Internet
                                       ↓
                                  Log + Categorize
                                       ↓
                                  [Blazor Dashboard]
```

## Features

- **DNS Proxy** — Intercepts and forwards DNS queries on port 53
- **Domain Categorization** — Blocklists for adult, gambling, hacking, malware, drugs, violence, VPN/proxy, and dating sites
- **Keyword Detection** — Flags domains containing suspicious keywords (e.g. "porn", "xxx")
- **Per-Device Monitoring** — Identifies devices by MAC address with auto-detection of hostnames via DHCP
- **Real-Time Alerts** — Dashboard updates every 5 seconds when flagged sites are accessed
- **Activity Charts** — Line charts showing device activity over time with configurable time ranges
- **Device Management** — Rename devices, exclude from monitoring, block internet access with timed duration
- **Per-Device Domain Ignore** — Hide noisy domains for specific devices without affecting others
- **Trace Log Export** — One-click copy of a device's full DNS log for analysis
- **Blocklist Management** — Add/remove domains and categories from the web UI
- **DHCP Server** — Built-in dnsmasq container so devices get NetworkGuard as their DNS automatically
- **Dark Mode** — Full dark theme optimized for ultrawide monitors
- **Mobile Responsive** — Hamburger menu, stacking layouts, touch-friendly on phones

## Requirements

- Docker and Docker Compose
- A machine on your network to run it (home server, Raspberry Pi, mini PC, etc.)

## Quick Start

1. Clone the repo:
   ```bash
   git clone https://github.com/DaleJ122/NetworkGuard.git
   cd NetworkGuard
   ```

2. Edit `dnsmasq.conf` — set your network's IP range, gateway, and the server's IP as the DNS server. Add any static DHCP reservations for your devices.

3. Edit `docker-compose.yml` — update the `ASPNETCORE_URLS` port if needed (default `8082`).

4. Start it:
   ```bash
   docker compose up -d --build
   ```

5. Disable DHCP on your router and let NetworkGuard's dnsmasq handle it. This ensures all devices get NetworkGuard as their DNS server directly.

6. Open the dashboard at `http://<server-ip>:8082`

## Configuration

### Environment Variables (docker-compose.yml)

| Variable | Default | Description |
|---|---|---|
| `Dns__ListenIp` | `0.0.0.0` | IP to listen on for DNS |
| `Dns__ListenPort` | `53` | DNS listening port |
| `Dns__UpstreamServer` | `8.8.8.8` | Upstream DNS server |
| `Database__UnflaggedRetentionHours` | `12` | Hours to keep unflagged logs |
| `Database__FlaggedRetentionDays` | `7` | Days to keep flagged logs |
| `Database__AlertRetentionDays` | `7` | Days to keep alerts |

### Blocklists

Text files in `src/NetworkGuard/blocklists/`, one domain per line. The filename becomes the category. Supports hosts-file format. Manage from the Settings page or edit files directly.

### DHCP

Edit `dnsmasq.conf` for your network. Key settings:
- `dhcp-range` — Your network's IP range
- `dhcp-option=option:router` — Your router's IP
- `dhcp-option=option:dns-server` — This server's IP
- `dhcp-host` — Static reservations for specific devices

## Tech Stack

- .NET 10 / Blazor Server
- EF Core + SQLite
- Chart.js for activity graphs
- dnsmasq for DHCP
- Docker with host networking

## Limitations

- **VPNs** bypass DNS monitoring entirely
- **DNS-over-HTTPS** (DoH) in browsers bypasses local DNS — disable in browser settings
- **Mobile data** bypasses WiFi DNS
- Cannot see the content of HTTPS traffic — only which domains are accessed
- Cannot see search queries (e.g. Google searches) — only the domain `google.com`
