# gomp

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

Still to come: the **gomp client GUI** — the Avalonia desktop app a person
actually clicks around in (the launched tier-3 frontend, ADR-0010/0011), built
on top of `Gomp.Client`.

## Layout

```
src/Gomp.Protocol/    room-ops wire schema (rooms.proto)
src/Gomp.Host/        the room server (gomp-host) + ensemble-service.yaml package manifest
src/Gomp.Client/      the client core library (member side; no UI)
tests/Gomp.Host.Tests/
tests/Gomp.Client.Tests/
```

## Build & test

```bash
dotnet build Gomp.slnx
dotnet test  Gomp.slnx
```

### SDK dependency

gomp depends on `Ensemble.Client`: the host on **2.2.0** (the `FromEnv` +
spawn-token surface) and the client on **2.3.0** (which adds the identity-document
kit — `SignDocumentAsync` / `VerifyBindingAsync` / `VerifyPostSignature` — that
the authorship scheme above is built on). Until those are published to nuget.org
they're consumed from a **local feed** (`nuget.config` → `local-packages/`).
Refresh it from an ensemble checkout:

```bash
dotnet pack ../ensemble/clients/dotnet/Ensemble.Client/Ensemble.Client.csproj \
  -c Release -o local-packages
```

Once `Ensemble.Client` is on nuget.org, drop the local source from
`nuget.config`.

## Running a host

`gomp-host` is launched by the Ensemble daemon's supervisor with the ADR-0009
spawn contract in the environment (`ENSEMBLE_SOCKET` / `ENSEMBLE_SERVICE_NAME` /
`ENSEMBLE_SERVICE_TOKEN` / `ENSEMBLE_DATA_DIR`), plus `ROOM_HOST_OWNER` (the
operator E-address with admin authority) and optional `ROOM_HISTORY_MAX`. It is
installed as the `rooms` service package (`src/Gomp.Host/ensemble-service.yaml`).

### At-rest data

The host stores room history (length-delimited protobuf) and admin/room state
(JSON) under `ENSEMBLE_DATA_DIR` as **plaintext** today. The host legitimately
sees plaintext (ADR-0007's trust model), so this is only about disk/backup theft
of the host node. At-rest encryption is deferred to a **platform-level** feature
in Ensemble (transparent encrypted data dirs / a daemon-provided per-service
key) — ensemble ticket `20e37edc`. When it lands, the store moves to encrypted
SQLite behind the existing `RoomStore` / `AdminState` / `RoomCatalog` seam.
