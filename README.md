# gomp

> ⚠️ **Hobby project — don't take it seriously.** This is just someone having fun
> building things in their spare time. It is **not secure**, **not complete**, and
> **not production-ready**. There are no guarantees, no support, and no warranty —
> don't trust it with anything that matters. Use at your own risk.

**G.O.M.P. — "Get Outta My Pub."** A small, self-hostable, decentralized
take on a Discord server, built on [Ensemble](https://github.com/boxsie/ensemble).
A room is a place you walk into; while its host is up, you can join, chat, and
catch up on what you missed.

gomp is an Ensemble *app*, not part of the Ensemble platform — it consumes the
`Ensemble.Client` SDK exactly like any other app. Two halves:

- **`Gomp.Host`** (`gomp-host`) — the room **server**. A daemon-supervised
  Ensemble service (ADR-0007 / ADR-0009) that registers a host identity plus one
  sub-identity per room, fans member posts out with verbatim sender-signed
  attribution, keeps per-room history for since-cursor backfill, tracks presence,
  and authorizes admin operations against an owner + designated admins.
- **`Gomp.Client`** — the client **core** library: the member's side of a room.
  Owns the one rooms-client identity, dials rooms, signs posts, and verifies
  peers — `RoomSession` (the room-ops speaker), `PostSigner`, `MemberDirectory`
  (verify-once + cache), and `GompClient` (join/admin + event routing). Pure
  logic behind a transport seam with fake-transport unit tests; no UI.
- **`Gomp.App`** (`gomp`) — the desktop **GUI**: an Avalonia app (the launched
  tier-3 frontend, ADR-0010/0011) on top of `Gomp.Client`. Connect, create a
  room, join one by address, chat, and see who's around. MVVM with the network
  behind an `IGompGateway` seam, so the view-models are unit-tested with no
  daemon.
- **`Gomp.Protocol`** — the room-ops wire schema (`rooms.proto`), carried as the
  opaque payload of Ensemble's `SERVICE_TRANSPORT_RPC` envelope. Shared between
  the host and the client.

### Verifiable authorship (ADR-0007 §6)

Posts are member-signed so a member's words are unforgeable even by an untrusted
host — "distribute-binding-once / sign-cheaply-per-message" (the TLS/PKI
pattern). At join a member hands the host its `IdentityBinding` (a `Hello`); the
host distributes it via the **roster** / presence, so every member ends up
holding every other's binding. Each member verifies a peer's binding **once**
(ML-DSA-65, via the SDK's `VerifyBinding` RPC) and pins the bound ed25519 key;
thereafter each post carries only a 64-byte ed25519 signature, verified
in-process with no daemon round-trip. The host stores and relays bindings
verbatim and never verifies them. v1 may run **host-trusted** (sign + distribute
bindings, skip the verify calls) and flip verification on with zero wire change.

The GUI renders this trust per message: **verified** (green check, unforgeable),
**unverified** (host-attributed, no badge), or **forged** (red, dimmed — the
claimed author's signature didn't check out).

## Layout

```
src/Gomp.Protocol/    room-ops wire schema (rooms.proto)
src/Gomp.Host/        the room server (gomp-host) + ensemble-service.yaml package manifest
src/Gomp.Client/      the client core library (member side; no UI)
src/Gomp.App/         the Avalonia desktop GUI (assembly: gomp)
tools/Gomp.Snapshot/  headless Skia render harness — PNGs of the UI without a display
tests/Gomp.Host.Tests/
tests/Gomp.Client.Tests/
tests/Gomp.App.Tests/
```

## The app

```bash
dotnet run --project src/Gomp.App          # talks to your local daemon (ENSEMBLE_SOCKET)
dotnet run --project tools/Gomp.Snapshot -- /tmp/shots   # render the screens to PNGs
```

A dark, Twitch-flavoured single window: a left rail of your rooms + identity, the
chat in the middle with coloured per-member names and trust badges, and the
member list (with owner-only remove) on the right. The snapshot harness is the
way to eyeball UI changes on this box (GNOME Wayland blocks screenshot tooling).

## Build & test

```bash
dotnet build Gomp.slnx
dotnet test  Gomp.slnx
```

### SDK dependency

gomp depends on **`Ensemble.Client` 2.3.0** (from nuget.org): the `FromEnv` +
spawn-token surface the host runs on, plus the identity-document kit —
`SignDocumentAsync` / `VerifyBindingAsync` / `VerifyPostSignature` — the
verifiable-authorship scheme above is built on. Plain `dotnet restore` pulls it;
no local feed.

## Running a host

`gomp-host` is launched by the Ensemble daemon's supervisor with the ADR-0009
spawn contract in the environment (`ENSEMBLE_SOCKET` / `ENSEMBLE_SERVICE_NAME` /
`ENSEMBLE_SERVICE_TOKEN` / `ENSEMBLE_DATA_DIR`), plus `ROOM_HOST_OWNER` (the
operator E-address with admin authority) and optional `ROOM_HISTORY_MAX`. It is
installed as the `rooms` service package (`src/Gomp.Host/ensemble-service.yaml`).

### Defining rooms headlessly

A host with no frontend defines its rooms by config, not by a live command
channel: drop a `rooms.yaml` into the service data dir (`$ENSEMBLE_DATA_DIR/rooms.yaml`)
and start. On boot the host reconciles its catalog from it.

```yaml
rooms:
  - name: lobby
    kind: open            # open | friends | invite
  - name: mates
    kind: friends
  - name: vip
    kind: invite
    members: [Ealice, Ebob]
```

Semantics are **ensure-exists**: config only ever *adds* — a declared room that
doesn't exist is created; new `members` on an existing invite room are added.
Config **never** deletes a room or changes its kind, so editing the file can't
nuke a room with history, and rooms created live (by an admin client) coexist
with config-defined ones. Because the data dir is keyed by service *name*, the
catalog survives upgrades, rollbacks and restarts. A malformed `rooms.yaml` is
logged and skipped — existing rooms are unaffected.

### At-rest data

The host stores room history (length-delimited protobuf) and admin/room state
(JSON) under `ENSEMBLE_DATA_DIR` as **plaintext** today. The host legitimately
sees plaintext (ADR-0007's trust model), so this is only about disk/backup theft
of the host node. At-rest encryption is deferred to a **platform-level** feature
in Ensemble (transparent encrypted data dirs / a daemon-provided per-service
key) — ensemble ticket `20e37edc`. When it lands, the store moves to encrypted
SQLite behind the existing `RoomStore` / `AdminState` / `RoomCatalog` seam.
