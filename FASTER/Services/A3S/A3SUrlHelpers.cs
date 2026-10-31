using System;

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
            if (type is not ("HTTP" or "HTTPS" or "HTTP/WEBDAV" or "HTTPS/WEBDAV"))
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

        private static bool IsDefaultPort(string scheme, string port)
        {
            if (!int.TryParse(port, out var p))
                return true;
            return (scheme == "https" && p == 443) || (scheme == "http" && p == 80);
        }
    }
}
