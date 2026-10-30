using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using FASTER.Models;
using FASTER.Properties;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// After sync: scan addon folders under the repository local path and upsert into
    /// <c>Settings.Default.armaMods</c>; optionally sync into Deployments.
    /// </summary>
    public class A3SModImport
    {
        private static readonly Regex MetaNameRegex = new(
            @"^\s*name\s*=\s*""([^""]+)""\s*;",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

        private readonly Settings _settings;

        public A3SModImport()
            : this(Settings.Default)
        {
        }

        internal A3SModImport(Settings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Import synced addon folders as local Arma mods.
        /// Prefers <paramref name="addonRelativePaths"/> from sync <c>markAsAddon</c>;
        /// otherwise scans <c>@*</c> and folders that contain an <c>addons</c> directory.
        /// </summary>
        public Task<A3SModImportResult> ImportFromRepositoryAsync(
            A3SRepository repository,
            IEnumerable<string>? addonRelativePaths = null,
            bool syncDeploymentList = true,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(repository);
            if (string.IsNullOrWhiteSpace(repository.LocalPath) || !Directory.Exists(repository.LocalPath))
            {
                return Task.FromResult(new A3SModImportResult(0, 0, 0, Array.Empty<string>()));
            }

            EnsureArmaMods();
            var folders = ResolveAddonFolders(repository.LocalPath, addonRelativePaths).ToList();
            var added = 0;
            var updated = 0;
            var skipped = 0;
            var names = new List<string>();

            foreach (var folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(folder))
                {
                    skipped++;
                    continue;
                }

                var folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var displayName = TryReadMetaName(folder) ?? StripAtPrefix(folderName);
                var workshopId = ResolveWorkshopId(folderName, folder);

                var existing = _settings.armaMods.ArmaMods.FirstOrDefault(m =>
                    m.WorkshopId == workshopId
                    || PathsEqual(m.Path, folder)
                    || string.Equals(m.Name, displayName, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    existing.Name = displayName;
                    existing.Path = folder;
                    existing.IsLocal = true;
                    existing.Status = ArmaModStatus.Local;
                    existing.Author = string.IsNullOrWhiteSpace(existing.Author) ? "A3S" : existing.Author;
                    if (existing.WorkshopId == 0)
                        existing.WorkshopId = workshopId;
                    updated++;
                    names.Add(displayName);
                }
                else
                {
                    var mod = new ArmaMod
                    {
                        WorkshopId = workshopId,
                        Name = displayName,
                        Path = folder,
                        Author = "A3S",
                        IsLocal = true,
                        Status = ArmaModStatus.Local
                    };
                    _settings.armaMods.ArmaMods.Add(mod);
                    added++;
                    names.Add(displayName);
                }
            }

            if (syncDeploymentList)
                SyncDeploymentList();

            _settings.armaMods = _settings.armaMods;
            _settings.Save();

            return Task.FromResult(new A3SModImportResult(added, updated, skipped, names));
        }

        private void SyncDeploymentList()
        {
            if (_settings.Deployments == null)
                _settings.Deployments = new ArmaDeployment();

            var deployment = _settings.Deployments;
            foreach (var mod in _settings.armaMods.ArmaMods)
            {
                if (deployment.DeployMods.Any(m => m.WorkshopId == mod.WorkshopId))
                    continue;
                deployment.DeployMods.Add(new DeploymentMod(mod));
            }

            foreach (var deployMod in deployment.DeployMods.ToArray())
            {
                if (_settings.armaMods.ArmaMods.All(m => m.WorkshopId != deployMod.WorkshopId))
                    deployment.DeployMods.Remove(deployMod);
                else
                    deployMod.UpdateInfos();
            }

            _settings.Deployments = deployment;
        }

        private void EnsureArmaMods()
        {
            if (_settings.armaMods == null)
                _settings.armaMods = new ArmaModCollection();
        }

        private static IEnumerable<string> ResolveAddonFolders(string localRoot, IEnumerable<string>? addonRelativePaths)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (addonRelativePaths != null)
            {
                foreach (var relative in addonRelativePaths)
                {
                    if (string.IsNullOrWhiteSpace(relative))
                        continue;
                    var full = Path.GetFullPath(Path.Combine(localRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (Directory.Exists(full))
                        found.Add(full);
                }
            }

            if (found.Count == 0)
            {
                foreach (var dir in Directory.EnumerateDirectories(localRoot))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith('@')
                        || Directory.Exists(Path.Combine(dir, "addons"))
                        || Directory.Exists(Path.Combine(dir, "Addons")))
                    {
                        found.Add(Path.GetFullPath(dir));
                    }
                }
            }

            return found.OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        }

        private uint ResolveWorkshopId(string folderName, string folderPath)
        {
            var bare = StripAtPrefix(folderName);
            if (uint.TryParse(bare, out var workshopId) && workshopId != 0)
            {
                return EnsureUnique(workshopId, folderPath);
            }

            // Stable-ish local id from path hash in the high synthetic range used by AddLocalMod.
            var hash = (uint)(Math.Abs(folderPath.ToUpperInvariant().GetHashCode()) % (ushort.MaxValue / 2));
            var candidate = uint.MaxValue - (hash == 0 ? 1u : hash);
            return EnsureUnique(candidate, folderPath);
        }

        private uint EnsureUnique(uint workshopId, string folderPath)
        {
            var mods = _settings.armaMods.ArmaMods;
            var clash = mods.FirstOrDefault(m => m.WorkshopId == workshopId && !PathsEqual(m.Path, folderPath));
            if (clash == null)
                return workshopId;

            var existingIds = new HashSet<uint>(mods.Select(m => m.WorkshopId));
            var id = workshopId;
            var rng = new Random(HashCode.Combine(folderPath, workshopId));
            while (id == 0 || existingIds.Contains(id))
                id = (uint)(uint.MaxValue - rng.Next(ushort.MaxValue / 2));
            return id;
        }

        private static string? TryReadMetaName(string folder)
        {
            var meta = Path.Combine(folder, "meta.cpp");
            if (!File.Exists(meta))
                return null;
            try
            {
                var text = File.ReadAllText(meta);
                var match = MetaNameRegex.Match(text);
                return match.Success ? match.Groups[1].Value.Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static string StripAtPrefix(string name)
            => name.StartsWith('@') ? name[1..] : name;

        private static bool PathsEqual(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return false;
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed record A3SModImportResult(int Added, int Updated, int Skipped, IReadOnlyList<string> ModNames);
}
