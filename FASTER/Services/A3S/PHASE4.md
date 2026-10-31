# Phase 4 — C# ObjectStream metadata (option B)

## What landed

- `SerializerA3SMetadataStore` — native GZIP Java `ObjectInputStream` reader for classic remote `.a3s/*`.
- Supported **read** kinds: `autoconfig`, `sync`, `serverinfo`, `changelogs`, `events`.
- Golden fixtures under `FASTERTests/Fixtures/a3s-classic/` (reichel-arma capture).
- Product default: `Settings.a3sPreferNativeSerializer = true` → `PreferNativeA3SMetadataStore` prefers C# for reads; **writes still go through the Java bridge**.
- Advanced checkbox in Settings to turn native preference off (force bridge reads).

## What still requires the JRE / bridge (“fully drop JRE”)

| Gap | Why it still needs the bridge |
| --- | --- |
| **Write path** (`WriteMetadataAsync` / `WriteAllAsync`) | C# Object**Output**Stream writer for the same domain graph is not implemented. Phase 2 Build/Publish still shells `a3s-metadata-bridge write`. |
| **Sealed `*.a3s.repository`** | Local AES `SealedObject` repo defs. FASTER settings remain source of truth; sealed files only matter if something still expects Arma3Sync local config. Not required for Sync/Check of community hosts. |
| **Changelog / events write fidelity** | Reads work; publishing bump/changelog/events still via bridge write envelope. |
| **Protocol edge subclasses on write** | Reader understands Http/Ftp/Sftp/Webdav/Socks graphs; writer would need matching `serialVersionUID`s and field layout. |
| **Shadow-compare vs live hosts** | Fixtures cover one known-good repo; broader community corpus should be regression-tested before deleting the bundled jlink/JAR from releases. |
| **Optional `.a3s2` (C)** | Explicitly out of scope until classic (B) write + publish is proven. |

## Cutover checklist before removing bundled JRE

1. Implement `SerializerA3SMetadataStore` **write** (or dual-write) and round-trip fixtures: C# write → Java/bridge read → C# read.
2. Build/Publish path uses native write end-to-end on Windows against a real host.
3. Expand golden corpus (multiple community repos / revisions).
4. Flip release packaging: stop shipping `a3s-bridge/` + jlink; keep Advanced bridge path for emergency override only, then delete.
5. Only then consider optional `.a3s2` dual-read.

## Operator note

Native read means Sync/Check against classic HTTP(S) repos can run **without Java** when metadata is reachable locally or over HTTP(S). Publish/Build still needs the bridge until the write gap closes.
