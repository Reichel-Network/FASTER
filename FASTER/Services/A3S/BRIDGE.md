# a3s-metadata-bridge (FASTER wiring)

Canonical contract: [Arma3Sync `docs/BRIDGE.md`](https://github.com/Reichel-Network/Arma3Sync/blob/cursor/a3s-headless-metadata-bridge-686c/docs/BRIDGE.md) (companion PR).

Classic remote `.a3s/*` files are **GZIP’d Java `ObjectOutputStream`** graphs. FASTER never asks operators to configure a product JAR; this helper is an internal adapter behind `IA3SMetadataStore` / `BridgeA3SMetadataStore` until a C# ObjectStream serializer lands.

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
| Write one file after build | `write --target <contentRoot> --kind <kind> --in <jsonFile>` |
| Mirror remote blobs locally | `copy --from <url> --to <contentRoot> --kind all` |

`--source` / `--from` accept remote autoconfig URLs (`https://host/path/.a3s/autoconfig`), content roots, `.a3s/` dirs, or a single metadata file path.

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Usage / unknown command |
| `2` | I/O, HTTP, missing file, deserialize failure |
| `3` | JSON / domain mapping error |

### Path resolution in FASTER

1. User setting `a3sMetadataBridgePath` when non-empty (Settings → Advanced) — path to the fat JAR (or a directory containing `a3s-metadata-bridge-*-all.jar`).
2. Else bundled default next to the FASTER executable: `a3s-bridge/a3s-metadata-bridge-*-all.jar`.
3. `java` from `JAVA_HOME` or `PATH`.

Missing JAR or `java` → `A3SBridgeNotFoundException` / `A3SBridgeException` (no Setup wizard).

## JSON contract (`bridgeVersion: 1`)

`kind=all` response (stdout when `--out -`):

```json
{
  "bridgeVersion": 1,
  "kind": "all",
  "files": {
    "autoconfig": { },
    "sync": { "type": "directory", "name": "racine", "children": [ ] },
    "serverinfo": { "revision": 1 },
    "changelogs": { },
    "events": null
  }
}
```

Sync nodes use `{ "type": "directory"|"leaf", "name", "children"?, "sha1"?, "size"?, "compressed"?, … }`. Parent links are omitted; FASTER rebuilds relative paths while walking.

## FASTER ownership (not the bridge)

- HTTP(S) download of addon trees: `A3SProtocolClient`
- Sync/Check/cancel/progress UX: `A3SSyncService` + Repositories UI
- Addon folders → `armaMods`: `A3SModImport`

## Smoke / CI note

Public HTTP(S) metadata smoke (`https://arma.game.reichel.network/.a3s/autoconfig`) works with the companion bridge (rev 1, 451 files). Cloud/Linux CI for FASTER generally cannot run the WPF UI or bundle JDK 25 + the JAR; unit tests use bridge-shaped JSON fixtures via `JsonA3SMetadataStore`. Full Sync against Tobias’s repo is a Windows + JDK 25 + bridge JAR manual test (`E:\ArmaSync\Mods\reichel_arma`).

## License

GNU GPLv3 — same as Arma3Sync. Preserve notices when bundling beside FASTER.
