using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace FASTER.Services.A3S
{
    /// <summary>Envelope returned by <c>a3s-metadata-bridge</c> (<c>bridgeVersion: 1</c>).</summary>
    public sealed class A3SBridgeEnvelope
    {
        [JsonPropertyName("bridgeVersion")]
        public int BridgeVersion { get; set; }

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("files")]
        public A3SBridgeFiles? Files { get; set; }

        [JsonPropertyName("autoconfig")]
        public A3SAutoconfigInfo? Autoconfig { get; set; }

        [JsonPropertyName("sync")]
        public A3SSyncTreeNode? Sync { get; set; }

        [JsonPropertyName("serverinfo")]
        public A3SServerInfo? ServerInfo { get; set; }
    }

    public sealed class A3SBridgeFiles
    {
        [JsonPropertyName("autoconfig")]
        public A3SAutoconfigInfo? Autoconfig { get; set; }

        [JsonPropertyName("sync")]
        public A3SSyncTreeNode? Sync { get; set; }

        [JsonPropertyName("serverinfo")]
        public A3SServerInfo? ServerInfo { get; set; }
    }

    /// <summary>Normalized bundle used by FASTER sync/check after a bridge read.</summary>
    public sealed class A3SMetadataBundle
    {
        public A3SAutoconfigInfo Autoconfig { get; init; } = new();
        public A3SServerInfo ServerInfo { get; init; } = new();
        public A3SSyncTreeNode SyncRoot { get; init; } = new();
    }

    /// <summary>Parsed classic <c>.a3s/autoconfig</c> (bridge JSON contract).</summary>
    public sealed class A3SAutoconfigInfo
    {
        [JsonPropertyName("repositoryName")]
        public string RepositoryName { get; set; } = string.Empty;

        [JsonPropertyName("protocol")]
        public A3SProtocolInfo Protocol { get; set; } = new();
    }

    public sealed class A3SProtocolInfo
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "HTTP";

        /// <summary>Host + optional remote path (Arma3Sync style), without scheme.</summary>
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("port")]
        public string Port { get; set; } = string.Empty;

        [JsonPropertyName("login")]
        public string Login { get; set; } = "anonymous";

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        [JsonPropertyName("connectionTimeOut")]
        public string? ConnectionTimeOut { get; set; }

        [JsonPropertyName("readTimeOut")]
        public string? ReadTimeOut { get; set; }

        [JsonPropertyName("validateSSLCertificate")]
        public bool ValidateSslCertificate { get; set; } = true;
    }

    /// <summary>Parsed classic <c>.a3s/serverinfo</c>.</summary>
    public sealed class A3SServerInfo
    {
        [JsonPropertyName("revision")]
        public long Revision { get; set; }

        [JsonPropertyName("buildDate")]
        public DateTime? BuildDate { get; set; }

        [JsonPropertyName("numberOfFiles")]
        public long NumberOfFiles { get; set; }

        [JsonPropertyName("totalFilesSize")]
        public long TotalFilesSize { get; set; }

        [JsonPropertyName("numberOfConnections")]
        public int NumberOfConnections { get; set; } = 1;

        [JsonPropertyName("noPartialFileTransfer")]
        public bool NoPartialFileTransfer { get; set; }

        [JsonPropertyName("repositoryContentUpdated")]
        public bool RepositoryContentUpdated { get; set; }

        [JsonPropertyName("compressedPboFilesOnly")]
        public bool CompressedPboFilesOnly { get; set; }

        [JsonPropertyName("hiddenFolderPaths")]
        public List<string> HiddenFolderPaths { get; set; } = new();
    }

    /// <summary>
    /// Bridge sync-tree node (<c>type: directory|leaf</c>). Parent links are omitted in JSON;
    /// relative paths are computed when walking.
    /// </summary>
    public sealed class A3SSyncTreeNode
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "directory";

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("markAsAddon")]
        public bool MarkAsAddon { get; set; }

        [JsonPropertyName("hidden")]
        public bool Hidden { get; set; }

        [JsonPropertyName("updated")]
        public bool Updated { get; set; }

        [JsonPropertyName("deleted")]
        public bool Deleted { get; set; }

        [JsonPropertyName("sha1")]
        public string? Sha1 { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("compressed")]
        public bool Compressed { get; set; }

        [JsonPropertyName("compressedSize")]
        public long CompressedSize { get; set; }

        [JsonPropertyName("children")]
        public List<A3SSyncTreeNode> Children { get; set; } = new();

        [JsonIgnore]
        public bool IsLeaf => string.Equals(Type, "leaf", StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool IsDirectory => !IsLeaf;

        public IEnumerable<A3SSyncTreeLeafRef> EnumerateLeaves(string parentRelativePath = "")
        {
            if (IsLeaf)
            {
                var path = string.IsNullOrEmpty(parentRelativePath) ? Name : $"{parentRelativePath}/{Name}";
                yield return new A3SSyncTreeLeafRef(Name, path, Sha1 ?? string.Empty, Size, Compressed, CompressedSize);
                yield break;
            }

            var selfIsRoot = string.Equals(Name, "racine", StringComparison.OrdinalIgnoreCase)
                             || string.IsNullOrEmpty(Name);
            var currentPath = selfIsRoot
                ? parentRelativePath
                : (string.IsNullOrEmpty(parentRelativePath) ? Name : $"{parentRelativePath}/{Name}");

            foreach (var child in Children)
            {
                foreach (var leaf in child.EnumerateLeaves(currentPath))
                    yield return leaf;
            }
        }

        public IReadOnlyList<string> AddonFolderRelativePaths()
        {
            var result = new List<string>();
            CollectAddonFolders(this, parentRelativePath: "", result);
            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Replace('/', System.IO.Path.DirectorySeparatorChar))
                .ToList();
        }

        private static void CollectAddonFolders(A3SSyncTreeNode node, string parentRelativePath, List<string> result)
        {
            if (node.IsLeaf)
                return;

            var selfIsRoot = string.Equals(node.Name, "racine", StringComparison.OrdinalIgnoreCase)
                             || string.IsNullOrEmpty(node.Name);
            var currentPath = selfIsRoot
                ? parentRelativePath
                : (string.IsNullOrEmpty(parentRelativePath) ? node.Name : $"{parentRelativePath}/{node.Name}");

            if (node.MarkAsAddon && !string.IsNullOrEmpty(currentPath))
                result.Add(currentPath);

            foreach (var child in node.Children)
                CollectAddonFolders(child, currentPath, result);
        }
    }

    public sealed record A3SSyncTreeLeafRef(
        string Name,
        string RelativePath,
        string Sha1,
        long Size,
        bool Compressed,
        long CompressedSize)
    {
        public string ParentRelativePath
        {
            get
            {
                var idx = RelativePath.LastIndexOf('/');
                return idx <= 0 ? string.Empty : RelativePath[..idx];
            }
        }
    }

    public sealed class A3SSyncProgress
    {
        public string Message { get; init; } = string.Empty;
        public int CompletedFiles { get; init; }
        public int TotalFiles { get; init; }
        public long CompletedBytes { get; init; }
        public long TotalBytes { get; init; }
        public double Percent => TotalFiles <= 0 ? 0 : CompletedFiles * 100.0 / TotalFiles;
    }

    public sealed class A3SCheckResult
    {
        public long Revision { get; init; }
        public int TotalRemoteFiles { get; init; }
        public int MissingOrOutdated { get; init; }
        public int ExtraLocalFiles { get; init; }
        public long DownloadBytes { get; init; }
        public IReadOnlyList<A3SSyncTreeLeafRef> FilesToDownload { get; init; } = Array.Empty<A3SSyncTreeLeafRef>();
        public IReadOnlyList<string> ExtraLocalPaths { get; init; } = Array.Empty<string>();
    }

    public sealed class A3SSyncResult
    {
        public long Revision { get; init; }
        public int Downloaded { get; init; }
        public int Unchanged { get; init; }
        public int Deleted { get; init; }
        public int Failed { get; init; }
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> AddonRelativePaths { get; init; } = Array.Empty<string>();
    }

    public sealed class A3SSyncOptions
    {
        /// <summary>When true, delete local files not present in remote sync (exact-match).</summary>
        public bool ExactMatch { get; init; }

        /// <summary>
        /// Caller must set true after UI confirm when <see cref="ExactMatch"/> is requested.
        /// Sync refuses exact-match deletes unless this is true.
        /// </summary>
        public bool ExactMatchConfirmed { get; init; }
    }
}
