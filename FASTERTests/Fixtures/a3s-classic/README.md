# Classic `.a3s` ObjectStream fixtures (Phase 4)

GZIP’d Java `ObjectOutputStream` blobs captured from `https://arma.game.reichel.network/.a3s/`
(same set as Arma3Sync `src/test/resources/a3s-fixtures`).

Used by `A3SPhase4SerializerTests` to prove `SerializerA3SMetadataStore` reads:

| File | Domain |
| --- | --- |
| `autoconfig` | `fr.soe.a3s.domain.repository.AutoConfig` |
| `sync` | `SyncTreeDirectory` tree (451 leaves) |
| `serverinfo` | `ServerInfo` (revision 1) |
| `changelogs` | `Changelogs` |
| `events` | `Events` (empty list) |

These are **not** encrypted `*.a3s.repository` files.
