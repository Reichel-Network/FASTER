# a3s-metadata-bridge (optional Advanced fallback)

Canonical contract: [Arma3Sync `docs/BRIDGE.md`](https://github.com/Reichel-Network/Arma3Sync/blob/master/docs/BRIDGE.md).

Classic remote `.a3s/*` files are **GZIP’d Java `ObjectOutputStream`** graphs.

**Default product path:** `SerializerA3SMetadataStore` (C#) reads and writes those blobs. **No JRE.** See `PHASE4.md`.

This Java helper is **optional**. Enable Settings → Advanced → **Enable Java bridge fallback** only if you need emergency/legacy metadata I/O. FASTER does **not** use sealed `*.a3s.repository` files (settings remain source of truth).

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

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Usage / unknown command |
| `2` | I/O, HTTP, missing file, deserialize failure |
| `3` | JSON / domain mapping error |

### Path resolution when fallback is enabled

1. User setting `a3sMetadataBridgePath` when non-empty — path to the fat JAR (or directory containing `a3s-metadata-bridge-*-all.jar`).
2. Else `a3s-bridge/a3s-metadata-bridge-*-all.jar` next to the FASTER executable (if you choose to ship it).
3. `java` from `JAVA_HOME` or `PATH`.

## License

GNU GPLv3 — same as Arma3Sync. Preserve notices if bundling beside FASTER.
