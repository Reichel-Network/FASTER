using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace FASTER.Services.A3S.Serialization
{
    /// <summary>
    /// Maps classic Arma3Sync domain ObjectStream graphs onto FASTER <see cref="A3SMetadataModels"/> DTOs.
    /// </summary>
    internal static class A3SObjectStreamMapper
    {
        public const string KindAutoconfig = "autoconfig";
        public const string KindSync = "sync";
        public const string KindServerInfo = "serverinfo";
        public const string KindChangelogs = "changelogs";
        public const string KindEvents = "events";

        public static object? ReadGzipObjectStream(byte[] gzipBytes)
        {
            using var ms = new MemoryStream(gzipBytes, writable: false);
            using var gzip = new GZipStream(ms, CompressionMode.Decompress);
            using var ois = new JavaObjectInputStream(gzip);
            return ois.ReadObject();
        }

        public static A3SServerInfo ToServerInfo(object? root)
        {
            var obj = RequireClass(root, "fr.soe.a3s.domain.repository.ServerInfo");
            var info = new A3SServerInfo
            {
                Revision = GetInt(obj, "revision"),
                NumberOfFiles = GetLong(obj, "numberOfFiles"),
                TotalFilesSize = GetLong(obj, "totalFilesSize"),
                NumberOfConnections = GetInt(obj, "numberOfConnections"),
                NoPartialFileTransfer = GetBool(obj, "noPartialFileTransfer"),
                RepositoryContentUpdated = GetBool(obj, "repositoryContentUpdated"),
                CompressedPboFilesOnly = GetBool(obj, "compressedPboFilesOnly"),
                BuildDate = GetDate(obj, "buildDate"),
                HiddenFolderPaths = new List<string>()
            };

            if (obj.Fields.TryGetValue("hiddenFolderPaths", out var hidden) && hidden is List<object?> list)
            {
                foreach (var item in list)
                {
                    if (item is string s && !string.IsNullOrEmpty(s))
                        info.HiddenFolderPaths.Add(s);
                }
            }

            return info;
        }

        public static A3SAutoconfigInfo ToAutoconfig(object? root)
        {
            var obj = RequireClass(root, "fr.soe.a3s.domain.repository.AutoConfig");
            var info = new A3SAutoconfigInfo
            {
                RepositoryName = GetString(obj, "repositoryName") ?? string.Empty,
                Protocol = new A3SProtocolInfo(),
                FavoriteServers = new List<A3SFavoriteServer>()
            };

            if (obj.Fields.TryGetValue("protocole", out var protocolObj) && protocolObj is JavaObject protocol)
                info.Protocol = ToProtocol(protocol);

            if (obj.Fields.TryGetValue("favoriteServers", out var favObj) && favObj is List<object?> favorites)
            {
                foreach (var item in favorites)
                {
                    if (item is not JavaObject fav)
                        continue;
                    info.FavoriteServers.Add(new A3SFavoriteServer
                    {
                        Name = GetString(fav, "name") ?? string.Empty,
                        IpAddress = GetString(fav, "ipAddress") ?? string.Empty,
                        Port = GetInt(fav, "port"),
                        Password = GetString(fav, "password") ?? string.Empty,
                        Selected = GetBool(fav, "selected"),
                        ModsetName = GetString(fav, "modsetName") ?? string.Empty,
                        RepositoryName = GetString(fav, "repositoryName") ?? string.Empty
                    });
                }
            }

            return info;
        }

        public static A3SSyncTreeNode ToSyncTree(object? root)
        {
            return ToSyncNode(root, parent: null)
                   ?? new A3SSyncTreeNode { Type = "directory", Name = "racine" };
        }

        public static A3SChangelogs ToChangelogs(object? root)
        {
            var obj = RequireClass(root, "fr.soe.a3s.domain.repository.Changelogs");
            var result = new A3SChangelogs();
            if (obj.Fields.TryGetValue("list", out var listObj) && listObj is List<object?> list)
            {
                foreach (var item in list)
                {
                    if (item is not JavaObject entry)
                        continue;
                    result.List.Add(new A3SChangelogEntry
                    {
                        Revision = GetInt(entry, "revision"),
                        BuildDate = GetDate(entry, "buildDate"),
                        ContentUpdated = GetBool(entry, "contentUpdated"),
                        NewAddons = ToStringList(entry, "newAddons"),
                        UpdatedAddons = ToStringList(entry, "updatedAddons"),
                        DeletedAddons = ToStringList(entry, "deletedAddons"),
                        Addons = ToStringList(entry, "addons")
                    });
                }
            }

            return result;
        }

        public static A3SEvents ToEvents(object? root)
        {
            var obj = RequireClass(root, "fr.soe.a3s.domain.repository.Events");
            var result = new A3SEvents();
            if (obj.Fields.TryGetValue("list", out var listObj) && listObj is List<object?> list)
            {
                foreach (var item in list)
                {
                    if (item is not JavaObject entry)
                        continue;
                    result.List.Add(new A3SEventInfo
                    {
                        Name = GetString(entry, "name") ?? string.Empty,
                        Description = GetString(entry, "description") ?? string.Empty,
                        AddonNames = ToBoolMap(entry, "addonNames"),
                        DlcNames = ToBoolMap(entry, "dlcNames"),
                        UserconfigFolderNames = ToBoolMap(entry, "userconfigFolderNames")
                    });
                }
            }

            return result;
        }

        private static A3SSyncTreeNode? ToSyncNode(object? node, A3SSyncTreeNode? parent)
        {
            if (node is not JavaObject obj)
                return null;

            if (obj.ClassName.EndsWith(".SyncTreeLeaf", StringComparison.Ordinal))
            {
                return new A3SSyncTreeNode
                {
                    Type = "leaf",
                    Name = GetString(obj, "name") ?? string.Empty,
                    Sha1 = GetString(obj, "sha1"),
                    Size = GetLong(obj, "size"),
                    Compressed = GetBool(obj, "compressed"),
                    CompressedSize = GetLong(obj, "compressedSize"),
                    Updated = GetBool(obj, "updated"),
                    Deleted = GetBool(obj, "deleted")
                };
            }

            if (!obj.ClassName.EndsWith(".SyncTreeDirectory", StringComparison.Ordinal))
                return null;

            var dir = new A3SSyncTreeNode
            {
                Type = "directory",
                Name = GetString(obj, "name") ?? string.Empty,
                MarkAsAddon = GetBool(obj, "markAsAddon"),
                Hidden = GetBool(obj, "hidden"),
                Updated = GetBool(obj, "updated"),
                Deleted = GetBool(obj, "deleted"),
                Children = new List<A3SSyncTreeNode>()
            };

            if (obj.Fields.TryGetValue("list", out var listObj) && listObj is List<object?> children)
            {
                foreach (var child in children)
                {
                    var mapped = ToSyncNode(child, dir);
                    if (mapped != null)
                        dir.Children.Add(mapped);
                }
            }

            return dir;
        }

        private static A3SProtocolInfo ToProtocol(JavaObject protocol)
        {
            var type = "HTTP";
            if (protocol.Fields.TryGetValue("protocolType", out var pt) && pt is JavaEnum enm)
                type = enm.Name;
            else if (protocol.ClassName.EndsWith(".Http", StringComparison.Ordinal))
                type = "HTTP";
            else if (protocol.ClassName.EndsWith(".Https", StringComparison.Ordinal))
                type = "HTTPS";
            else if (protocol.ClassName.EndsWith(".Ftp", StringComparison.Ordinal))
                type = "FTP";
            else if (protocol.ClassName.EndsWith(".Sftp", StringComparison.Ordinal))
                type = "SFTP";
            else if (protocol.ClassName.EndsWith(".Webdav", StringComparison.Ordinal))
                type = "HTTP_WEBDAV";

            // Prefer enum name when present (HTTPS vs HTTP class).
            if (protocol.Fields.TryGetValue("protocolType", out var pt2) && pt2 is JavaEnum e2 && !string.IsNullOrEmpty(e2.Name))
                type = e2.Name;

            return new A3SProtocolInfo
            {
                Type = type,
                Url = GetString(protocol, "url") ?? string.Empty,
                Port = GetString(protocol, "port") ?? string.Empty,
                Login = GetString(protocol, "login") ?? "anonymous",
                Password = GetString(protocol, "password") ?? string.Empty,
                ConnectionTimeOut = GetString(protocol, "connectionTimeOut"),
                ReadTimeOut = GetString(protocol, "readTimeOut"),
                ValidateSslCertificate = GetBool(protocol, "validateSSLCertificate")
            };
        }

        private static JavaObject RequireClass(object? root, string expectedClass)
        {
            if (root is not JavaObject obj)
                throw new InvalidDataException($"Expected Java object {expectedClass}, got {root?.GetType().Name ?? "null"}.");
            if (!string.Equals(obj.ClassName, expectedClass, StringComparison.Ordinal))
                throw new InvalidDataException($"Expected {expectedClass}, got {obj.ClassName}.");
            return obj;
        }

        private static string? GetString(JavaObject obj, string name)
            => obj.Fields.TryGetValue(name, out var v) ? v as string : null;

        private static bool GetBool(JavaObject obj, string name)
            => obj.Fields.TryGetValue(name, out var v) && v is bool b && b;

        private static int GetInt(JavaObject obj, string name)
        {
            if (!obj.Fields.TryGetValue(name, out var v) || v is null)
                return 0;
            return v switch
            {
                int i => i,
                long l => checked((int)l),
                short s => s,
                byte b => b,
                _ => 0
            };
        }

        private static long GetLong(JavaObject obj, string name)
        {
            if (!obj.Fields.TryGetValue(name, out var v) || v is null)
                return 0;
            return v switch
            {
                long l => l,
                int i => i,
                short s => s,
                byte b => b,
                _ => 0
            };
        }

        private static DateTime? GetDate(JavaObject obj, string name)
        {
            if (!obj.Fields.TryGetValue(name, out var v) || v is null)
                return null;
            if (v is DateTime dt)
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            return null;
        }

        private static List<string> ToStringList(JavaObject obj, string name)
        {
            var result = new List<string>();
            if (!obj.Fields.TryGetValue(name, out var v) || v is not List<object?> list)
                return result;
            foreach (var item in list)
            {
                if (item is string s)
                    result.Add(s);
            }

            return result;
        }

        private static Dictionary<string, bool> ToBoolMap(JavaObject obj, string name)
        {
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (!obj.Fields.TryGetValue(name, out var v) || v is not Dictionary<object, object?> map)
                return result;
            foreach (var pair in map)
            {
                if (pair.Key is string key)
                    result[key] = ToBoolValue(pair.Value);
            }

            return result;
        }

        /// <summary>
        /// Unwrap HashMap bool values. After handle rewrite, values are usually unboxed
        /// <see cref="bool"/>; still accept leftover <c>java.lang.Boolean</c> objects.
        /// </summary>
        private static bool ToBoolValue(object? value)
        {
            if (value is bool b)
                return b;
            if (value is JavaObject jo
                && jo.ClassName.Equals("java.lang.Boolean", StringComparison.Ordinal)
                && jo.Fields.TryGetValue("value", out var field)
                && field is bool fb)
                return fb;
            return false;
        }
    }
}
