using System;
using System.IO;

using FASTER.Models;

namespace FASTER.Services.A3S
{
    /// <summary>Autoconfig URL / content-root helpers matching Arma3Sync <c>AutoConfigURLAccessMethods</c>.</summary>
    public static class A3SUrlHelpers
    {
        public const string AutoconfigSuffix = "/.a3s/autoconfig";
        public const string A3SFolderName = ".a3s";

        /// <summary>
        /// FASTER test / fixture JSON sidecar written by <c>JsonA3SMetadataStore</c>.
        /// Not classic wire content — build walk must skip it the same way as <see cref="A3SFolderName"/>.
        /// </summary>
        public const string A3SJsonSidecarFolderName = ".a3s-json";

        /// <summary>
        /// Normalize a sync / content-root / autoconfig URL into classic <c>.../.a3s/autoconfig</c>.
        /// Accepts bare content roots, <c>/.a3s/</c>, <c>/.a3s/sync</c>, or an already-valid autoconfig URL.
        /// </summary>
        public static string NormalizeAutoconfigUrl(string urlOrSyncUrl)
        {
            if (string.IsNullOrWhiteSpace(urlOrSyncUrl))
                throw new ArgumentException("URL is required.", nameof(urlOrSyncUrl));

            var trimmed = urlOrSyncUrl.Trim().TrimEnd('/');
            if (trimmed.EndsWith("/.a3s/autoconfig", StringComparison.OrdinalIgnoreCase))
                return trimmed;

            if (trimmed.EndsWith("/.a3s/sync", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith("/.a3s/events", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith("/.a3s/serverinfo", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith("/.a3s/changelogs", StringComparison.OrdinalIgnoreCase))
            {
                var cut = trimmed.LastIndexOf("/.a3s/", StringComparison.OrdinalIgnoreCase);
                return trimmed[..cut] + AutoconfigSuffix;
            }

            if (trimmed.EndsWith("/.a3s", StringComparison.OrdinalIgnoreCase))
                return trimmed + "/autoconfig";

            return trimmed + AutoconfigSuffix;
        }

        /// <summary>Directory names under a publish root that are metadata stores, not addon content.</summary>
        public static bool IsMetadataSidecarDirectory(string? directoryName)
        {
            if (string.IsNullOrEmpty(directoryName))
                return false;
            return string.Equals(directoryName, A3SFolderName, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(directoryName, A3SJsonSidecarFolderName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when a path relative to the publish root is under a metadata sidecar folder.</summary>
        public static bool IsUnderMetadataSidecar(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return false;

            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar)
                .Trim(Path.DirectorySeparatorChar);
            if (IsMetadataSidecarDirectory(normalized))
                return true;

            var firstSep = normalized.IndexOf(Path.DirectorySeparatorChar);
            var top = firstSep < 0 ? normalized : normalized[..firstSep];
            return IsMetadataSidecarDirectory(top);
        }

        /// <summary>
        /// Content root URI for addon downloads: strip trailing <c>/.a3s/autoconfig</c> from the autoconfig URL.
        /// </summary>
        public static Uri GetContentRootUri(string autoconfigUrl)
        {
            if (string.IsNullOrWhiteSpace(autoconfigUrl))
                throw new ArgumentException("Autoconfig URL is required.", nameof(autoconfigUrl));

            var trimmed = autoconfigUrl.Trim().TrimEnd('/');
            var idx = trimmed.LastIndexOf("/.a3s/autoconfig", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                throw new ArgumentException(
                    "Autoconfig URL must end with /.a3s/autoconfig (Arma3Sync shape).",
                    nameof(autoconfigUrl));

            var root = trimmed[..idx].TrimEnd('/');
            if (!Uri.TryCreate(root + "/", UriKind.Absolute, out var uri))
                throw new ArgumentException($"Invalid content root derived from '{autoconfigUrl}'.", nameof(autoconfigUrl));

            return uri;
        }

        public static Uri GetMetadataUri(Uri contentRoot, string metadataFileName)
        {
            ArgumentNullException.ThrowIfNull(contentRoot);
            if (string.IsNullOrWhiteSpace(metadataFileName))
                throw new ArgumentException("Metadata file name is required.", nameof(metadataFileName));

            // contentRoot MUST end with '/' or the last path segment is replaced
            // (e.g. …/repository + .a3s/sync → …/.a3s/sync).
            var root = EnsureTrailingSlash(contentRoot);
            return new Uri(root, $"{A3SFolderName}/{metadataFileName.TrimStart('/')}");
        }

        public static Uri GetAddonFileUri(Uri contentRoot, string relativePathWithSlashes)
        {
            ArgumentNullException.ThrowIfNull(contentRoot);
            var relative = (relativePathWithSlashes ?? string.Empty).Replace('\\', '/').TrimStart('/');
            var root = EnsureTrailingSlash(contentRoot);
            return new Uri(root, relative);
        }

        /// <summary>
        /// Map a remote upload URI to the SFTP path string passed to SSH.NET.
        /// Host-only endpoints use home-relative paths (<c>.a3s/sync</c>);
        /// host+/path endpoints keep absolute paths (<c>/repository/.a3s/sync</c>).
        /// </summary>
        public static string GetSftpRemotePath(Uri remoteUri, A3SUploadEndpoint? endpoint)
        {
            ArgumentNullException.ThrowIfNull(remoteUri);
            var absolute = Uri.UnescapeDataString(remoteUri.AbsolutePath);
            if (string.IsNullOrEmpty(absolute) || absolute == "/")
                throw new InvalidOperationException($"SFTP remote path missing for {remoteUri}.");

            if (endpoint?.SftpRelativeToHome == true)
            {
                var relative = absolute.TrimStart('/');
                if (string.IsNullOrEmpty(relative))
                    throw new InvalidOperationException($"SFTP relative remote path missing for {remoteUri}.");
                return relative;
            }

            return absolute;
        }

        public static bool IsSftpHostOnlyContentRoot(Uri contentRoot)
        {
            ArgumentNullException.ThrowIfNull(contentRoot);
            var path = contentRoot.AbsolutePath;
            return string.IsNullOrEmpty(path) || path == "/";
        }

        private static Uri EnsureTrailingSlash(Uri contentRoot)
        {
            var s = contentRoot.ToString();
            if (s.EndsWith('/'))
                return contentRoot;
            if (!Uri.TryCreate(s + "/", UriKind.Absolute, out var withSlash))
                throw new ArgumentException($"Invalid content root URI '{contentRoot}'.", nameof(contentRoot));
            return withSlash;
        }

        /// <summary>Build a content-root URI from bridge-parsed protocol info (HTTP/HTTPS Phase 1).</summary>
        public static Uri? TryBuildContentRootFromProtocol(A3SProtocolInfo protocol)
        {
            if (protocol == null || string.IsNullOrWhiteSpace(protocol.Url))
                return null;

            var type = (protocol.Type ?? "HTTP").Trim().ToUpperInvariant();
            if (type is not ("HTTP" or "HTTPS" or "HTTP/WEBDAV" or "HTTPS/WEBDAV" or "HTTP_WEBDAV" or "HTTPS_WEBDAV"))
                return null;

            var scheme = type.StartsWith("HTTPS", StringComparison.Ordinal) ? "https" : "http";
            var raw = protocol.Url.Trim().TrimStart('/');
            var slash = raw.IndexOf('/');
            var hostPort = slash >= 0 ? raw[..slash] : raw;
            var path = slash >= 0 ? raw[slash..] : string.Empty;

            if (!string.IsNullOrWhiteSpace(protocol.Port)
                && !hostPort.Contains(':')
                && !IsDefaultPort(scheme, protocol.Port))
            {
                hostPort = $"{hostPort}:{protocol.Port.Trim()}";
            }

            if (!Uri.TryCreate($"{scheme}://{hostPort}{path}/", UriKind.Absolute, out var uri))
                return null;

            return uri;
        }

        /// <summary>
        /// Resolve C# upload endpoint from repository publish settings.
        /// HTTP(S) falls back to the autoconfig content root when UploadUrl is empty.
        /// </summary>
        public static A3SUploadEndpoint ResolveUploadEndpoint(A3SRepository repository)
        {
            ArgumentNullException.ThrowIfNull(repository);

            var protocol = (repository.UploadProtocol ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(protocol))
                protocol = "HTTPS";

            if (protocol is "HTTP" or "HTTPS" or "HTTP/WEBDAV" or "HTTPS/WEBDAV" or "HTTP_WEBDAV" or "HTTPS_WEBDAV")
            {
                if (!string.IsNullOrWhiteSpace(repository.UploadUrl))
                {
                    var scheme = protocol.StartsWith("HTTPS", StringComparison.Ordinal) ? "https" : "http";
                    var normalizedProtocol = scheme == "https" ? "HTTPS" : "HTTP";
                    var uri = BuildUriFromHostPath(scheme, repository.UploadUrl, repository.UploadPort,
                        defaultHttpsPort: "443", defaultHttpPort: "80");
                    return new A3SUploadEndpoint
                    {
                        Protocol = normalizedProtocol,
                        HostAndPath = repository.UploadUrl.Trim(),
                        Port = repository.UploadPort ?? string.Empty,
                        Login = repository.UploadLogin ?? string.Empty,
                        Password = repository.UploadPassword ?? string.Empty,
                        ContentRoot = uri
                    };
                }

                if (string.IsNullOrWhiteSpace(repository.AutoconfigUrl))
                {
                    throw new InvalidOperationException(
                        "HTTP(S) publish requires UploadUrl or an AutoconfigUrl to derive the content root.");
                }

                var contentRoot = GetContentRootUri(repository.AutoconfigUrl);
                return new A3SUploadEndpoint
                {
                    Protocol = contentRoot.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "HTTPS" : "HTTP",
                    HostAndPath = contentRoot.Host.TrimEnd('/') + contentRoot.AbsolutePath.TrimEnd('/'),
                    Port = contentRoot.IsDefaultPort ? string.Empty : contentRoot.Port.ToString(),
                    Login = repository.UploadLogin ?? string.Empty,
                    Password = repository.UploadPassword ?? string.Empty,
                    ContentRoot = contentRoot
                };
            }

            if (protocol == "SFTP")
            {
                if (string.IsNullOrWhiteSpace(repository.UploadUrl))
                    throw new InvalidOperationException("SFTP publish requires UploadUrl (host[/path]).");

                var uri = BuildUriFromHostPath("sftp", repository.UploadUrl, repository.UploadPort,
                    defaultHttpsPort: "22", defaultHttpPort: "22", defaultPortOverride: "22");
                // Host-only UploadUrl → content root AbsolutePath is "/". Absolute SFTP
                // writes then hit "/.a3s/…" at the SSH root (Permission denied). The repo
                // lives in the login home / working directory — use relative paths.
                var relativeToHome = IsSftpHostOnlyContentRoot(uri);
                return new A3SUploadEndpoint
                {
                    Protocol = "SFTP",
                    HostAndPath = repository.UploadUrl.Trim(),
                    Port = string.IsNullOrWhiteSpace(repository.UploadPort) ? "22" : repository.UploadPort.Trim(),
                    Login = string.IsNullOrWhiteSpace(repository.UploadLogin) ? "anonymous" : repository.UploadLogin.Trim(),
                    Password = repository.UploadPassword ?? string.Empty,
                    ContentRoot = uri,
                    SftpRelativeToHome = relativeToHome
                };
            }

            throw new NotSupportedException(
                $"Upload protocol '{protocol}' is not supported yet. Phase 2 implements HTTP(S) and SFTP.");
        }

        private static Uri BuildUriFromHostPath(
            string scheme,
            string hostAndPath,
            string? port,
            string defaultHttpsPort,
            string defaultHttpPort,
            string? defaultPortOverride = null)
        {
            var raw = hostAndPath.Trim().TrimStart('/');
            // Allow accidental scheme prefix.
            if (raw.Contains("://", StringComparison.Ordinal))
            {
                if (Uri.TryCreate(raw.EndsWith('/') ? raw : raw + "/", UriKind.Absolute, out var absolute))
                    return absolute;
            }

            var slash = raw.IndexOf('/');
            var hostPort = slash >= 0 ? raw[..slash] : raw;
            var path = slash >= 0 ? raw[slash..] : string.Empty;

            var defaultPort = defaultPortOverride
                              ?? (scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? defaultHttpsPort : defaultHttpPort);

            if (!string.IsNullOrWhiteSpace(port)
                && !hostPort.Contains(':')
                && !string.Equals(port.Trim(), defaultPort, StringComparison.Ordinal))
            {
                hostPort = $"{hostPort}:{port.Trim()}";
            }
            else if (string.IsNullOrWhiteSpace(port)
                     && defaultPortOverride != null
                     && !hostPort.Contains(':')
                     && scheme.Equals("sftp", StringComparison.OrdinalIgnoreCase))
            {
                // leave default 22 implicit in Uri
            }

            if (!Uri.TryCreate($"{scheme}://{hostPort}{path}/", UriKind.Absolute, out var uri))
                throw new ArgumentException($"Invalid upload URL '{hostAndPath}'.");

            return uri;
        }

        private static bool IsDefaultPort(string scheme, string port)
        {
            if (!int.TryParse(port, out var p))
                return true;
            return (scheme == "https" && p == 443)
                   || (scheme == "http" && p == 80)
                   || (scheme == "sftp" && p == 22);
        }
    }
}
