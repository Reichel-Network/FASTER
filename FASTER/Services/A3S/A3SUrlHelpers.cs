using System;

using FASTER.Models;

namespace FASTER.Services.A3S
{
    /// <summary>Autoconfig URL / content-root helpers matching Arma3Sync <c>AutoConfigURLAccessMethods</c>.</summary>
    public static class A3SUrlHelpers
    {
        public const string AutoconfigSuffix = "/.a3s/autoconfig";
        public const string A3SFolderName = ".a3s";

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

            return new Uri(contentRoot, $"{A3SFolderName}/{metadataFileName.TrimStart('/')}");
        }

        public static Uri GetAddonFileUri(Uri contentRoot, string relativePathWithSlashes)
        {
            ArgumentNullException.ThrowIfNull(contentRoot);
            var relative = (relativePathWithSlashes ?? string.Empty).Replace('\\', '/').TrimStart('/');
            return new Uri(contentRoot, relative);
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
                return new A3SUploadEndpoint
                {
                    Protocol = "SFTP",
                    HostAndPath = repository.UploadUrl.Trim(),
                    Port = string.IsNullOrWhiteSpace(repository.UploadPort) ? "22" : repository.UploadPort.Trim(),
                    Login = string.IsNullOrWhiteSpace(repository.UploadLogin) ? "anonymous" : repository.UploadLogin.Trim(),
                    Password = repository.UploadPassword ?? string.Empty,
                    ContentRoot = uri
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
