using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FASTER.Models;
using FASTER.Properties;
using FASTER.Services.A3S.Serialization;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Phase 3+: list/create/edit A3S events (modsets), map addon sets onto Server Profile mod lists,
    /// optional favorite-server write into autoconfig, and Sync → Deploy → Launch orchestration.
    /// </summary>
    public class A3SEventService
    {
        private readonly IA3SMetadataStore _metadataStore;
        private readonly A3SRepositoryService _repositoryService;
        private readonly A3SSyncService _syncService;
        private readonly A3SModImport _modImport;
        private readonly Settings _settings;

        public A3SEventService()
            : this(
                A3SMetadataStoreFactory.CreateDefault(),
                new A3SRepositoryService(),
                null,
                new A3SModImport(),
                Settings.Default)
        {
        }

        public A3SEventService(
            IA3SMetadataStore metadataStore,
            A3SRepositoryService repositoryService,
            A3SSyncService? syncService = null,
            A3SModImport? modImport = null)
            : this(metadataStore, repositoryService, syncService, modImport, Settings.Default)
        {
        }

        internal A3SEventService(
            IA3SMetadataStore metadataStore,
            A3SRepositoryService repositoryService,
            A3SSyncService? syncService,
            A3SModImport? modImport,
            Settings settings)
        {
            _metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
            _repositoryService = repositoryService ?? throw new ArgumentNullException(nameof(repositoryService));
            _syncService = syncService ?? new A3SSyncService(_metadataStore);
            _modImport = modImport ?? new A3SModImport();
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>Resolve metadata source for a repository (autoconfig URL preferred, else local/publish root).</summary>
        public static string ResolveMetadataSource(A3SRepository repository)
        {
            ArgumentNullException.ThrowIfNull(repository);
            if (!string.IsNullOrWhiteSpace(repository.AutoconfigUrl))
                return repository.AutoconfigUrl.Trim();

            try
            {
                var publish = A3SBuildService.ResolvePublishRoot(repository);
                if (!string.IsNullOrWhiteSpace(publish) && Directory.Exists(publish))
                    return publish;
            }
            catch
            {
                // fall through
            }

            if (!string.IsNullOrWhiteSpace(repository.LocalPath) && Directory.Exists(repository.LocalPath))
                return repository.LocalPath.Trim();

            throw new InvalidOperationException(
                $"Repository \"{repository.Name}\" has no autoconfig URL or local content root for metadata.");
        }

        /// <summary>
        /// Prefer local publish/content root when <c>.a3s/events</c> exists (host authoring),
        /// otherwise fall back to <see cref="ResolveMetadataSource"/>.
        /// </summary>
        public static string ResolveEventsReadSource(A3SRepository repository)
        {
            ArgumentNullException.ThrowIfNull(repository);
            try
            {
                var root = ResolveWritableContentRoot(repository);
                var eventsPath = Path.Combine(root, A3SUrlHelpers.A3SFolderName, A3SObjectStreamMapper.KindEvents);
                if (File.Exists(eventsPath))
                    return root;
            }
            catch
            {
                // fall through to remote/autoconfig
            }

            return ResolveMetadataSource(repository);
        }

        /// <summary>List events from repository metadata via <see cref="IA3SMetadataStore"/>.</summary>
        public async Task<IReadOnlyList<A3SEventInfo>> ListEventsAsync(
            A3SRepository repository,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(repository);
            var source = ResolveEventsReadSource(repository);

            try
            {
                var events = await _metadataStore.ReadEventsAsync(source, cancellationToken).ConfigureAwait(false);
                return events.List ?? new List<A3SEventInfo>();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Missing events file — try ReadAll which tolerates null events.
                try
                {
                    var bundle = await _metadataStore.ReadAllAsync(source, cancellationToken).ConfigureAwait(false);
                    return bundle.Events?.List ?? (IReadOnlyList<A3SEventInfo>)Array.Empty<A3SEventInfo>();
                }
                catch
                {
                    return Array.Empty<A3SEventInfo>();
                }
            }
        }

        /// <summary>
        /// Parse an Arma 3 Launcher HTML preset and match mods onto this repository's known addon keys.
        /// Does not write <c>.a3s/events</c> — caller reviews unmatched rows then <see cref="UpsertEventAsync"/>.
        /// </summary>
        public async Task<A3SPresetImportResult> ImportLauncherPresetAsync(
            A3SRepository repository,
            string presetHtmlPath,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(repository);
            var preset = A3SArmaLauncherPresetParser.ParseFile(presetHtmlPath);
            var known = await ListKnownAddonNamesAsync(repository, cancellationToken).ConfigureAwait(false);
            return A3SArmaLauncherPresetParser.MatchToAddonKeys(preset, known);
        }

        /// <summary>
        /// Known addon folder names from local sync metadata and/or publish-tree directories
        /// that look like Arma3Sync addons (<c>addons/</c> child or <c>@</c> prefix).
        /// </summary>
        public async Task<IReadOnlyList<string>> ListKnownAddonNamesAsync(
            A3SRepository repository,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(repository);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string? contentRoot = null;
            try
            {
                contentRoot = ResolveWritableContentRoot(repository);
            }
            catch
            {
                // optional — may still read remote sync
            }

            // Prefer local sync tree (markAsAddon folders).
            foreach (var candidate in new[] { contentRoot, TrySafe(() => ResolveMetadataSource(repository)) })
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;
                try
                {
                    var sync = await _metadataStore.ReadSyncTreeAsync(candidate, cancellationToken).ConfigureAwait(false);
                    foreach (var path in sync.AddonFolderRelativePaths())
                    {
                        var key = NormalizeAddonEventKey(path);
                        if (!string.IsNullOrEmpty(key))
                            names.Add(key);
                    }

                    if (names.Count > 0)
                        break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // try next source / disk walk
                }
            }

            // Disk walk of publish/content root (works before first Build).
            if (contentRoot != null && Directory.Exists(contentRoot))
            {
                foreach (var name in EnumerateAddonFoldersOnDisk(contentRoot))
                    names.Add(name);
            }

            // Keep any keys already present on events so edits do not drop orphans.
            try
            {
                foreach (var evt in await ListEventsAsync(repository, cancellationToken).ConfigureAwait(false))
                {
                    foreach (var key in evt.AddonNames.Keys)
                    {
                        if (!string.IsNullOrWhiteSpace(key))
                            names.Add(key.Trim());
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ignore
            }

            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Write the full events list to <c>.a3s/events</c> under the publish/content root.</summary>
        public async Task SaveEventsAsync(
            A3SRepository repository,
            A3SEvents events,
            string? contentRootOverride = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(repository);
            ArgumentNullException.ThrowIfNull(events);
            events.List ??= new List<A3SEventInfo>();

            var contentRoot = !string.IsNullOrWhiteSpace(contentRootOverride)
                ? contentRootOverride!.Trim()
                : ResolveWritableContentRoot(repository);

            Directory.CreateDirectory(contentRoot);
            await _metadataStore.WriteMetadataAsync(contentRoot, "events", events, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Create or update an event (modset). When <paramref name="originalName"/> differs from
        /// <paramref name="name"/>, renames that entry. Addon map values: <c>false</c>=required, <c>true</c>=optional.
        /// </summary>
        public async Task<A3SEventInfo> UpsertEventAsync(
            A3SRepository repository,
            string name,
            string? description,
            IDictionary<string, bool>? addonNames,
            string? originalName = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(repository);
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Event name is required.", nameof(name));

            var trimmed = name.Trim();
            var lookup = string.IsNullOrWhiteSpace(originalName) ? trimmed : originalName.Trim();

            var list = (await ListEventsAsync(repository, cancellationToken).ConfigureAwait(false)).ToList();
            var existing = list.FirstOrDefault(e =>
                string.Equals(e.Name, lookup, StringComparison.OrdinalIgnoreCase));

            // Collision when renaming/creating onto another event's name.
            var conflict = list.FirstOrDefault(e =>
                !ReferenceEquals(e, existing)
                && string.Equals(e.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (conflict != null)
                throw new InvalidOperationException($"An event named \"{trimmed}\" already exists.");

            A3SEventInfo target;
            if (existing == null)
            {
                target = new A3SEventInfo { Name = trimmed };
                list.Add(target);
            }
            else
            {
                target = existing;
                target.Name = trimmed;
            }

            target.Description = description?.Trim() ?? string.Empty;
            target.AddonNames = NormalizeAddonMap(addonNames);
            target.DlcNames ??= new Dictionary<string, bool>();
            target.UserconfigFolderNames ??= new Dictionary<string, bool>();

            await SaveEventsAsync(repository, new A3SEvents { List = list }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return target;
        }

        /// <summary>Remove an event by name and rewrite <c>.a3s/events</c>.</summary>
        public async Task<bool> DeleteEventAsync(
            A3SRepository repository,
            string eventName,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(repository);
            if (string.IsNullOrWhiteSpace(eventName))
                throw new ArgumentException("Event name is required.", nameof(eventName));

            var list = (await ListEventsAsync(repository, cancellationToken).ConfigureAwait(false)).ToList();
            var removed = list.RemoveAll(e =>
                string.Equals(e.Name, eventName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return false;

            await SaveEventsAsync(repository, new A3SEvents { List = list }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Map event selected addons onto a profile's client / server / HC checkboxes.
        /// Unmatched addon names are reported; optional clear of prior checks on those lists.
        /// </summary>
        public A3SEventApplyResult ApplyEventToProfile(
            A3SEventInfo evt,
            ServerProfile profile,
            A3SEventApplyOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(evt);
            ArgumentNullException.ThrowIfNull(profile);
            options ??= new A3SEventApplyOptions();

            if (profile.ProfileMods == null)
                profile.ProfileMods = new List<ProfileMod>();

            if (options.ClearExistingOnTargetLists)
            {
                foreach (var mod in profile.ProfileMods)
                {
                    if (options.ApplyClient)
                        mod.ClientSideChecked = false;
                    if (options.ApplyServer)
                        mod.ServerSideChecked = false;
                    if (options.ApplyHeadless)
                        mod.HeadlessChecked = false;
                }
            }

            // Keys = event membership; bool = isOptional (not "selected").
            var selected = evt.SelectedAddonNames.ToList();
            var matched = new List<string>();
            var missing = new List<string>();
            ushort loadPriority = 1;

            System.Diagnostics.Debug.WriteLine(
                $"[A3S] ApplyEvent \"{evt.Name}\": {selected.Count} addon key(s), " +
                $"{profile.ProfileMods.Count} profile mod(s), " +
                $"Client={options.ApplyClient} Server={options.ApplyServer} HC={options.ApplyHeadless}");

            foreach (var addonKey in selected)
            {
                var mod = FindMatchingProfileMod(profile.ProfileMods, addonKey);
                if (mod == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[A3S]   missing addon key [{addonKey}]");
                    missing.Add(addonKey);
                    continue;
                }

                if (options.ApplyClient)
                    mod.ClientSideChecked = true;
                if (options.ApplyServer)
                    mod.ServerSideChecked = true;
                if (options.ApplyHeadless)
                    mod.HeadlessChecked = true;

                if (options.SetLoadPriority)
                {
                    mod.LoadPriority = loadPriority;
                    loadPriority++;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[A3S]   matched [{addonKey}] → \"{mod.Name}\" (Id={mod.Id})");
                matched.Add(mod.Name);
            }

            ApplyDlcFlags(profile, evt);

            profile.A3SEventName = evt.Name;
            profile.RaisePropertyChanged(nameof(ServerProfile.ProfileMods));
            profile.RaisePropertyChanged(nameof(ServerProfile.FilteredProfileMods));
            profile.RaisePropertyChanged(nameof(ServerProfile.CommandLine));

            return new A3SEventApplyResult(evt.Name, matched, missing, selected.Count);
        }

        /// <summary>
        /// Upsert a favorite server into repository autoconfig from profile connection info,
        /// writing via <see cref="IA3SMetadataStore"/> under the publish/local content root.
        /// </summary>
        public async Task<A3SFavoriteServer> WriteFavoriteServerFromProfileAsync(
            A3SRepository repository,
            ServerProfile profile,
            string? contentRootOverride = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(repository);
            ArgumentNullException.ThrowIfNull(profile);

            var contentRoot = !string.IsNullOrWhiteSpace(contentRootOverride)
                ? contentRootOverride!.Trim()
                : ResolveWritableContentRoot(repository);

            A3SAutoconfigInfo autoconfig;
            try
            {
                autoconfig = await _metadataStore.ReadAutoconfigAsync(contentRoot, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Fall back to remote autoconfig URL, then empty shell.
                try
                {
                    if (!string.IsNullOrWhiteSpace(repository.AutoconfigUrl))
                    {
                        autoconfig = await _metadataStore
                            .ReadAutoconfigAsync(repository.AutoconfigUrl, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        autoconfig = new A3SAutoconfigInfo { RepositoryName = repository.Name };
                    }
                }
                catch
                {
                    autoconfig = new A3SAutoconfigInfo { RepositoryName = repository.Name };
                }
            }

            if (string.IsNullOrWhiteSpace(autoconfig.RepositoryName))
                autoconfig.RepositoryName = repository.Name;

            autoconfig.FavoriteServers ??= new List<A3SFavoriteServer>();

            var favorite = BuildFavoriteFromProfile(profile, autoconfig.RepositoryName);
            var existing = autoconfig.FavoriteServers.FirstOrDefault(f =>
                string.Equals(f.Name, favorite.Name, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(f.IpAddress, favorite.IpAddress, StringComparison.OrdinalIgnoreCase)
                    && f.Port == favorite.Port));

            if (existing != null)
            {
                existing.Name = favorite.Name;
                existing.IpAddress = favorite.IpAddress;
                existing.Port = favorite.Port;
                existing.Password = favorite.Password;
                existing.ModsetName = favorite.ModsetName;
                existing.RepositoryName = favorite.RepositoryName;
                existing.Selected = favorite.Selected;
                favorite = existing;
            }
            else
            {
                autoconfig.FavoriteServers.Add(favorite);
            }

            Directory.CreateDirectory(contentRoot);
            await _metadataStore.WriteMetadataAsync(contentRoot, "autoconfig", autoconfig, cancellationToken)
                .ConfigureAwait(false);

            return favorite;
        }

        /// <summary>
        /// One-click: Sync repository → import mods → apply event → Deploy All → (optional) launch callback.
        /// </summary>
        public async Task<A3SEventLaunchResult> SyncEventDeployLaunchAsync(
            A3SRepository repository,
            ServerProfile profile,
            string eventName,
            A3SEventApplyOptions? applyOptions = null,
            A3SSyncOptions? syncOptions = null,
            IProgress<A3SSyncProgress>? progress = null,
            Func<ServerProfile, Task>? launchAsync = null,
            Action? deployAll = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(repository);
            ArgumentNullException.ThrowIfNull(profile);
            if (string.IsNullOrWhiteSpace(eventName))
                throw new ArgumentException("Event name is required.", nameof(eventName));

            progress?.Report(new A3SSyncProgress { Message = $"Listing events for \"{repository.Name}\"…" });
            var events = await ListEventsAsync(repository, cancellationToken).ConfigureAwait(false);
            var evt = events.FirstOrDefault(e =>
                string.Equals(e.Name, eventName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (evt == null)
                throw new InvalidOperationException($"Event \"{eventName}\" was not found in repository metadata.");

            progress?.Report(new A3SSyncProgress { Message = $"Syncing \"{repository.Name}\"…" });
            var syncResult = await _syncService
                .SyncAsync(repository, syncOptions ?? new A3SSyncOptions(), progress, cancellationToken)
                .ConfigureAwait(false);

            A3SModImportResult? importResult = null;
            try
            {
                progress?.Report(new A3SSyncProgress { Message = "Importing synced mods…" });
                importResult = await _modImport
                    .ImportFromRepositoryAsync(
                        repository,
                        addonRelativePaths: syncResult.AddonRelativePaths,
                        syncDeploymentList: true,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                progress?.Report(new A3SSyncProgress { Message = $"Mod import warning: {ex.Message}" });
            }

            // Refresh profile mod catalog from settings so new imports are selectable.
            MergeArmaModsIntoProfile(profile);

            progress?.Report(new A3SSyncProgress { Message = $"Applying event \"{evt.Name}\"…" });
            var apply = ApplyEventToProfile(evt, profile, applyOptions);
            AttachEventToProfile(profile, repository, evt.Name);

            progress?.Report(new A3SSyncProgress { Message = "Deploying mods…" });
            if (deployAll != null)
                deployAll();
            else
                TryDeployAll();

            _repositoryService.Persist();
            PersistProfile(profile);

            var launched = false;
            if (launchAsync != null)
            {
                progress?.Report(new A3SSyncProgress { Message = $"Launching profile \"{profile.Name}\"…" });
                await launchAsync(profile).ConfigureAwait(true);
                launched = true;
            }

            return new A3SEventLaunchResult(syncResult, importResult, apply, launched);
        }

        /// <summary>Persist attach fields on the profile and mirror into an <see cref="A3SEventRef"/> if desired by callers.</summary>
        public void AttachEventToProfile(ServerProfile profile, A3SRepository repository, string eventName)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(repository);
            profile.A3SRepositoryId = repository.Id;
            profile.A3SEventName = eventName ?? string.Empty;
            PersistProfile(profile);
        }

        public A3SRepository? FindAttachedRepository(ServerProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (string.IsNullOrWhiteSpace(profile.A3SRepositoryId))
                return null;
            return _repositoryService.Repositories.FirstOrDefault(r =>
                string.Equals(r.Id, profile.A3SRepositoryId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Operator-facing guard before Apply / Sync→Deploy→Launch.
        /// Returns null when ready; otherwise a clear message (never ArgumentNullException).
        /// </summary>
        public static string? GetApplyPrerequisiteError(A3SRepository? repository, string? eventName)
        {
            if (repository == null)
                return "Select an A3S repository first.";
            if (string.IsNullOrWhiteSpace(eventName))
                return "Select an A3S event first.";
            return null;
        }

        /// <summary>
        /// Resolve a repository for apply: prefer an already-selected instance, else look up by
        /// <see cref="ServerProfile.A3SRepositoryId"/> in the persisted repository list.
        /// </summary>
        public A3SRepository? ResolveRepositoryForApply(ServerProfile profile, A3SRepository? selected = null)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (selected != null)
                return selected;
            return FindAttachedRepository(profile);
        }

        public static A3SFavoriteServer BuildFavoriteFromProfile(ServerProfile profile, string repositoryName)
        {
            ArgumentNullException.ThrowIfNull(profile);
            var name = !string.IsNullOrWhiteSpace(profile.ServerCfg?.Hostname)
                ? profile.ServerCfg.Hostname
                : profile.Name;
            var ip = profile.A3SPublicIp?.Trim() ?? string.Empty;
            return new A3SFavoriteServer
            {
                Name = name ?? string.Empty,
                IpAddress = ip,
                Port = profile.Port <= 0 ? 2302 : profile.Port,
                Password = profile.ServerCfg?.Password ?? string.Empty,
                Selected = false,
                ModsetName = profile.A3SEventName ?? string.Empty,
                RepositoryName = repositoryName ?? string.Empty
            };
        }

        public static ProfileMod? FindMatchingProfileMod(IEnumerable<ProfileMod> mods, string addonKey)
        {
            if (mods == null || string.IsNullOrWhiteSpace(addonKey))
                return null;

            var key = addonKey.Trim().Replace('\\', '/');
            // Event keys are often "450814997" or "@CBA_A3"; tolerate path-ish forms.
            var basename = key.Contains('/') ? key[(key.LastIndexOf('/') + 1)..] : key;
            var keyNoAt = basename.StartsWith("@", StringComparison.Ordinal) ? basename[1..] : basename;
            var keyCompare = ModUtilities.GetCompareString(keyNoAt);
            var keySafe = Functions.SafeName(keyNoAt);
            var keyIsWorkshopId = uint.TryParse(keyNoAt, out var workshopId);

            foreach (var mod in mods)
            {
                if (mod == null || string.IsNullOrWhiteSpace(mod.Name))
                    continue;

                // Workshop folder id (classic A3S event keys for Steam/content folders)
                if (keyIsWorkshopId && mod.Id == workshopId)
                    return mod;

                if (string.Equals(mod.Name, key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mod.Name, basename, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mod.Name, keyNoAt, StringComparison.OrdinalIgnoreCase)
                    || string.Equals($"@{mod.Name}", basename, StringComparison.OrdinalIgnoreCase)
                    || string.Equals($"@{mod.Name}", key, StringComparison.OrdinalIgnoreCase))
                    return mod;

                if (string.Equals(Functions.SafeName(mod.Name), keySafe, StringComparison.OrdinalIgnoreCase))
                    return mod;

                if (string.Equals(ModUtilities.GetCompareString(mod.Name), keyCompare, StringComparison.OrdinalIgnoreCase))
                    return mod;

                // Name stored as raw workshop folder id
                if (keyIsWorkshopId
                    && uint.TryParse(ModUtilities.GetCompareString(mod.Name), out var nameAsId)
                    && nameAsId == workshopId)
                    return mod;
            }

            return null;
        }

        private static void ApplyDlcFlags(ServerProfile profile, A3SEventInfo evt)
        {
            if (evt.DlcNames == null || evt.DlcNames.Count == 0)
                return;

            foreach (var (name, selected) in evt.DlcNames)
            {
                if (!selected)
                    continue;
                var key = name.Trim().ToLowerInvariant();
                switch (key)
                {
                    case "contact":
                    case "enoch":
                        profile.ContactDLCChecked = true;
                        break;
                    case "gm":
                        profile.GMDLCChecked = true;
                        break;
                    case "vn":
                    case "sog":
                    case "sogpf":
                    case "pf":
                        profile.PFDLCChecked = true;
                        break;
                    case "csla":
                        profile.CSLADLCChecked = true;
                        break;
                    case "ws":
                        profile.WSDLCChecked = true;
                        break;
                    case "spe":
                        profile.SPEDLCChecked = true;
                        break;
                    case "rf":
                        profile.RFDLCChecked = true;
                        break;
                    case "ef":
                        profile.EFDLCChecked = true;
                        break;
                }
            }
        }

        private void MergeArmaModsIntoProfile(ServerProfile profile)
        {
            if (_settings.armaMods?.ArmaMods == null)
                return;

            var modlist = new List<ProfileMod>();
            foreach (var mod in _settings.armaMods.ArmaMods)
            {
                var existing = profile.ProfileMods.Find(m => m.Id == mod.WorkshopId);
                if (existing == null)
                {
                    modlist.Add(new ProfileMod
                    {
                        Name = mod.Name,
                        Id = mod.WorkshopId,
                        IsLocal = mod.IsLocal
                    });
                }
                else
                {
                    existing.Name = mod.Name;
                    modlist.Add(existing);
                }
            }

            // Keep any profile-only entries that were already checked.
            foreach (var leftover in profile.ProfileMods.Where(pm =>
                         modlist.All(m => m.Id != pm.Id)))
            {
                modlist.Add(leftover);
            }

            profile.ProfileMods = modlist;
        }

        private static string ResolveWritableContentRoot(A3SRepository repository)
        {
            try
            {
                var publish = A3SBuildService.ResolvePublishRoot(repository);
                if (!string.IsNullOrWhiteSpace(publish))
                    return publish;
            }
            catch
            {
                // fall through
            }

            if (!string.IsNullOrWhiteSpace(repository.LocalPath))
                return repository.LocalPath.Trim();

            throw new InvalidOperationException(
                $"Repository \"{repository.Name}\" needs a LocalPath or Publish Root to write events/autoconfig.");
        }

        private static Dictionary<string, bool> NormalizeAddonMap(IDictionary<string, bool>? addonNames)
        {
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (addonNames == null)
                return map;

            foreach (var kv in addonNames)
            {
                var key = NormalizeAddonEventKey(kv.Key);
                if (string.IsNullOrEmpty(key))
                    continue;
                map[key] = kv.Value;
            }

            return map;
        }

        /// <summary>Event keys are folder names (workshop id or <c>@mod</c>), not nested paths.</summary>
        public static string NormalizeAddonEventKey(string? pathOrName)
        {
            if (string.IsNullOrWhiteSpace(pathOrName))
                return string.Empty;
            var trimmed = pathOrName.Trim().Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(trimmed))
                return string.Empty;
            var slash = trimmed.LastIndexOf('/');
            return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        }

        private static IEnumerable<string> EnumerateAddonFoldersOnDisk(string contentRoot)
        {
            string[] entries;
            try
            {
                entries = Directory.GetDirectories(contentRoot);
            }
            catch
            {
                yield break;
            }

            foreach (var dir in entries)
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name) || A3SUrlHelpers.IsMetadataSidecarDirectory(name))
                    continue;

                var looksLikeAddon = name.StartsWith('@')
                    || Directory.Exists(Path.Combine(dir, A3SBuildService.AddonsFolderName));
                if (looksLikeAddon)
                    yield return name;
            }
        }

        private static T? TrySafe<T>(Func<T> factory)
        {
            try { return factory(); }
            catch { return default; }
        }

        private void PersistProfile(ServerProfile profile)
        {
            if (_settings.Profiles == null)
                return;

            var index = _settings.Profiles.FindIndex(p => p.Id == profile.Id);
            if (index != -1)
                _settings.Profiles[index] = profile;
            _settings.Save();
        }

        private void TryDeployAll()
        {
            try
            {
                if (_settings.Deployments == null)
                    return;

                var installPath = _settings.Deployments.InstallPath;
                if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
                    return;

                foreach (var mod in _settings.Deployments.DeployMods)
                {
                    if (mod == null || string.IsNullOrWhiteSpace(mod.Path) || !Directory.Exists(mod.Path))
                        continue;

                    var linkPath = Path.Combine(installPath, $"@{Functions.SafeName(mod.Name)}");
                    if (Directory.Exists(linkPath))
                    {
                        var info = new DirectoryInfo(linkPath);
                        if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            continue;
                        try { Directory.Delete(linkPath); } catch { /* skip */ }
                    }

                    try
                    {
                        Directory.CreateSymbolicLink(linkPath, mod.Path);
                        mod.Marked = true;
                    }
                    catch
                    {
                        // Symlinks require admin / Developer Mode — caller may deploy via UI.
                    }
                }

                _settings.Deployments = _settings.Deployments;
                _settings.Save();
            }
            catch
            {
                // Non-fatal; UI Deploy remains available.
            }
        }
    }

    public sealed class A3SEventApplyOptions
    {
        /// <summary>Map event addons onto Client (+ <c>-mod</c>). Default on.</summary>
        public bool ApplyClient { get; init; } = true;
        /// <summary>Map onto Server-only (+ <c>-serverMod</c>). Default off — typical A3S events are client modsets.</summary>
        public bool ApplyServer { get; init; } = false;
        /// <summary>Map onto Headless. Default on.</summary>
        public bool ApplyHeadless { get; init; } = true;
        public bool ClearExistingOnTargetLists { get; init; } = true;
        public bool SetLoadPriority { get; init; } = true;
    }

    public sealed record A3SEventApplyResult(
        string EventName,
        IReadOnlyList<string> MatchedMods,
        IReadOnlyList<string> MissingAddons,
        int EventAddonCount = 0);

    public sealed record A3SEventLaunchResult(
        A3SSyncResult Sync,
        A3SModImportResult? Import,
        A3SEventApplyResult Apply,
        bool Launched);
}
