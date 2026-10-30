using System.Threading;
using System.Threading.Tasks;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Sole boundary for classic <c>.a3s/*</c> metadata I/O (autoconfig, sync, serverinfo, changelogs, events).
    /// Phase 0: stubs. Phase 1: <see cref="BridgeA3SMetadataStore"/> (hidden headless helper).
    /// Later: C# ObjectStream-compatible serializer behind the same interface. No operator-facing JAR UX.
    /// </summary>
    public interface IA3SMetadataStore
    {
        /// <summary>Read remote or local autoconfig metadata for the given content root or URL.</summary>
        Task<object?> ReadAutoconfigAsync(string source, CancellationToken cancellationToken = default);

        /// <summary>Read the sync tree graph for a repository content root.</summary>
        Task<object?> ReadSyncTreeAsync(string contentRoot, CancellationToken cancellationToken = default);

        /// <summary>Read serverinfo metadata.</summary>
        Task<object?> ReadServerInfoAsync(string contentRoot, CancellationToken cancellationToken = default);

        /// <summary>Write classic <c>.a3s/*</c> metadata files under <paramref name="contentRoot"/>.</summary>
        Task WriteMetadataAsync(string contentRoot, object metadata, CancellationToken cancellationToken = default);
    }
}
