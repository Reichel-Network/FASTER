using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace FASTER.Services.A3S.Serialization
{
    /// <summary>
    /// Builds classic GZIP Java ObjectStream blobs from FASTER A3S DTOs
    /// (compatible with Arma3Sync <c>A3SFilesAccessor</c> / our reader).
    /// </summary>
    internal static class A3SObjectStreamWriter
    {
        private const byte ScWriteMethod = 0x01;
        private const byte ScSerializable = 0x02;
        private const byte ScEnum = 0x10;

        // Cached class descriptors (shared by name within a single write graph via OOS handle table).
        private static readonly JavaClassDesc ArrayListDesc = CreateArrayListDesc();
        private static readonly JavaClassDesc HashSetDesc = CreateHashSetDesc();
        private static readonly JavaClassDesc HashMapDesc = CreateHashMapDesc();
        private static readonly JavaClassDesc DateDesc = CreateDateDesc();
        private static readonly JavaClassDesc EnumDesc = CreateEnumBaseDesc();
        private static readonly JavaClassDesc ProtocolTypeDesc = CreateProtocolTypeDesc();
        private static readonly JavaClassDesc AbstractProtocolDesc = CreateAbstractProtocolDesc();
        private static readonly JavaClassDesc HttpDesc = CreateHttpDesc();
        private static readonly JavaClassDesc FtpDesc = CreateProtocolSubclass(
            "fr.soe.a3s.domain.Ftp", -1917448655850030044L);
        private static readonly JavaClassDesc SftpDesc = CreateProtocolSubclass(
            "fr.soe.a3s.domain.Sftp", 1L);
        private static readonly JavaClassDesc WebdavDesc = CreateProtocolSubclass(
            "fr.soe.a3s.domain.Webdav", -30L);
        private static readonly JavaClassDesc SocksDesc = CreateProtocolSubclass(
            "fr.soe.a3s.domain.Socks", -40L);
        private static readonly JavaClassDesc FavoriteServerDesc = CreateFavoriteServerDesc();
        private static readonly JavaClassDesc AutoConfigDesc = CreateAutoConfigDesc();
        private static readonly JavaClassDesc ServerInfoDesc = CreateServerInfoDesc();
        private static readonly JavaClassDesc SyncDirDesc = CreateSyncDirectoryDesc();
        private static readonly JavaClassDesc SyncLeafDesc = CreateSyncLeafDesc();
        private static readonly JavaClassDesc ChangelogsDesc = CreateChangelogsDesc();
        private static readonly JavaClassDesc ChangelogDesc = CreateChangelogDesc();
        private static readonly JavaClassDesc EventsDesc = CreateEventsDesc();
        private static readonly JavaClassDesc EventDesc = CreateEventDesc();

        public static byte[] WriteGzipObjectStream(object rootGraph)
        {
            using var ms = new MemoryStream();
            using (var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            using (var oos = new JavaObjectOutputStream(gzip))
            {
                oos.WriteObject(rootGraph);
            }

            return ms.ToArray();
        }

        public static byte[] WriteServerInfo(A3SServerInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);
            var obj = new JavaObject(ServerInfoDesc)
            {
                Fields =
                {
                    ["compressedPboFilesOnly"] = info.CompressedPboFilesOnly,
                    ["noPartialFileTransfer"] = info.NoPartialFileTransfer,
                    ["numberOfConnections"] = info.NumberOfConnections,
                    ["numberOfFiles"] = info.NumberOfFiles,
                    ["repositoryContentUpdated"] = info.RepositoryContentUpdated,
                    ["revision"] = checked((int)info.Revision),
                    ["totalFilesSize"] = info.TotalFilesSize,
                    ["buildDate"] = info.BuildDate.HasValue ? CreateDate(info.BuildDate.Value) : null,
                    ["hiddenFolderPaths"] = CreateHashSet(info.HiddenFolderPaths ?? new List<string>())
                }
            };
            return WriteGzipObjectStream(obj);
        }

        public static byte[] WriteAutoconfig(A3SAutoconfigInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);
            var favorites = CreateArrayList(
                (info.FavoriteServers ?? new List<A3SFavoriteServer>())
                .Select(CreateFavoriteServer)
                .Cast<object?>()
                .ToList());

            var obj = new JavaObject(AutoConfigDesc)
            {
                Fields =
                {
                    ["favoriteServers"] = favorites,
                    ["protocole"] = CreateProtocol(info.Protocol ?? new A3SProtocolInfo()),
                    ["repositoryName"] = info.RepositoryName ?? string.Empty
                }
            };
            return WriteGzipObjectStream(obj);
        }

        public static byte[] WriteSyncTree(A3SSyncTreeNode root)
        {
            ArgumentNullException.ThrowIfNull(root);
            return WriteGzipObjectStream(BuildSyncNode(root, parent: null));
        }

        public static byte[] WriteChangelogs(A3SChangelogs changelogs)
        {
            ArgumentNullException.ThrowIfNull(changelogs);
            var entries = CreateArrayList(
                (changelogs.List ?? new List<A3SChangelogEntry>())
                .Select(CreateChangelog)
                .Cast<object?>()
                .ToList());
            var obj = new JavaObject(ChangelogsDesc)
            {
                Fields = { ["list"] = entries }
            };
            return WriteGzipObjectStream(obj);
        }

        public static byte[] WriteEvents(A3SEvents events)
        {
            ArgumentNullException.ThrowIfNull(events);
            var entries = CreateArrayList(
                (events.List ?? new List<A3SEventInfo>())
                .Select(CreateEvent)
                .Cast<object?>()
                .ToList());
            var obj = new JavaObject(EventsDesc)
            {
                Fields = { ["list"] = entries }
            };
            return WriteGzipObjectStream(obj);
        }

        public static byte[] WriteKind(string kind, object payload)
        {
            var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                A3SObjectStreamMapper.KindAutoconfig => WriteAutoconfig((A3SAutoconfigInfo)payload),
                A3SObjectStreamMapper.KindSync => WriteSyncTree((A3SSyncTreeNode)payload),
                A3SObjectStreamMapper.KindServerInfo => WriteServerInfo((A3SServerInfo)payload),
                A3SObjectStreamMapper.KindChangelogs => WriteChangelogs((A3SChangelogs)payload),
                A3SObjectStreamMapper.KindEvents => WriteEvents((A3SEvents)payload),
                _ => throw new ArgumentException($"Unsupported metadata kind '{kind}'.", nameof(kind))
            };
        }

        private static JavaObject BuildSyncNode(A3SSyncTreeNode node, JavaObject? parent)
        {
            if (node.IsLeaf)
            {
                var leaf = new JavaObject(SyncLeafDesc)
                {
                    Fields =
                    {
                        ["compressed"] = node.Compressed,
                        ["compressedSize"] = node.CompressedSize,
                        ["deleted"] = node.Deleted,
                        ["size"] = node.Size,
                        ["updated"] = node.Updated,
                        ["name"] = node.Name ?? string.Empty,
                        ["parent"] = parent,
                        ["sha1"] = node.Sha1
                    }
                };
                return leaf;
            }

            var dir = new JavaObject(SyncDirDesc)
            {
                Fields =
                {
                    ["deleted"] = node.Deleted,
                    ["hidden"] = node.Hidden,
                    ["markAsAddon"] = node.MarkAsAddon,
                    ["updated"] = node.Updated,
                    ["name"] = string.IsNullOrEmpty(node.Name) ? "racine" : node.Name,
                    ["parent"] = parent
                }
            };

            var children = new List<object?>();
            foreach (var child in node.Children ?? new List<A3SSyncTreeNode>())
                children.Add(BuildSyncNode(child, dir));
            dir.Fields["list"] = CreateArrayList(children);
            return dir;
        }

        private static JavaObject CreateFavoriteServer(A3SFavoriteServer fav)
        {
            return new JavaObject(FavoriteServerDesc)
            {
                Fields =
                {
                    ["port"] = fav.Port,
                    ["selected"] = fav.Selected,
                    ["ipAddress"] = fav.IpAddress ?? string.Empty,
                    ["modsetName"] = fav.ModsetName ?? string.Empty,
                    ["name"] = fav.Name ?? string.Empty,
                    ["password"] = fav.Password ?? string.Empty,
                    ["repositoryName"] = fav.RepositoryName ?? string.Empty
                }
            };
        }

        private static JavaObject CreateChangelog(A3SChangelogEntry entry)
        {
            return new JavaObject(ChangelogDesc)
            {
                Fields =
                {
                    ["contentUpdated"] = entry.ContentUpdated,
                    ["revision"] = entry.Revision,
                    ["addons"] = CreateArrayList((entry.Addons ?? new List<string>()).Cast<object?>().ToList()),
                    ["buildDate"] = entry.BuildDate.HasValue ? CreateDate(entry.BuildDate.Value) : null,
                    ["deletedAddons"] = CreateArrayList((entry.DeletedAddons ?? new List<string>()).Cast<object?>().ToList()),
                    ["newAddons"] = CreateArrayList((entry.NewAddons ?? new List<string>()).Cast<object?>().ToList()),
                    ["updatedAddons"] = CreateArrayList((entry.UpdatedAddons ?? new List<string>()).Cast<object?>().ToList())
                }
            };
        }

        private static JavaObject CreateEvent(A3SEventInfo ev)
        {
            return new JavaObject(EventDesc)
            {
                Fields =
                {
                    ["addonNames"] = CreateHashMap(ev.AddonNames),
                    ["description"] = ev.Description ?? string.Empty,
                    ["dlcNames"] = CreateHashMap(ev.DlcNames),
                    ["name"] = ev.Name ?? string.Empty,
                    ["userconfigFolderNames"] = CreateHashMap(ev.UserconfigFolderNames)
                }
            };
        }

        private static JavaObject CreateProtocol(A3SProtocolInfo protocol)
        {
            var typeName = (protocol.Type ?? "HTTP").Trim().ToUpperInvariant();
            var desc = typeName switch
            {
                "FTP" or "FTPS" => FtpDesc,
                "SFTP" => SftpDesc,
                "HTTP/WEBDAV" or "HTTP_WEBDAV" => WebdavDesc,
                "HTTPS/WEBDAV" or "HTTPS_WEBDAV" => WebdavDesc,
                "SOCKS4" or "SOCKS5" => SocksDesc,
                _ => HttpDesc
            };

            // Enum constant names match ProtocolType enum identifiers.
            var enumName = typeName switch
            {
                "HTTP/WEBDAV" or "HTTP_WEBDAV" => "HTTP_WEBDAV",
                "HTTPS/WEBDAV" or "HTTPS_WEBDAV" => "HTTPS_WEBDAV",
                "FTPS" => "FTPS",
                "FTP" => "FTP",
                "SFTP" => "SFTP",
                "HTTPS" => "HTTPS",
                "SOCKS4" => "SOCKS4",
                "SOCKS5" => "SOCKS5",
                _ => "HTTP"
            };

            var obj = new JavaObject(desc)
            {
                Fields =
                {
                    ["validateSSLCertificate"] = protocol.ValidateSslCertificate,
                    ["connectionTimeOut"] = protocol.ConnectionTimeOut,
                    ["encryptionMode"] = null,
                    ["login"] = protocol.Login ?? "anonymous",
                    ["password"] = protocol.Password ?? string.Empty,
                    ["port"] = protocol.Port ?? string.Empty,
                    ["protocolType"] = new JavaEnum(ProtocolTypeDesc, enumName),
                    ["readTimeOut"] = protocol.ReadTimeOut,
                    ["url"] = protocol.Url ?? string.Empty
                }
            };
            return obj;
        }

        private static JavaObject CreateDate(DateTime utc)
        {
            var dto = utc.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(utc, DateTimeKind.Utc)
                : utc.ToUniversalTime();
            var millis = new DateTimeOffset(dto).ToUnixTimeMilliseconds();
            return new JavaObject(DateDesc)
            {
                WriteCustom = oos => oos.WriteBlockInt64(millis)
            };
        }

        private static JavaObject CreateArrayList(List<object?> elements)
        {
            var size = elements.Count;
            return new JavaObject(ArrayListDesc)
            {
                Fields = { ["size"] = size },
                WriteCustom = oos =>
                {
                    // JDK ArrayList.writeObject: writeInt(size) then elements
                    oos.WriteBlockInt32(size);
                    foreach (var el in elements)
                        oos.WriteObject(el);
                }
            };
        }

        private static JavaObject CreateHashSet(IEnumerable<string> values)
        {
            var list = values?.Where(v => v != null).Distinct(StringComparer.Ordinal).Cast<object?>().ToList()
                       ?? new List<object?>();
            var size = list.Count;
            var capacity = Math.Max(16, size);
            return new JavaObject(HashSetDesc)
            {
                WriteCustom = oos =>
                {
                    oos.WriteBlockHashSetHeader(capacity, 0.75f, size);
                    foreach (var el in list)
                        oos.WriteObject(el);
                }
            };
        }

        private static JavaObject CreateHashMap(Dictionary<string, bool>? map)
        {
            var pairs = map ?? new Dictionary<string, bool>();
            var size = pairs.Count;
            var buckets = Math.Max(16, size);
            return new JavaObject(HashMapDesc)
            {
                Fields =
                {
                    ["loadFactor"] = 0.75f,
                    ["threshold"] = (int)(buckets * 0.75f)
                },
                WriteCustom = oos =>
                {
                    oos.WriteBlockHashMapHeader(buckets, size);
                    foreach (var kv in pairs)
                    {
                        oos.WriteObject(kv.Key);
                        // Boolean as java.lang.Boolean would be an object; Arma3Sync uses Boolean in HashMap.
                        // Our reader normalizes HashMap values; for Boolean boxed we write a simple approach:
                        // Actually Java HashMap<String,Boolean> stores Boolean objects. For empty maps it's fine.
                        // For non-empty, write java.lang.Boolean — keep it simple with a lightweight Boolean object.
                        oos.WriteObject(CreateBoolean(kv.Value));
                    }
                }
            };
        }

        private static readonly JavaClassDesc BooleanDesc = CreateBooleanDesc();

        private static JavaObject CreateBoolean(bool value)
        {
            return new JavaObject(BooleanDesc)
            {
                Fields = { ["value"] = value }
            };
        }

        private static JavaClassDesc CreateBooleanDesc()
        {
            var d = new JavaClassDesc("java.lang.Boolean", -3665804199970212011L, ScSerializable);
            d.Fields = new[] { new JavaFieldDesc("value", 'Z', null) };
            return d;
        }

        #region Class descriptors

        private static JavaClassDesc CreateArrayListDesc()
        {
            var d = new JavaClassDesc("java.util.ArrayList", 8683452581122892189L, (byte)(ScSerializable | ScWriteMethod));
            d.Fields = new[] { new JavaFieldDesc("size", 'I', null) };
            return d;
        }

        private static JavaClassDesc CreateHashSetDesc()
        {
            return new JavaClassDesc("java.util.HashSet", -5024744406713321676L, (byte)(ScSerializable | ScWriteMethod));
        }

        private static JavaClassDesc CreateHashMapDesc()
        {
            var d = new JavaClassDesc("java.util.HashMap", 362498820763181265L, (byte)(ScSerializable | ScWriteMethod));
            d.Fields = new[]
            {
                new JavaFieldDesc("loadFactor", 'F', null),
                new JavaFieldDesc("threshold", 'I', null)
            };
            return d;
        }

        private static JavaClassDesc CreateDateDesc()
        {
            return new JavaClassDesc("java.util.Date", 7523967970034938905L, (byte)(ScSerializable | ScWriteMethod));
        }

        private static JavaClassDesc CreateEnumBaseDesc()
        {
            return new JavaClassDesc("java.lang.Enum", 0L, (byte)(ScSerializable | ScEnum));
        }

        private static JavaClassDesc CreateProtocolTypeDesc()
        {
            var d = new JavaClassDesc("fr.soe.a3s.constant.ProtocolType", 0L, (byte)(ScSerializable | ScEnum));
            d.SuperClass = EnumDesc;
            return d;
        }

        private static JavaClassDesc CreateAbstractProtocolDesc()
        {
            var d = new JavaClassDesc("fr.soe.a3s.domain.AbstractProtocole", -886357032287815203L, ScSerializable);
            d.Fields = new[]
            {
                new JavaFieldDesc("validateSSLCertificate", 'Z', null),
                new JavaFieldDesc("connectionTimeOut", 'L', "Ljava/lang/String;"),
                new JavaFieldDesc("encryptionMode", 'L', "Lfr/soe/a3s/constant/EncryptionMode;"),
                new JavaFieldDesc("login", 'L', "Ljava/lang/String;"),
                new JavaFieldDesc("password", 'L', "Ljava/lang/String;"),
                new JavaFieldDesc("port", 'L', "Ljava/lang/String;"),
                new JavaFieldDesc("protocolType", 'L', "Lfr/soe/a3s/constant/ProtocolType;"),
                new JavaFieldDesc("readTimeOut", 'L', "Ljava/lang/String;"),
                new JavaFieldDesc("url", 'L', "Ljava/lang/String;")
            };
            return d;
        }

        private static JavaClassDesc CreateHttpDesc()
            => CreateProtocolSubclass("fr.soe.a3s.domain.Http", -20L);

        private static JavaClassDesc CreateProtocolSubclass(string name, long uid)
        {
            var d = new JavaClassDesc(name, uid, ScSerializable);
            d.SuperClass = AbstractProtocolDesc;
            return d;
        }

        private static JavaClassDesc CreateFavoriteServerDesc()
        {
            var d = new JavaClassDesc("fr.soe.a3s.domain.configration.FavoriteServer", 4613236292596702132L, ScSerializable);
            d.Fields = new[]
            {
                new JavaFieldDesc("port", 'I', null),
                new JavaFieldDesc("selected", 'Z', null),
                new JavaFieldDesc("ipAddress", 'L', "Ljava/lang/String;"),
                new JavaFieldDesc("modsetName", 'L', "Ljava/lang/String;"),
                new JavaFieldDesc("name", 'L', "Ljava/lang/String;"),
                new JavaFieldDesc("password", 'L', "Ljava/lang/String;"),
                new JavaFieldDesc("repositoryName", 'L', "Ljava/lang/String;")
            };
            return d;
        }

        private static JavaClassDesc CreateAutoConfigDesc()
        {
            var d = new JavaClassDesc("fr.soe.a3s.domain.repository.AutoConfig", 2706280725619197755L, ScSerializable);
            d.Fields = new[]
            {
                new JavaFieldDesc("favoriteServers", 'L', "Ljava/util/List;"),
                new JavaFieldDesc("protocole", 'L', "Lfr/soe/a3s/domain/AbstractProtocole;"),
                new JavaFieldDesc("repositoryName", 'L', "Ljava/lang/String;")
            };
            return d;
        }

        private static JavaClassDesc CreateServerInfoDesc()
        {
            var d = new JavaClassDesc("fr.soe.a3s.domain.repository.ServerInfo", 7697232677958952953L, ScSerializable);
            d.Fields = new[]
            {
                new JavaFieldDesc("compressedPboFilesOnly", 'Z', null),
                new JavaFieldDesc("noPartialFileTransfer", 'Z', null),
                new JavaFieldDesc("numberOfConnections", 'I', null),
                new JavaFieldDesc("numberOfFiles", 'J', null),
                new JavaFieldDesc("repositoryContentUpdated", 'Z', null),
                new JavaFieldDesc("revision", 'I', null),
                new JavaFieldDesc("totalFilesSize", 'J', null),
                new JavaFieldDesc("buildDate", 'L', "Ljava/util/Date;"),
                new JavaFieldDesc("hiddenFolderPaths", 'L', "Ljava/util/Set;")
            };
            return d;
        }

        private static JavaClassDesc CreateSyncDirectoryDesc()
        {
            var d = new JavaClassDesc("fr.soe.a3s.domain.repository.SyncTreeDirectory", -2855304993780573704L, ScSerializable);
            d.Fields = new[]
            {
                new JavaFieldDesc("deleted", 'Z', null),
                new JavaFieldDesc("hidden", 'Z', null),
                new JavaFieldDesc("markAsAddon", 'Z', null),
                new JavaFieldDesc("updated", 'Z', null),
                new JavaFieldDesc("list", 'L', "Ljava/util/List;"),
                new JavaFieldDesc("name", 'L', "Ljava/lang/String;"),
                new JavaFieldDesc("parent", 'L', "Lfr/soe/a3s/domain/repository/SyncTreeDirectory;")
            };
            return d;
        }

        private static JavaClassDesc CreateSyncLeafDesc()
        {
            var d = new JavaClassDesc("fr.soe.a3s.domain.repository.SyncTreeLeaf", 8849248143660225239L, ScSerializable);
            d.Fields = new[]
            {
                new JavaFieldDesc("compressed", 'Z', null),
                new JavaFieldDesc("compressedSize", 'J', null),
                new JavaFieldDesc("deleted", 'Z', null),
                new JavaFieldDesc("size", 'J', null),
                new JavaFieldDesc("updated", 'Z', null),
                new JavaFieldDesc("name", 'L', "Ljava/lang/String;"),
                new JavaFieldDesc("parent", 'L', "Lfr/soe/a3s/domain/repository/SyncTreeDirectory;"),
                new JavaFieldDesc("sha1", 'L', "Ljava/lang/String;")
            };
            return d;
        }

        private static JavaClassDesc CreateChangelogsDesc()
        {
            var d = new JavaClassDesc("fr.soe.a3s.domain.repository.Changelogs", 7332983755625818660L, ScSerializable);
            d.Fields = new[] { new JavaFieldDesc("list", 'L', "Ljava/util/List;") };
            return d;
        }

        private static JavaClassDesc CreateChangelogDesc()
        {
            var d = new JavaClassDesc("fr.soe.a3s.domain.repository.Changelog", 749128678979864247L, ScSerializable);
            d.Fields = new[]
            {
                new JavaFieldDesc("contentUpdated", 'Z', null),
                new JavaFieldDesc("revision", 'I', null),
                new JavaFieldDesc("addons", 'L', "Ljava/util/List;"),
                new JavaFieldDesc("buildDate", 'L', "Ljava/util/Date;"),
                new JavaFieldDesc("deletedAddons", 'L', "Ljava/util/List;"),
                new JavaFieldDesc("newAddons", 'L', "Ljava/util/List;"),
                new JavaFieldDesc("updatedAddons", 'L', "Ljava/util/List;")
            };
            return d;
        }

        private static JavaClassDesc CreateEventsDesc()
        {
            var d = new JavaClassDesc("fr.soe.a3s.domain.repository.Events", -5141643688299352462L, ScSerializable);
            d.Fields = new[] { new JavaFieldDesc("list", 'L', "Ljava/util/List;") };
            return d;
        }

        private static JavaClassDesc CreateEventDesc()
        {
            var d = new JavaClassDesc("fr.soe.a3s.domain.repository.Event", 7456226002765813117L, ScSerializable);
            d.Fields = new[]
            {
                new JavaFieldDesc("addonNames", 'L', "Ljava/util/Map;"),
                new JavaFieldDesc("description", 'L', "Ljava/lang/String;"),
                new JavaFieldDesc("dlcNames", 'L', "Ljava/util/Map;"),
                new JavaFieldDesc("name", 'L', "Ljava/lang/String;"),
                new JavaFieldDesc("userconfigFolderNames", 'L', "Ljava/util/Map;")
            };
            return d;
        }

        #endregion
    }
}
