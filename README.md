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
- **`Gomp.Protocol`** — the room-ops wire schema (`rooms.proto`), carried as the
  opaque payload of Ensemble's `SERVICE_TRANSPORT_RPC` envelope. Shared between
  the host and the (forthcoming) client.

Still to come: the **gomp client** — the Avalonia desktop app a person actually
clicks around in (the launched tier-3 frontend, ADR-0010/0011). It lands in this
repo alongside the host and shares `Gomp.Protocol`.

## Layout

```
src/Gomp.Protocol/   room-ops wire schema (rooms.proto)
src/Gomp.Host/       the room server (gomp-host) + ensemble-service.yaml package manifest
tests/Gomp.Host.Tests/
```

## Build & test

```bash
dotnet build Gomp.slnx
dotnet test  Gomp.slnx
```

### SDK dependency

gomp depends on `Ensemble.Client` (the `FromEnv` + spawn-token surface, 2.2.0).
Until that bump is published to nuget.org, it's consumed from a **local feed**
(`nuget.config` → `local-packages/`). Refresh it from an ensemble checkout:

```bash
dotnet pack ../ensemble/clients/dotnet/Ensemble.Client/Ensemble.Client.csproj \
  -c Release -o local-packages
```

Once `Ensemble.Client` 2.2.0 is on nuget.org, drop the local source from
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
