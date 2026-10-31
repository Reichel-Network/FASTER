using System.Threading;
using System.Threading.Tasks;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Sole boundary for classic <c>.a3s/*</c> metadata I/O (autoconfig, sync, serverinfo, changelogs, events).
    /// Phase 1: <see cref="BridgeA3SMetadataStore"/> shells
    /// <c>java -jar a3s-metadata-bridge-*-all.jar</c> (JSON contract in BRIDGE.md).
    /// Later: C# ObjectStream-compatible serializer behind the same interface. No operator-facing JAR UX.
    /// </summary>
    public interface IA3SMetadataStore
    {
        /// <summary>
        /// Read all available classic metadata from an autoconfig URL, content root, <c>.a3s</c> dir, or file.
        /// Preferred Phase 1 entry (bridge: <c>read --source … --kind all --out -</c>).
        /// </summary>
        Task<A3SMetadataBundle> ReadAllAsync(string source, CancellationToken cancellationToken = default);

        /// <summary>Read autoconfig only (<c>--kind autoconfig</c>).</summary>
        Task<A3SAutoconfigInfo> ReadAutoconfigAsync(string source, CancellationToken cancellationToken = default);

        /// <summary>Read sync tree only (<c>--kind sync</c>).</summary>
        Task<A3SSyncTreeNode> ReadSyncTreeAsync(string source, CancellationToken cancellationToken = default);

        /// <summary>Read serverinfo only (<c>--kind serverinfo</c>).</summary>
        Task<A3SServerInfo> ReadServerInfoAsync(string source, CancellationToken cancellationToken = default);

        /// <summary>Write classic <c>.a3s/*</c> metadata (Phase 2 publish).</summary>
        Task WriteMetadataAsync(string contentRoot, object metadata, CancellationToken cancellationToken = default);
    }
}
