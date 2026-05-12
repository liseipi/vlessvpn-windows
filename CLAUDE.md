# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build

```bash
# SelfContained mode requires explicit platform; AnyCPU is unsupported
dotnet build VlessClient/VlessClient.csproj -c Debug -p:Platform=x64
dotnet build VlessClient/VlessClient.csproj -c Release -p:Platform=x64
```

There is no .sln file — build the .csproj directly.

## Architecture

This is an **unpackaged** WinUI 3 desktop app (no MSIX). The entry point is auto-generated in `App.g.i.cs`, which calls `XamlCheckProcessRequirements()` (P/Invoke into `Microsoft.ui.xaml.dll`) then launches `App`.

**Protocol pipeline** (inbound → outbound):
```
TCP listener (127.0.0.1:ListenPort)
  → inspect first byte: 0x05 = SOCKS5, A-Z = HTTP
  → VLESS header construction (BuildVlessHeader)
  → ClientWebSocket tunnel (wss://server:port/path?ed=...)
  → bidirectional relay with early-data passthrough
```

**Key layers:**

| Layer | Type | Role |
|---|---|---|
| `VlessProxyService` | Core | Single-listener proxy. Handles SOCKS5 handshake, HTTP CONNECT, and plain HTTP forwarding over VLESS+WebSocket. Owns `TcpListener`, accept loop, per-connection `WebSocket` relay. |
| `TunService` | Core | Wrapper around `hev-socks5-tunnel.exe`. Writes a temp YAML config pointing the TUN at the local SOCKS5 port, then manages the process lifecycle. |
| `ProxyManager` | Service | Orchestrates `VlessProxyService` + optional `TunService`. Exposes `Status` enum, manages start/stop lifecycle, aggregates log/connection events. |
| `SettingsService` | Service | Loads/saves `AppSettings` as JSON from `%AppData%/VlessClient/settings.json`. Synchronous `Load()` / `Save()`, async `SaveAsync()`. |
| `AutoStartService` | Service | Static utility. Reads/writes `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\VlessClient` with `--minimized` flag. |

**Data models:**
- `VlessConfig` — immutable-seeming config (use `Clone()`). Parses `vless://` URIs into structured fields: server, port, uuid, path, sni, wsHost, security, encryption, network. Also carries local-side settings: `ListenPort`, `EnableTun`, `TunAddress`, `TunPrefix`, `TunDns`. Serialized with `System.Text.Json` `JsonPropertyName` attributes.
- `AppSettings` — wrapping model: list of `VlessConfig`, `SelectedIndex`, `AutoStart`, `StartMinimized`, `EnableTun`, `ListenPort`, plus TUN defaults.

**UI:** Single `MainWindow` with a content stack: status card (connect button + status indicator), server config card (ComboBox selector + detail pane + import/export/new), proxy settings card (port + TUN toggle), system settings card (auto-start + minimize-to-tray toggles), log panel, status bar. The `App` class owns the `TaskbarIcon` (H.NotifyIcon.WinUI), global `ProxyManager` singleton, and `SettingsService` singleton.

**Window management:** Custom title bar via `ExtendsContentIntoTitleBar` + `SetTitleBar(AppTitleBar)`. Uses `MicaBackdrop`. `AppWindow.Resize()` sets initial size. Window close triggers minimize-to-tray when `StartMinimized` is on — `AppWindow.Closing` cancels the close and calls `this.Hide()` (an extension from H.NotifyIcon).

## Key constraints

- **SelfContained**: `WindowsAppSDKSelfContained` is `true` in the .csproj. Builds must specify `-p:Platform=x64` (or x86/arm64), never AnyCPU.
- **Requires admin**: `app.manifest` requests `requireAdministrator` for TUN interface creation. The app always runs elevated.
- **Unpackaged**: There is no MSIX package. Native DLLs (`hev-socks5-tunnel.exe`, `wintun.dll`, `msys-2.0.dll`) are deployed as `Content` with `CopyToOutputDirectory` from `runtimes/win-x64/`.
- **Tray icon in unpackaged apps**: `H.NotifyIcon` requires explicit `ForceCreate(enablesEfficiencyMode: false)` because there is no WinUI `Application` infrastructure to automatically manage the taskbar registration.
- **No MVVM**: The app uses code-behind with direct event handlers. `RelayCommand` is hand-rolled in `App.xaml.cs` for the tray icon only.
- **Config persistence**: `Save()` is synchronous (blocks on `SaveAsync().GetAwaiter().GetResult()`). Use `SaveAsync()` in async contexts. Directory is created on each save.
