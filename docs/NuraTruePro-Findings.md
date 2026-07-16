# NuraTrue Pro: provisioning, local control, and the "deactivated device" problem

Field notes from a live reverse-engineering session against a real **NuraTrue Pro** (June 2026).

The repo previously stated that device-family coverage was "still being expanded from the confirmed
Nuraphone path". This documents the first **confirmed end-to-end NuraTrue Pro provisioning and local
encrypted control**, the TruePro-specific protocol differences found along the way, and an unresolved
problem: the device produces **no audio**, and we could not fix that.

All identifiers below (serial, MAC, keys, tokens, email) are redacted.

---

## TL;DR

**What now works on the NuraTrue Pro (verified on hardware):**

- Backend-assisted provisioning end-to-end — the persistent `app_enc.key` is recovered and persisted.
- Local RFCOMM encrypted session (challenge/response handshake succeeds with the recovered key).
- Reading current profile id, profile names, visualisation data, and **button configuration**.
- Reading/writing **personalisation mode** (Personalised ↔ Neutral).
- `NuraApp` runs the full lifecycle cleanly: discover → provision → refresh → start monitoring, no errors.

**What does NOT work / remains open:**

- **The earbuds still output no audio.** Root cause is understood (see
  [The no-audio problem](#the-no-audio-problem)) but not solved. Bypassing personalisation
  (Neutral mode) did **not** restore sound.
- Several commands are unsupported on this device and some **hard-drop the RFCOMM link**.

> This is **not** an "unbrick". Control and provisioning are recovered; audio is not.

---

## Test device

| | |
|---|---|
| Model | NuraTrue Pro (Bluetooth name `NuraTrue Pro 2xx`) |
| Firmware | `400188` |
| Backend | `https://api-p3.nuraphone.com/` |
| Date | 2026-06-19 / 2026-06-20 |
| History | Device had been **remotely deactivated** by the vendor; the official app no longer usable |

---

## 1. The Nura backend is still alive

As of 2026-06-20, `api-p3.nuraphone.com` is **fully operational** — not a parked domain:

```
POST /app/session          -> 200   (msgpack; returns asid + app session token)
POST /auth/login_via_email -> 200   (sends the 6-digit code)
POST /auth/login_via_email_verify -> 200 (returns access token; ~10-year expiry)
POST /auth/validate_token  -> 200
POST /end_to_end/session/start ... start_5 -> 200
```

Response headers show the real origin app behind Cloudflare (`X-Powered-By: Phusion Passenger`,
`X-Runtime`, `Content-Type: application/msgpack`), and error bodies are application-level
(e.g. `param is missing or the value is empty: os`), not edge errors.

**Implication:** the one-time bootstrap that recovers the persistent device key still works today.
Anyone with a Nura device should recover and persist their `app_enc.key` while this remains up.

Note: the legacy `api-p1` host still resolves but rejects `api-p3` sessions (`401 Unauthenticated`);
the automatic 404-retry fallback to it is a red herring in logs.

---

## 2. NuraTrue Pro provisioning works — with one hard requirement

The full chain (`session/start` → `start_1` → … → `start_5`) completes against the TruePro and
returns `app_enc.key`. Every headset packet in the chain answered `status=0x00`, including the
~45-packet bulk exchange in `start_3`. The recovered key is **stable** — re-provisioning after
clearing it returned the identical key.

**Gotcha — both earbuds must be connected.** With only one bud active, the chain runs all the way to
`start_3` and then the backend rejects it:

```
HTTP 422  {"s":false,"e":["Only one earbud connected"]}
```

The failure surfaces late and the message is easy to miss. Both buds out of the case and connected →
the chain completes in ~6 seconds.

### GAIA framing

The TruePro provisioning succeeded with GAIA version `0x04`, flags `0x00`
(`NuraProvisioningSupport.ResolveBootstrapGaia` maps `NuraTruePro` → `(0x04, 0x02)`, but the device
was classified as `NuraTrue` → `(0x04, 0x00)` at the time — and it worked). Worth confirming which
is actually correct; the two may be interchangeable here.

---

## 3. Device discovery: the harness cannot see a TruePro

`src/NuraUtilityConsole/Library/Transport/BluetoothDeviceProbe.cs` (`FindConnectedNuraphones`) filters on:

```csharp
deviceInfo.Name.StartsWith("Nuraphone", StringComparison.OrdinalIgnoreCase)
```

A NuraTrue Pro advertises `NuraTrue Pro 2xx`, so **`probe devices` reports 0 devices** even with the
buds connected. `NuraLib`'s copy filters on `"Nura"` and finds it correctly.

This makes the console harness effectively Nuraphone-only for anything requiring device discovery.
Use `NuraApp` for TWS devices, or widen the harness filter.

(Neither filter would match a Denon-branded unit, e.g. "Denon PerL Pro".)

---

## 4. Device type misclassification

`NuraDeviceCapabilities.ResolveType("NuraTrue Pro 201")` returned **`NuraTrue`**, not `NuraTruePro`.

The exact-match arm (`"nuratrue pro"`) misses because of the numeric suffix, and the fallback chain
tested `Contains("nurapro")` (no match — the name has a space) *before* `Contains("nuratrue")`
(match). Fixed by checking `Contains("nuratrue pro")` ahead of the generic `nuratrue` arm.

This matters: `Info.DeviceType` is resolved from the **persisted `config.Type` string**
(`NuraDevice.UpdateConfig`), so a bad classification is written to `nura-config.json` and sticks
across runs even after the resolver is fixed.

---

## 5. Button configuration: the TruePro has no triple-tap

`GetButtonConfiguration` threw `Button configuration response length 0 did not match expected 8`,
aborting the whole state refresh.

Probing the device directly with each command variant (raw payload `00 <cmdLow> <profileId>`):

| command low byte | profile 0 / 1 / 2 | profile 255 |
|---|---|---|
| `0x51` (legacy) | empty | empty |
| `0x73` (triple-tap) | empty | empty |
| **`0xB7`** (fw > 510, double-tap) | **6 bytes** `08 12 13 0b 00 00` | `00 00 00 00 00 00` |

**Findings:**

- The TruePro serves button config on **`0xB7` as a 6-byte double-tap layout**
  (L/R × single, double, hold) — it has **no triple-tap** at all.
- `NuraDeviceCapabilities` unconditionally granted `TripleTap` to the TruePro family, so
  `GetButtonConfigurationCommand.GetCommandIdLowByte()` picked `0x73`, which the device answers with
  an **empty payload**, and `RequireExactLength(…, 8)` then threw.
- Button config is **per-profile**; `255` is not a valid sentinel for it (returns zeros).
- Firmware `400188` is the **latest available** (see §8), so this is not fixable by updating.

Decoded live config (`08 12 13 0b 00 00`):

| Gesture | Left | Right |
|---|---|---|
| Single tap | `0x08` ToggleSocial | `0x12` PlayPauseAndAnswerCall |
| Double tap | `0x13` VoiceAssistant | `0x0B` NextTrack |
| Tap & hold | `0x00` None | `0x00` None |

---

## 6. Commands that hard-drop the RFCOMM link

Two reads cause the TruePro firmware to **close the RFCOMM connection** rather than return an error:

| Command | Plain payload | Result |
|---|---|---|
| `GetANCLevel` | `01 02 <profile>` | no response; socket closed ~3–4 s later |
| `GetGlobalAncEnabled` | — | no response; socket closed |

Worse, **reconnecting immediately after such a drop fails** with Winsock `WSAEINVAL`
("An invalid argument was supplied") for some seconds — so one bad command poisons the link and
every subsequent read *and* the monitoring loop fails.

Conclusion: the TruePro has **ANC on/off + passthrough but no numeric ANC level and no global-ANC
toggle** in the queried form. Both are now gated off for the family. Do not rely on
reconnect-after-drop as a recovery strategy — avoid sending the offending command.

---

## 7. Personalisation uses the *classic* enabled-flag commands

`ConnectedNuraDevice.SetPersonalisationModeAsync` routes non-Nuraphone devices through the TWS
kickit-**state** path. On the TruePro that path returns empty (`Kickit state response was too
short: 0`).

The device actually responds to the **classic enabled-flag commands**:

| | payload | behaviour |
|---|---|---|
| `GetKickitEnabled` | `00 B4` | returns Personalised / Neutral |
| `SetKickitEnabled` | `00 B3 <01\|00>` | sets Personalised / Neutral — **verified, and persists across sessions** |

So `UsesClassicKickitCommands()` (Nuraphone-only) is the wrong predicate for personalisation. Added
`UsesEnabledFlagPersonalisation()` = Nuraphone **or** NuraTruePro.

### Other reads that return empty (non-fatal)

`GetAncState`, kickit state (immersion), and spatial state all return zero-length payloads on this
device. They don't drop the link, but every parser rejects them.

---

## 8. Firmware `400188` is the latest

Asked the backend directly for this serial:

```
POST /end_to_end/upgrade -> 404  {"s":false,"e":["No upgrade available"]}
```

There is no newer firmware. Note `400188` is *below* the `800020` minimum that
`GetMinimumFirmwareVersion` assigns to `NuraTruePro`, so the version scheme in that table doesn't
match this hardware either.

---

## The no-audio problem

**This is the unsolved part.**

The earbuds connect to Windows with a healthy A2DP render endpoint
(`Headphones (NuraTrue Pro 2xx)`, state ACTIVE) but produce **no sound**.

What the device reports over the local encrypted session:

```
current profile id = 255 (0xFF)      <- no hearing profile active
profile 0 name = <null>   visualisation = <null>
profile 1 name = <null>   visualisation = <null>
profile 2 name = <null>   visualisation = <null>
SelectProfile(0) -> empty ack, current profile id stays 255   <- slot is genuinely empty
```

**All three hearing-profile slots are empty and no profile is active.** Selecting a slot does not
stick, which rules out "we're querying profiles with the wrong command variant" — the slots really
are blank. The vendor's remote deactivation appears to have **wiped the personalisation profiles**.

### The backend does not hold a restorable profile

Re-ran the full provisioning chain on working auth and inspected every stage response:

- `session/start_1…5` contain only `t:"u"` (unencrypted) and `t:"r"` (run/crypto) packets.
- **No `t:"t", c:"profiles"` action at any stage.**
- `NuraAuthResponseParser.ExtractProfileVisualisationSlots` — the only profile hook in the codebase —
  extracts **visualisation metadata only** (display curves + names), and it is applied for display.
  There is **no code path anywhere that writes a hearing profile to the device.**

So the backend surfaces no restorable profile for this account, and even if it did, the profile
appears to be *display* data rather than the DSP profile.

### Neutral mode does not restore audio

Hypothesis: with personalisation bypassed, the device should play flat/unprocessed audio without
needing a profile. Result:

```
current personalisation mode = Personalised
SetKickitEnabled(False) -> ack
personalisation mode after = Neutral     (persists across sessions)
```

The mode change is real and sticks — **but the earbuds are still silent.** So the silence is *not*
(only) the missing profile. Something keeps audio output disabled even with the personalisation
engine bypassed.

### Open questions

1. Is the vendor deactivation disabling audio output at a level below the known command set
   (a master output/enable flag, a licence/entitlement check in firmware)?
2. Windows-side routing has not been fully excluded — the machine has several active render
   endpoints, and we could not programmatically confirm the default device / mute state / whether
   the stream actually reaches the buds. **Anyone reproducing this should first confirm the A2DP
   "Headphones" endpoint is the default, unmuted, and that the level meter moves.**
3. Do device-generated **voice prompts / ANC confirmation tones** still play? If those are silent
   too, output is locked device-side rather than a routing problem. (Untested.)
4. Is there a profile-**write** command at all (needed for any restore, even a synthesized flat
   profile)? Not present in the current command set.
5. Recreating a profile means replicating Nura's on-device hearing test (tone playback + OAE
   measurement + proprietary profile computation). Not implemented; a large effort. A Frida/logcat
   capture of a real personalisation run would turn this from blind RE into replay-and-adapt.

---

## Code changes

### `src/NuraLib/Devices/NuraDeviceCapabilities.cs`

- `ResolveType`: match `"nuratrue pro"` / `"nuratrue sport"` before the generic `"nuratrue"` arm.
- Split `NuraTruePro` out of the shared `NuraTruePro`/`NuraTrueSport` case (Sport left unchanged —
  no hardware to verify it).
- `NuraTruePro`: removed `TripleTap`; masked off `AncLevel` and `GlobalAncToggle`.

### `src/NuraLib/Devices/ConnectedNuraDevice.cs`

- Added `TryRefreshStepAsync(...)` — each optional refresh read is best-effort; on failure it logs a
  warning and tears the (possibly dead) session down so the next step reconnects. A single
  unsupported command can no longer abort refresh + monitoring.
- Added `ResetLocalSessionForReconnectAsync()` — disposes and **nulls** `_session`.
  (`EnsureConnectedAsync` only reconnects when `_session is null`; after a device-initiated drop the
  dead session object remained, so nothing ever reconnected.)
- Added `UsesEnabledFlagPersonalisation()` (Nuraphone **or** NuraTruePro) and used it for
  personalisation get/set instead of `UsesClassicKickitCommands()`.
- Wrapped the optional state/configuration reads in `TryRefreshStepAsync`.

### `tests/NuraLib.Tests/` (live probe)

`LiveButtonProbe.cs` + a `probe` dispatch in `Program.cs`. Uses the project's existing
`InternalsVisibleTo("NuraLib.Tests")` to drive real hardware:

```powershell
dotnet .\tests\NuraLib.Tests\bin\Debug\net10.0-windows\NuraLib.Tests.dll probe buttons
dotnet .\tests\NuraLib.Tests\bin\Debug\net10.0-windows\NuraLib.Tests.dll probe profiles
dotnet .\tests\NuraLib.Tests\bin\Debug\net10.0-windows\NuraLib.Tests.dll probe select 0
dotnet .\tests\NuraLib.Tests\bin\Debug\net10.0-windows\NuraLib.Tests.dll probe perso 0   # 0 = Neutral
```

It opens an RFCOMM session with the persisted device key, performs the handshake, and dumps **raw
decrypted payloads** without the strict length parsers — which is how the `0xB7` button-config
variant was found. Requires a provisioned device in `nura-config.json`.

The existing packet round-trip tests still pass (they set `SupportedButtonGestures` directly on
`NuraDeviceInfo`, so they're unaffected by the capability-table changes).

### `global.json` (local only — probably don't merge)

`rollForward` `latestPatch` → `latestFeature`, because the pinned SDK `10.0.202` wasn't installed and
`latestPatch` won't cross the `10.0.2xx` → `10.0.3xx` feature band.

---

## Reproducing

1. Both earbuds out of the case and connected to Windows.
2. Seed `nura-config.json` with auth (or let `NuraApp` do the email-code login):
   `dotnet run --project .\src\NuraApp\NuraApp.csproj`
3. It will discover the device, provision it (recovering `app_enc.key`), refresh state, and start
   monitoring.
4. The persistent key lands in `nura-config.json` → all later control is local, no backend.

**Sensitive:** `nura-config.json`, `nura-auth.json`, `logs/`, and any probe output contain device
keys, access tokens, session ids, and the account email. Don't commit them.
