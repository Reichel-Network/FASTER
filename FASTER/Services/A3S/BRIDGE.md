# a3s-metadata-bridge (FASTER wiring)

Canonical contract: [Arma3Sync `docs/BRIDGE.md`](https://github.com/Reichel-Network/Arma3Sync/blob/master/docs/BRIDGE.md)
(Phase 2 write shapes / `write --kind all`: companion [PR #2](https://github.com/Reichel-Network/Arma3Sync/pull/2)).

Classic remote `.a3s/*` files are **GZIP’d Java `ObjectOutputStream`** graphs. FASTER never asks operators to configure a product JAR; this helper is an internal adapter behind `IA3SMetadataStore`.

**Phase 4:** `SerializerA3SMetadataStore` reads those blobs natively in C#. Product default (`a3sPreferNativeSerializer`) prefers the serializer for **reads**; this bridge remains the **write** path and read fallback until native write lands. See `PHASE4.md`.

## Build (Arma3Sync companion)

```bash
gradle metadataBridgeJar
# → build/libs/a3s-metadata-bridge-<version>-all.jar
```

Requires JDK 25 (same toolchain as Arma3Sync).

## Invoke (FASTER `BridgeA3SMetadataStore`)

```text
java -jar <a3s-metadata-bridge-*-all.jar> <command> [options]
```

| Need | Command |
| --- | --- |
| Import / refresh repo metadata | `read --source <autoconfigUrlOrContentRoot> --kind all --out -` |
| Read one file | `read --source … --kind autoconfig\|sync\|serverinfo\|changelogs\|events --out -` |
| Phase 3 events / favorites | `read --kind events`; write `autoconfig` with `favoriteServers` via `write --kind autoconfig` |
| Write one file | `write --target <contentRoot> --kind <kind> --in <json\|->` |
| **Phase 2 Build (preferred)** | `write --target <contentRoot> --kind all --in <files-envelope\|->` |
| Mirror remote blobs locally | `copy --from <url> --to <contentRoot> --kind all` |

`--source` / `--from` accept remote autoconfig URLs (`https://host/path/.a3s/autoconfig`), content roots, `.a3s/` dirs, or a single metadata file path.

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Usage / unknown command |
| `2` | I/O, HTTP, missing file, deserialize failure |
| `3` | JSON / domain mapping error (bad payload, unknown protocol, invalid dates) |

### Path resolution in FASTER

1. User setting `a3sMetadataBridgePath` when non-empty (Settings → Advanced) — path to the fat JAR (or a directory containing `a3s-metadata-bridge-*-all.jar`).
2. Else bundled default next to the FASTER executable: `a3s-bridge/a3s-metadata-bridge-*-all.jar`.
3. `java` from `JAVA_HOME` or `PATH`.

Missing JAR or `java` → `A3SBridgeNotFoundException` / `A3SBridgeException` (no Setup wizard).

## JSON contract (`bridgeVersion: 1`)

### Read / single-kind write

```json
{
  "bridgeVersion": 1,
  "kind": "serverinfo",
  "serverinfo": { "revision": 12, "buildDate": "2026-10-29T18:44:48.000Z" }
}
```

### Phase 2 multi-write (`write --kind all`) — used by `A3SBuildService`

```json
{
  "bridgeVersion": 1,
  "kind": "all",
  "files": {
    "autoconfig": { "repositoryName": "…", "protocol": { "type": "HTTPS", "url": "host", "port": "443", "login": "anonymous", "password": "" } },
    "sync": { "type": "directory", "name": "racine", "children": [ ] },
    "serverinfo": { "revision": 2, "numberOfFiles": 10, "totalFilesSize": 123, "repositoryContentUpdated": true },
    "changelogs": { "list": [ { "revision": 2, "contentUpdated": true, "newAddons": [], "updatedAddons": [], "deletedAddons": [], "addons": [] } ] },
    "events": null
  }
}
```

Null / omitted entries under `files` are skipped on write; at least one non-null file is required. Single-kind write also accepts a payload nested under `files.<kind>`.

Sync nodes use `{ "type": "directory"|"leaf", "name", "children"?, "sha1"?, "size"?, "compressed"?, … }`. Parent links are omitted; FASTER rebuilds relative paths while walking. Addon folders are marked when they contain an `addons` child (Arma3Sync rule).

Build walk skips metadata sidecars under the publish root: classic `.a3s/` and the FASTER fixture store `.a3s-json/` (used by `JsonA3SMetadataStore` in tests).

## FASTER ownership (not the bridge)

| Concern | Owner |
| --- | --- |
| Tree walk + SHA-1 + revision/changelogs | `A3SBuildService` |
| Metadata I/O | `IA3SMetadataStore` → bridge `write --kind all` |
| HTTP(S) PUT / SFTP upload of changed files + `.a3s/*` | `A3SProtocolClient` + `A3SPublishService` |
| Sync/Check download UX | `A3SSyncService` + Repositories UI |
| Events ↔ profiles, favorite servers, Sync→Deploy→Launch | `A3SEventService` + Repositories / Profile UI |
| Repo defs (publish root, upload protocol/creds) | FASTER settings / `A3SRepository` |

## Publish smoke limits

- Public HTTP(S) **read** of `https://arma.game.reichel.network/.a3s/autoconfig` works with the companion bridge.
- Cloud/Linux CI cannot run WPF UI, JDK 25 + bridge JAR write against Tobias’s hosts, or live SFTP/HTTP PUT to private endpoints.
- Unit tests cover walk/SHA-1, `WriteAll` envelope via `JsonA3SMetadataStore`, HTTP PUT upload stubs, and publish orchestration with a stub handler.
- Full Build → Publish against a real host needs Windows + JDK 25 + bridge JAR (PR #2+) + upload credentials (`Repositories` → Configure).

## License

GNU GPLv3 — same as Arma3Sync. Preserve notices when bundling beside FASTER.
