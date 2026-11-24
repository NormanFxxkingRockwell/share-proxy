# Proxy Hotspot Share

**English** | [简体中文](README.zh-CN.md)

[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)](#) [![Dependency](https://img.shields.io/badge/dependency-zero-46A043)](#) [![Size](https://img.shields.io/badge/exe-%3C60KB-orange)](#) [![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

> One click to share your internet connection as a WiFi hotspot — with **TUN-mode proxy preferred**. A tiny (~58 KB), single-file, zero-dependency Windows tray app.

## Why

- You run Clash / mihomo / sing-box (TUN mode) on your PC and want your phone or other devices to **go through the proxy with zero configuration** — connect to this hotspot and traffic is NAT'ed straight into the TUN tunnel
- Windows' built-in Mobile Hotspot **auto-turns-off after ~5 minutes without clients**, has no device list, and no keep-alive; `netsh wlan start hostednetwork` is dead on modern drivers
- Existing tools are tens of MB, paid, or install resident services. This is one 58 KB exe running on the in-box .NET runtime — close it and it's gone

## Features

| Feature | Notes |
|---|---|
| Share on launch | Hotspot starts automatically; egress **prefers the TUN proxy adapter** (Clash / mihomo / sing-box / 0dcloud …), falls back to the current internet connection |
| One-click toggle | Main window button or tray right-click menu |
| SSID & password | Edit anytime; saving while running restarts the hotspot to apply immediately |
| Network status | Upstream WiFi (SSID / signal / IP), proxy TUN adapter state, system proxy, local core port probe (7890 / 7897 / 7891 / 17891 …) |
| Throughput | Live TUN adapter up/down rates and totals |
| Device list | Live IP + MAC of connected clients (raw ARP table + subnet auto-detect), balloon toast on join |
| Tray-resident | X only hides to tray (hotspot & keep-alive keep running); re-launch the exe to bring the window back |
| Auto re-arm | Defeats the "no clients for 5 min → auto-off" behavior |
| Start with Windows | Per-user Run key, no admin required |
| i18n | UI follows OS language (中文 / English), in-app toggle, choice remembered |

## Download

1. Grab `ProxyHotspotShare.exe` from [Releases](../../releases) — single file
2. Double-click → sharing starts
3. Connect your device to the hotspot (name & password shown in the window); traffic goes through the proxy if one is running in TUN mode

> SmartScreen may show an "unknown publisher" warning — normal for unsigned binaries, choose "Run anyway".
> `share.log` next to the exe is the diagnostic log (entries are in Chinese); attach it when filing an issue.

## Does it work without a proxy?

Yes. Hotspot sharing itself is the core feature: egress selection is *TUN proxy first → current internet connection as fallback*. Without a proxy the UI marks the state in **gray** ("not detected, sharing direct connection") and the hotspot works normally — e.g. share a wired desktop connection to your phone.

## How is this different from the built-in Mobile Hotspot?

Under the hood it's the same hotspot (same WinRT tethering API, same configuration the Settings app edits). The difference is the automation and visibility layer on top:

| | Built-in (Settings → Mobile hotspot) | This tool |
|---|---|---|
| Shared connection | Manual dropdown; **TUN/virtual adapters often don't show up** (fake-IP adapters like Clash TUN lack a standard connectivity verdict), and a selection breaks silently when the adapter is recreated | Auto-prefers the TUN proxy adapter, **re-evaluates every 3 s** — reloads/reconnects that recreate the TUN adapter are re-bound automatically |
| Recovery after drop | None — adapter resets, sleep/wake or TUN rebuilds leave it off | Auto re-arms within ~6 s, logged |
| "Auto-off after 5 idle minutes" | On by default (there is a hidden toggle, but even then resets can kill it) | Actively fought by keep-alive |
| After reboot | Usually needs manual re-enabling | Autostart + share-on-launch, deterministic |
| Proxy awareness | None | TUN detection / system proxy / core port probe / live throughput — confirm "traffic is really going through the tunnel" at a glance |
| Device list | Exists inside Settings (several clicks deep) | Always-visible panel + join toast, IP + MAC, 2 s refresh |
| Access path | Win11 quick panel: two steps | Tray right-click: one step; re-launch the exe to reopen |
| What the built-in does better | Band selection (2.4/5 GHz), Bluetooth sharing, friendly device names | Not exposed yet — PRs welcome |

Fair summary: if you don't run a proxy, the built-in hotspot is fine and you don't need this. Its audience is TUN proxy users (Clash / mihomo / sing-box) who want three annoyances gone — *can't pick the TUN adapter / hotspot breaks when the proxy reloads / Windows turns it off by itself* — plus a live panel proving the proxy is working.

## Technical notes

- **Zero-dependency single exe**: .NET Framework 4.x (in-box on Win10/11) + WinForms, ~58 KB
- **WinRT fully loaded at runtime**: the tethering API (`NetworkOperatorTetheringManager`) is WinRT; this project references no winmd — everything is driven at runtime through `Type.GetType("..., ContentType=WindowsRuntime")` + `dynamic` + a reflection-invoked `WindowsRuntimeSystemExtensions.AsTask`, avoiding the "no aggregate Windows.winmd / no facade assembly / legacy compiler" triple trap
- **Device detection**: raw `GetIpNetTable` P/Invoke (careful: the sizing probe returns 122 *by design*), subnet broadcast ping to refresh, hotspot subnet auto-adaptation
- **Build without an SDK**: `build.ps1` invokes the in-box `csc.exe` (C# 5.0 — the code intentionally stays compatible); if you have Windows, you can build

## Build from source

```
git clone https://github.com/NormanFxxkingRockwell/share-proxy.git
cd share-proxy
powershell -ExecutionPolicy Bypass -File build.ps1
```

## Known limitations

- Mobile hotspot requires a usable WLAN adapter (wired → hotspot is fine; WiFi → needs same-band concurrency support in the driver)
- Device list depends on the ARP table: a client shows up once it generates traffic (usually within seconds)
- UI strings: zh-CN and en are maintained; other languages welcome via PR

## License

[MIT](LICENSE)
