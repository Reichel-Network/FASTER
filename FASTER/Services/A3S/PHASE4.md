# Phase 4 — C# ObjectStream metadata (option B)

## What landed

### Phase 4a (read)

- `SerializerA3SMetadataStore` — native GZIP Java `ObjectInputStream` reader for classic remote `.a3s/*`.
- Supported **read** kinds: `autoconfig`, `sync`, `serverinfo`, `changelogs`, `events`.
- Golden fixtures under `FASTERTests/Fixtures/a3s-classic/`.

### Phase 4b (write)

- `JavaObjectOutputStream` + `A3SObjectStreamWriter` — classic ObjectStream **write** for the same kinds.
- `SerializerA3SMetadataStore.WriteMetadataAsync` / `WriteAllAsync` write GZIP blobs under `{contentRoot}/.a3s/`.
- `PreferNativeA3SMetadataStore` prefers C# for **reads and writes** when `a3sPreferNativeSerializer` is true; Bridge remains fallback.
- Golden round-trip tests: read fixture → write temp → read back (`A3SPhase4bWriteTests`).

## What still requires the JRE / bridge (“fully drop JRE”)

| Gap | Why it still needs the bridge |
| --- | --- |
| **Sealed `*.a3s.repository`** | Local AES `SealedObject` repo defs. FASTER settings remain source of truth; not required for Sync/Check/Build of community hosts. |
| **Shadow-compare vs live hosts / Java oracle** | Broader community corpus + optional bridge read of C#-written blobs before deleting bundled jlink/JAR from releases. |
| **Optional `.a3s2` (C)** | Still deferred. |

## Cutover checklist before removing bundled JRE

1. ~~Implement native write + round-trip fixtures~~ (4b).
2. Build/Publish end-to-end on Windows against a real host with **no** bridge JAR (Advanced path empty / missing).
3. Expand golden corpus; optional Java/bridge read of C#-written metadata.
4. Flip release packaging: stop shipping `a3s-bridge/` + jlink; keep Advanced bridge path for emergency override only, then delete.
5. Only then consider optional `.a3s2` dual-read.

## Operator note

With `a3sPreferNativeSerializer` (default on), Sync/Check/Events/Build metadata I/O can run **without Java** for classic unencrypted `.a3s/*`. Bridge remains available as fallback and for sealed local repo files if ever needed.
