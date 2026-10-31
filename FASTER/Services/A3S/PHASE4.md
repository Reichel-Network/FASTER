# Phase 4 — C# ObjectStream metadata (option B)

## Product default (no JRE)

With defaults (`a3sPreferNativeSerializer=true`, `a3sEnableMetadataBridgeFallback=false`):

- `A3SMetadataStoreFactory` returns **`SerializerA3SMetadataStore` only**
- Sync / Check / Events / Build / Publish classic **unencrypted** `.a3s/*` needs **no Java**, no bridge JAR, no jlink
- Proven against the reichel-arma golden fixtures (`FASTERTests/Fixtures/a3s-classic/`)

Operators should not install a JRE for A3S. The Java helper is Advanced-only.

## What landed

### Phase 4a (read)

- Native GZIP Java `ObjectInputStream` reader for `autoconfig`, `sync`, `serverinfo`, `changelogs`, `events`

### Phase 4b (write)

- Native `ObjectOutputStream` writer for the same kinds under `{contentRoot}/.a3s/`
- Golden round-trip tests (`A3SPhase4bWriteTests`)

### Packaging (this step)

- Bridge **off by default** (`a3sEnableMetadataBridgeFallback=false`)
- Settings → Advanced: optional “Enable Java bridge fallback” + JAR path
- Releases need not ship `a3s-bridge/` or a JRE for A3S

## Sealed `*.a3s.repository`

Arma3Sync’s local encrypted repo definition files (`*.a3s.repository`, AES `SealedObject`) are **not used by FASTER**.

| Concern | FASTER behavior |
| --- | --- |
| Repo list / URLs / publish settings | User settings (`a3sRepositories`, …) — **source of truth** |
| Remote classic `.a3s/*` on hosts | Native C# ObjectStream read/write |
| Sealed local Arma3Sync config | **Unused** — do not expect FASTER to create/read these |
| If you ever need sealed files | Opt-in bridge fallback + JAR (not a product path) |

## Optional bridge fallback

Enable only when diagnosing or forcing legacy bridge I/O:

1. Settings → Advanced → **Enable Java bridge fallback**
2. Point at `a3s-metadata-bridge-*-all.jar` (or leave empty for `a3s-bridge/` next to FASTER)
3. Provide a JRE/JDK (`JAVA_HOME` / `PATH`)

Uncheck native serializer entirely only to force bridge-for-all (emergency).

## Remaining (non-blocking for daily use)

| Item | Notes |
| --- | --- |
| Broader community corpus | Extra golden repos beyond reichel-arma |
| Optional `.a3s2` (C) | Deferred; classic `.a3s` is the supported wire format |
| Shipping bridge/jlink | **Not required**; omit from release zips unless you want the Advanced fallback pre-bundled |

## Operator note

**Sync, Build, and Publish of classic `.a3s` repositories work without Java.** Point Repositories at an autoconfig URL (e.g. Tobias’s reichel-arma host), sync to a local folder, and build/publish with C# metadata I/O.
