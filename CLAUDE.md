# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows reverse-engineering effort to control Nura headphones (primarily the Nuraphone) without an Android/iOS device. The end goal: recover the long-lived per-device key once via the backend, persist it, and then run all ongoing control as **local RFCOMM encrypted sessions** with no further backend dependency.

Targets `.NET 10` (`net10.0-windows`). Bluetooth/RFCOMM control is Windows-only (Windows Bluetooth enumeration + Winsock RFCOMM). SDK `10.0.202` is pinned in `global.json`.

## Projects (one solution: `NuraDesktopApp.slnx`)

- `src/NuraLib` — the reusable SDK. The clean, production-intended surface. Entry point is `src/NuraLib/NuraClient.cs`.
- `src/NuraApp` — minimal console/TUI sample host. **The easiest live target to run** when validating SDK behavior.
- `src/NuraDesktopApp` (assembly `NuraDesktopConsole`) — the reverse-engineering / packet-analysis harness. Command-driven; this is where new protocol behavior gets explored before being ported into `NuraLib`.
- `src/NuraPopupWpf` — experimental WPF GUI client consuming the live SDK. Useful for view-model/binding validation, not the reference for SDK flow.
- `tests/NuraLib.Tests` — a plain console `Exe` (not a test-framework project) that runs packet round-trip and stability assertions and throws on failure.

Note: `NuraLib`, `NuraDesktopConsole`, and `NuraApp` each carry their **own** copies of some protocol/crypto/auth primitives (e.g. `GaiaFrame`, `NuraSessionCrypto`, `MessagePackLite`, `NuraAuthApiClient`). The console harness's copies are the experimental originals; `NuraLib`'s copies are the ported/stabilized versions. When changing protocol behavior, be clear which copy you're editing — they are not shared code.

## Commands

Build everything (use the local NuGet config):
```powershell
dotnet build .\NuraDesktopApp.slnx --configfile .\NuGet.Config
```

Run the SDK sample host (best first run / smoke test):
```powershell
dotnet run --project .\src\NuraApp\NuraApp.csproj
```

Run the library tests:
```powershell
dotnet run --project .\tests\NuraLib.Tests\NuraLib.Tests.csproj
```
There is no single-test filter — `tests/NuraLib.Tests/Program.cs` calls each test method directly. To run one in isolation, comment out the others or add a temporary call in `Main`.

Run the RE harness (command-driven):
```powershell
dotnet run --project .\src\NuraDesktopApp\NuraDesktopConsole.csproj -- <group> <command>
```
Command groups: `probe`, `protocol`, `headset`, `auth`, `flow`. Safe starting points with hardware connected: `probe devices`, `probe hw-info`, `flow init-to-start3`, `headset anc-toggle-test`.

If your environment needs repo-local dotnet/nuget state, set `DOTNET_CLI_HOME`, `NUGET_PACKAGES`, `APPDATA` etc. into `$PWD` subdirs (see README "Build And Run").

## Architecture

### NuraLib service model
`NuraClient` is constructed with a `NuraConfigState` (a wrapper over the persisted `NuraConfig`) and exposes three managers:
- `Auth` (`NuraAuthManager`) — email-code login, session resume/validation, provisioning.
- `Devices` (`NuraDeviceManager`) — `RefreshAsync()` discovers connected headsets as `ConnectedNuraDevice`; handles provisioning and local session setup.
- `Monitoring` (`NuraMonitoringManager`) — connection lifecycle polling; per-device indication monitoring lives on `ConnectedNuraDevice.StartMonitoringAsync()`.

**The library never writes files.** It mutates the in-memory `NuraConfig` and raises `NuraClient.RequestStateSave` with `NuraStateSaveReason` flags (`Configuration`, `Authentication`, `DeviceInventory`, `DeviceKey`, `Session`, `Bootstrap`). The host is responsible for persistence — `NuraConfigStore.LoadOrCreate`/`Save` is a provided convenience but any store works. Diagnostics flow through `NuraClient.OnLog`. See `docs/SDK-Guide.md` for the full host integration contract.

### Protocol layer (the core mechanic)
Device communication is GAIA packet framing over RFCOMM/SPP. In `NuraLib`, each operation is a command object under `src/NuraLib/Protocol/Commands/`:
- `NuraBluetoothCommand<TResponse>` defines `CreateFrame(runtime)` + `ParseResponse(runtime, response)`.
- Two families: `Unencrypted/` (challenge, device info — work before a session) and `AppEncrypted/` (profiles, ANC, buttons, dials, kickit/spatial — require an open encrypted session).
- `NuraCommandRunner.ExecuteAsync` ties a command to an `IHeadsetTransport` (real impl: `RfcommHeadsetTransport`) and an optional `NuraSessionRuntime` (the crypto context for encrypted commands).
- `NuraCommandFactory` builds command instances; the test suite asserts exact wire bytes for each command's request/response round-trip — keep those byte expectations in sync when touching framing or command IDs.

### Auth / provisioning flow
The persistent per-device key is recovered via a backend bootstrap chain (`session/start` → `start_1..start_4`); `session/start_4` returns `app_enc.key`, the long-lived key. The console harness implements the raw chain (`auth` / `flow` actions, `nura-auth.json` bootstrap state); `NuraLib` wraps the stabilized version in `NuraAuthManager` + `NuraProvisioningSupport`. Once the key is persisted in config (`deviceKey`), local encrypted control needs only a freshly generated local nonce, no backend.

### Console harness structure
`Program.cs` → `ActionHandler` resolves `<group> <command>` to an `IAction` (under `src/NuraDesktopApp/Actions/<Group>/`). Every run writes a timestamped log to `logs/`.

## Configuration & sensitive data

- `nura-config.json` (cwd, gitignored) — durable host config: auth state, device inventory, persistent `deviceKey`, `isNuraNowDevice`, `lastProvisionedUtc`. camelCase JSON. Root model: `src/NuraLib/Configuration/NuraConfig.cs`. Sample: `src/NuraDesktopApp/nura-config.sample.json`.
- `nura-auth.json` (gitignored) — console-harness bootstrap/auth state (`asid`, `usid`, recovered `app_enc.key`/`nonce`). Harness-only, not the SDK's durable model.
- Logs and both JSON files contain device keys, access tokens, session IDs, and emails. Never print them or commit them.

## Conventions

- Implemented commands map to confirmed Nuraphone behavior aligned with the decompiled Android app. Several public surfaces are deliberately **not implemented yet** (profile rename, head detection, multipoint, voice-prompt-gain readback, ProEQ) — check current state before assuming a feature exists end-to-end.
- This is an RE harness for hardware you may damage: be cautious with unknown setters, firmware commands, and unexplored command families that write to the headset.
- Brace style is K&R (`{` on same line); `ImplicitUsings` and `Nullable` are enabled across all projects.
