using FASTER.Models;
using FASTER.Services.A3S;

using Microsoft.AppCenter.Analytics;
using Microsoft.AppCenter.Crashes;
using Microsoft.WindowsAPICodePack.Dialogs;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace FASTER.ViewModel
{
    internal class ProfileViewModel
    {
        public ProfileViewModel()
        { Profile = new ServerProfile("Server", false); }

        public ProfileViewModel(ServerProfile p)
        { Profile = p; }
        public ServerProfile Profile { get; set; }

        public ObservableCollection<string> VonCodecs           { get; } = new ObservableCollection<string>(ServerCfgArrays.VonCodecStrings);
        public ObservableCollection<string> FilePatching        { get; } = new ObservableCollection<string>(ServerCfgArrays.AllowFilePatchingStrings);
        public ObservableCollection<string> VerifySignatures    { get; } = new ObservableCollection<string>(ServerCfgArrays.VerifySignaturesStrings);
        public ObservableCollection<string> TimestampFormats    { get; } = new ObservableCollection<string>(ServerCfgArrays.TimeStampStrings);
        public ObservableCollection<string> EnabledStrings      { get; } = new ObservableCollection<string>(ProfileCfgArrays.EnabledStrings);
        public ObservableCollection<string> MissionDifficulties { get; } = new ObservableCollection<string> { "Recruit", "Regular", "Veteran", "Custom" };
        public ObservableCollection<string> PerfPresets         { get; } = new ObservableCollection<string>(BasicCfgArrays.PerfPresets);
        public ObservableCollection<double> TerrainGrids        { get; } = new ObservableCollection<double>(BasicCfgArrays.TerrainGrids);
        public ObservableCollection<string> Languages           { get; } = new ObservableCollection<string>(BasicCfgArrays.Languages);

        internal void DisplayMessage(string msg)
        {
            MainWindow.Instance.IFlyout.IsOpen         = true;
            MainWindow.Instance.IFlyoutMessage.Content = msg;
        }

        internal void OpenProfileLocation()
        {
            Analytics.TrackEvent("Profile - Clicked OpenProfile", new Dictionary<string, string>
            {
                {"Name", Properties.Settings.Default.steamUserName}
            });

            string folderPath = Path.Combine(Profile.ArmaPath, "Servers", Profile.Id);
            if (Directory.Exists(folderPath))
            {
                ProcessStartInfo startInfo = new()
                {
                    Arguments = folderPath,
                    FileName  = "explorer.exe"
                };

                Process.Start(startInfo);
            }
            else
            { DisplayMessage("Could not open profile location..."); }
        }

        internal void LaunchHCs()
        {
            if (!Profile.ServerCfg.HeadlessClientEnabled) return;
            if (!EnsureExecutableResolved()) return;
            if (!VerifyBeforeLaunch()) return;

            DisplayMessage($"Launching Headless Clients for {Profile.Name}...");
            for (int hc = 1; hc <= Profile.HeadlessNumber; hc++)
            {
                var commandLine = SetHCCommandLine(hc);
                try
                {
                    ServerLaunch.StartServer(Profile.Executable, commandLine, Profile.ArmaPath);
                }
                catch (Exception ex)
                {
                    DisplayMessage($"Failed to start headless client #{hc}: {ex.Message}");
                    return;
                }
            }
        }

        private string SetHCCommandLine(int hc)
        {
            string headlessMods = string.Join(";", Profile.ProfileMods.Where(m => m.HeadlessChecked).Select(m =>$"@{Functions.SafeName(m.Name)}"));
            List<string> arguments = new()
            {
                "-client",
                " -connect=127.0.0.1",
                $" -password={Profile.ServerCfg.Password}",
                $" \"-profiles={Path.Combine(Profile.ArmaPath, "Servers", $"{Profile.Id}_hc{hc}")}\"",
                " -nosound",
                $" -port={Profile.Port}",
                Profile.GetDlcAndPlayerMods(headlessMods),
                $"{(Profile.ServerCfg.MaxMemOverride ? $" -maxMem={Profile.ServerCfg.MaxMem}" : "")}",
                $"{(Profile.ServerCfg.CpuCountOverride ? $" -cpuCount={Profile.ServerCfg.CpuCount}" : "")}",
                $"{(Profile.EnableHyperThreading ? " -enableHT" : "")}",
                $"{(!string.IsNullOrWhiteSpace(Profile.ServerCfg.CommandLineParameters) ? $" {Profile.ServerCfg.CommandLineParameters}" : "")}"
            };

            string commandLine = string.Join("", arguments);

            try { Clipboard.SetText(commandLine); }
            catch (COMException e)
            {
                try
                {
                    Crashes.TrackError(e, new Dictionary<string, string> { { "Name", Properties.Settings.Default.steamUserName } });
                    Clipboard.SetDataObject(commandLine);
                }
                catch (COMException ex) { Crashes.TrackError(ex, new Dictionary<string, string> { { "Name", Properties.Settings.Default.steamUserName } }); }
            }
            return commandLine;
        }

        internal async Task LaunchServer()
        {
            if (!EnsureExecutableResolved())
                return;

            if (Properties.Settings.Default.a3sSyncBeforeLaunch
                && !string.IsNullOrWhiteSpace(Profile.A3SRepositoryId)
                && !string.IsNullOrWhiteSpace(Profile.A3SEventName))
            {
                var eventService = new A3SEventService();
                var repo = eventService.FindAttachedRepository(Profile);
                if (repo == null)
                {
                    DisplayMessage("Attached A3S repository not found; launching with current profile mods.");
                }
                else
                {
                    try
                    {
                        DisplayMessage($"A3S sync-before-launch: {Profile.A3SEventName}…");
                        Action? deploy = null;
                        if (MainWindow.HasLoaded())
                            deploy = () => MainWindow.Instance.DeployViewModel.DeployAll();

                        await eventService.SyncEventDeployLaunchAsync(
                                repo,
                                Profile,
                                Profile.A3SEventName,
                                deployAll: deploy,
                                launchAsync: null)
                            .ConfigureAwait(true);
                        DisplayMessage($"Synced event \"{Profile.A3SEventName}\" — launching…");
                    }
                    catch (Exception ex)
                    {
                        // Never abort Process.Start because sync/network failed — apply event
                        // checkboxes from metadata when possible, then launch with what we have.
                        DisplayMessage($"A3S sync-before-launch failed: {ex.Message}\nApplying event from metadata and launching anyway…");
                        try
                        {
                            await ApplyAttachedEventFromMetadataAsync(eventService, repo).ConfigureAwait(true);
                        }
                        catch (Exception applyEx)
                        {
                            DisplayMessage($"Could not apply A3S event \"{Profile.A3SEventName}\": {applyEx.Message}\nLaunching with current profile selection.");
                        }
                    }
                }
            }

            LaunchServerCore();
        }

        /// <summary>Apply attached event addon checks without syncing content (metadata only).</summary>
        private async Task ApplyAttachedEventFromMetadataAsync(A3SEventService eventService, A3SRepository repo)
        {
            var events = await eventService.ListEventsAsync(repo).ConfigureAwait(true);
            var evt = events.FirstOrDefault(e =>
                string.Equals(e.Name, Profile.A3SEventName, StringComparison.OrdinalIgnoreCase));
            if (evt == null)
                throw new InvalidOperationException($"Event \"{Profile.A3SEventName}\" was not found in repository metadata.");

            LoadData();
            var result = eventService.ApplyEventToProfile(evt, Profile);
            eventService.AttachEventToProfile(Profile, repo, evt.Name);
            DisplayMessage(
                $"Applied \"{result.EventName}\" from metadata: matched {result.MatchedMods.Count}, missing {result.MissingAddons.Count}.");
        }

        /// <summary>Launch without running sync-before-launch (used by Sync → Deploy → Launch).</summary>
        internal void LaunchServerCore()
        {
            if (!EnsureExecutableResolved())
                return;
            if (!VerifyBeforeLaunch())
                return;

            DisplayMessage($"Launching Profile {Profile.Name}...");

            Analytics.TrackEvent("Profile - Clicked LaunchServer", new Dictionary<string, string>
            {
                {"Name", Properties.Settings.Default.steamUserName}
            });

            Profile.RaisePropertyChanged("CommandLine");
            var commandLine = Profile.CommandLine;
            try { Clipboard.SetText(commandLine); }
            catch (COMException e)
            {
                try
                {
                    Crashes.TrackError(e, new Dictionary<string, string>
                                           {{ "Name", Properties.Settings.Default.steamUserName }});
                    Clipboard.SetDataObject(commandLine);
                }
                catch (COMException ex)
                {
                    Crashes.TrackError(ex, new Dictionary<string, string>
                                           {{ "Name", Properties.Settings.Default.steamUserName }});
                }
            }

            var selectedMods = ServerLaunch.CountSelectedLaunchMods(Profile);
            if (selectedMods == 0)
            {
                DisplayMessage(
                    "No mods are checked on this profile (Client / Server / HC). " +
                    "Launching anyway — use Apply A3S Event or tick mods before Launch if you need -mod.");
            }
            else
            {
                var missing = ServerLaunch.MissingDeployedMods(Profile);
                if (missing.Count > 0)
                {
                    DisplayMessage(
                        $"{missing.Count} selected mod folder(s) missing under the server directory (Deploy first):\n" +
                        string.Join("\n", missing.Take(8)) +
                        (missing.Count > 8 ? "\n…" : string.Empty));
                }
            }

            try
            {
                var process = ServerLaunch.StartServer(Profile.Executable, commandLine, Profile.ArmaPath);
                DisplayMessage(
                    $"Profile {Profile.Name} launched (PID {process.Id}).\n" +
                    $"Exe: {Path.GetFileName(Profile.Executable)} | Mods selected: {selectedMods}\n" +
                    "Command line copied to clipboard.");
                LaunchHCs();
            }
            catch (Exception ex)
            {
                DisplayMessage(
                    $"Failed to start Arma 3 server:\n{ex.Message}\n" +
                    $"Executable: {Profile.Executable}\nWorking directory: {Profile.ArmaPath}");
            }
        }

        /// <summary>
        /// Resolve arma3server_x64.exe (preferred) under serverPath / profile path when missing or invalid.
        /// </summary>
        private bool EnsureExecutableResolved()
        {
            var resolved = ServerLaunch.ResolveExecutable(
                Profile.Executable,
                Properties.Settings.Default.serverPath);

            if (resolved == null)
            {
                DisplayMessage(
                    "Could not find arma3server_x64.exe (or arma3server.exe).\n" +
                    "Set Settings → Arma server path / profile Server File to your dedicated server install " +
                    "(Steam app 233780), then Save the profile.");
                return false;
            }

            if (!string.Equals(Profile.Executable, resolved, StringComparison.OrdinalIgnoreCase))
            {
                Profile.Executable = resolved;
                DisplayMessage($"Server executable resolved to:\n{resolved}");
            }

            return true;
        }

        /// <summary>
        /// Check if the server can be launched
        /// </summary>
        private bool VerifyBeforeLaunch()
        {
            var result = ServerLaunch.ValidateForLaunch(Profile, requireConfigFiles: true);
            if (!result.Ok)
            {
                DisplayMessage(result.Message);
                return false;
            }

            return true;
        }

        internal void DeleteProfile()
        {
            if (Directory.Exists(Path.Combine(Profile.ArmaPath, "Servers", Profile.Id)))
            { Directory.Delete(Path.Combine(Profile.ArmaPath, "Servers", Profile.Id), true); }
            Properties.Settings.Default.Profiles.Remove(Profile);
            Properties.Settings.Default.Save();
            MainWindow.Instance.ContentProfileViews.Remove(MainWindow.Instance.ContentProfileViews.Find(p => p.Profile.Id == Profile.Id));

            MainWindow.Instance.NavigateToConsole();
            MainWindow.Instance.LoadServerProfiles();
        }

        internal void SaveProfile()
        {
            var armaPath = Path.GetDirectoryName(Profile.Executable);

            if(string.IsNullOrWhiteSpace(armaPath))
            {
                DisplayMessage("Arma executable is empty. Select the correct executable before saving your profile.");
                return;
            }

            string config        = Path.Combine(Profile.ArmaPath, "Servers", Profile.Id, "server_config.cfg");
            string basic         = Path.Combine(Profile.ArmaPath, "Servers", Profile.Id, "server_basic.cfg");
            string serverProfile = Path.Combine(Profile.ArmaPath, "Servers", Profile.Id, "users", Profile.Id, $"{Profile.Id}.Arma3Profile");

            //Creating profile directory
            Directory.CreateDirectory(Path.Combine(Profile.ArmaPath, "Servers", Profile.Id, "users", Profile.Id));

            //Writing files
            try
            {
                File.WriteAllLines(config,        Profile.ServerCfg.ServerCfgContent.Replace("\r", "").Split('\n'));
                File.WriteAllLines(basic,         Profile.BasicCfg.BasicContent.Replace("\r", "").Split('\n'));
                File.WriteAllLines(serverProfile, Profile.ArmaProfile.ArmaProfileContent.Replace("\r", "").Split('\n'));
            }
            catch
            { DisplayMessage("Could not write the config files. Please ensure the server is not running and retry."); 
            return;
            }

            if(string.IsNullOrWhiteSpace(armaPath))
            {
                DisplayMessage("Arma executable is empty. Select the correct executable before saving your profile.");
                return;
            }

            var links = Directory.EnumerateDirectories(armaPath).Select(d => new DirectoryInfo(d)).Where(d => d.Attributes.HasFlag(FileAttributes.ReparsePoint));
            uint MissingMods = 0;
            foreach (ProfileMod profileMod in Profile.ProfileMods.Where(m => m.ServerSideChecked || m.ClientSideChecked || m.HeadlessChecked || m.OptChecked))
            {
                if (!links.Any(l => l.Name == $"@{Functions.SafeName(profileMod.Name)}"))
                    MissingMods++;
            }


            var index = Properties.Settings.Default.Profiles.FindIndex(p => p.Id == Profile.Id);  
            if (index != -1)
            { Properties.Settings.Default.Profiles[index] = Profile; }

            Properties.Settings.Default.Save();
            Profile.RaisePropertyChanged("CommandLine");
            DisplayMessage($"Saved Profile {Profile.Name}");

            if (MissingMods > 0)
                DisplayMessage($"{MissingMods} mods were not found in the Arma directory.\nMake sure you have deployed the correct mods");

        }

        public ObservableCollection<string> FadeOutStrings         { get; } = new ObservableCollection<string>(ProfileCfgArrays.FadeOutStrings);

        internal void LoadModsFromFile()
        {
            var dialog = new CommonOpenFileDialog
            {
                Title                     = "Select the Arma3 mod preset",
                IsFolderPicker            = false,
                AddToMostRecentlyUsedList = false,
                AllowNonFileSystemItems   = false,
                EnsureFileExists          = true,
                EnsurePathExists          = true,
                EnsureReadOnly            = false,
                EnsureValidNames          = true,
                Multiselect               = false,
                ShowPlacesList            = true
            };
            dialog.Filters.Add(new CommonFileDialogFilter("Arma 3 Mod Preset", ".html"));

            if (dialog.ShowDialog() != CommonFileDialogResult.Ok) return;

            if (dialog.FileName == null)
            {
                MessageBox.Show("Please enter a valid arma3server executable location");
                return;
            }

            //Clear mods
            foreach (var mod in Profile.ProfileMods)
            { mod.ClientSideChecked = false; }

            ushort? loadPriority = 1;
            List<ProfileMod> extractedModList = ModUtilities.ParseModsFromArmaProfileFile(dialog.FileName).Select(armaMod =>
            {
                return new ProfileMod
                {
                    Id = armaMod.WorkshopId,
                    Name = armaMod.Name,
                    IsLocal = armaMod.IsLocal
                };
            }).ToList();

            //Select new ones
            List<string> notFound = new();
            foreach (var extractedMod in extractedModList)
            {
                var mod = Profile.ProfileMods.Find(m => m.Id == extractedMod.Id || ModUtilities.GetCompareString(extractedMod.Name) == ModUtilities.GetCompareString(m.Name));
                if (mod != null)
                {
                    mod.ClientSideChecked = true;
                    mod.LoadPriority = loadPriority;
                    loadPriority += 1;
                }
                else
                {
                    notFound.Add(extractedMod.Name);
                }
            }

            // Display mods that weren't found in message
            if (notFound.Count > 0)
            {
                DisplayMessage($"Some mods in the preset were not found: \n{string.Join("\n\t", notFound)}");
            }
        }

        internal void SelectBePath()
        {
            var dialog = new CommonOpenFileDialog
            {
                Title                     = "Select the BattlEye directory",
                IsFolderPicker            = true,
                AddToMostRecentlyUsedList = false,
                AllowNonFileSystemItems   = false,
                EnsureFileExists          = false,
                EnsurePathExists          = true,
                EnsureReadOnly            = false,
                EnsureValidNames          = true,
                Multiselect               = false,
                ShowPlacesList            = true
            };

            if (dialog.ShowDialog() != CommonFileDialogResult.Ok) return;

            if (dialog.FileName != null)
            { Profile.BePath = dialog.FileName; }
            else
            { MessageBox.Show("Please enter a valid BattlEye directory"); }
        }

        internal void SelectKeysFolder()
        {
            var dialog = new CommonOpenFileDialog
            {
                Title                     = "Select the keys directory",
                IsFolderPicker            = true,
                AddToMostRecentlyUsedList = false,
                AllowNonFileSystemItems   = false,
                EnsureFileExists          = false,
                EnsurePathExists          = true,
                EnsureReadOnly            = false,
                EnsureValidNames          = true,
                Multiselect               = false,
                ShowPlacesList            = true
            };

            if (dialog.ShowDialog() != CommonFileDialogResult.Ok) return;

            if (dialog.FileName != null)
            { Profile.KeysFolder = dialog.FileName; }
            else
            { MessageBox.Show("Please enter a valid keys directory"); }
        }

        internal void SelectServerFile()
        {
            var dialog = new CommonOpenFileDialog
            {
                Title                     = "Select the arma server executable",
                IsFolderPicker            = false,
                AddToMostRecentlyUsedList = false,
                InitialDirectory          = Properties.Settings.Default.serverPath,
                DefaultDirectory          = Properties.Settings.Default.serverPath,
                AllowNonFileSystemItems   = false,
                EnsureFileExists          = true,
                EnsurePathExists          = true,
                EnsureReadOnly            = false,
                EnsureValidNames          = true,
                Multiselect               = false,
                ShowPlacesList            = true
            };
            dialog.Filters.Add(new CommonFileDialogFilter("Arma 3 Server Executable", ".exe"));

            if (dialog.ShowDialog() != CommonFileDialogResult.Ok) return;

            if (dialog.FileName != null)
            { Profile.Executable = dialog.FileName; }
            else
            { MessageBox.Show("Please enter a valid arma3server executable location"); }
        }

        internal async Task CopyModKeys()
        {
            var mods = new List<string>();

            if (!Directory.Exists(Properties.Settings.Default.modStagingDirectory))
            {
                MainWindow.Instance.IFlyout.IsOpen         = true;
                MainWindow.Instance.IFlyoutMessage.Content = $"The SteamCMD path does not exist :\n{Properties.Settings.Default.modStagingDirectory}";
                return;
            }

            var clientMods = Profile.ProfileMods.Where(p => p.ClientSideChecked).ToList();
            var optionalMods = Profile.ProfileMods.Where(p => p.OptChecked).ToList();
            var steamMods = clientMods.Union(optionalMods).ToList();

            foreach (var line in steamMods)
            {
                try
                { mods.AddRange(Directory.GetDirectories(Path.Combine(Properties.Settings.Default.modStagingDirectory, line.Id.ToString()))
				.SelectMany(subDir => Directory.GetFiles(subDir, "*.bikey", SearchOption.TopDirectoryOnly))); }
                catch (DirectoryNotFoundException)
                { /*there was no directory*/ }
            }

            await ClearModKeys();

            Directory.CreateDirectory(Path.Combine(Profile.ArmaPath, "keys"));

            foreach (var link in mods)
            {
                try { File.Copy(link, Path.Combine(Profile.ArmaPath, "keys", Path.GetFileName(link)), true); }
                catch (IOException)
                {
                    MainWindow.Instance.IFlyout.IsOpen         = true;
                    MainWindow.Instance.IFlyoutMessage.Content = $"Some keys could not be copied : {Path.GetFileName(link)}";
                }
            }
            await Task.Delay(1000);
        }

        internal async Task ClearModKeys()
        {
            var ignoredKeys = new[] {"a3.bikey", "a3c.bikey", "gm.bikey", "ws.bikey", "csla.bikey", "vn.bikey", "spe.bikey", "rf.bikey", "ef.bikey" };
            if (Directory.Exists(Path.Combine(Profile.ArmaPath, "keys")))
            {
                foreach (var keyFile in Directory.GetFiles(Path.Combine(Profile.ArmaPath, "keys")))
                {
                    if (Array.Exists(ignoredKeys, x => keyFile.Contains(x)))
                        continue;
                    try
                    {
                        await Task.Run(() => File.Delete(keyFile));
                    }
                    catch (Exception)
                    {
                        MainWindow.Instance.IFlyout.IsOpen         = true;
                        MainWindow.Instance.IFlyoutMessage.Content = $"Some keys could not be cleared : {Path.GetFileName(keyFile)}";
                    }
                }
            }
        }

        public ObservableCollection<string> LimitedDistanceStrings   { get; } = new ObservableCollection<string>(ProfileCfgArrays.LimitedDistanceStrings);
        public ObservableCollection<string> AiPresetStrings          { get; } = new ObservableCollection<string>(ProfileCfgArrays.AiPresetStrings);
        public ObservableCollection<string> ForcedDifficultyString   { get; } = new ObservableCollection<string> { "Recruit", "Regular", "Veteran", "Custom" };
        public ObservableCollection<string> ThirdPersonStrings       { get; } = new ObservableCollection<string>(ProfileCfgArrays.ThirdPersonStrings);
        public ObservableCollection<string> TacticalPingStrings      { get; } = new ObservableCollection<string>(ProfileCfgArrays.TacticalPingStrings);


        public void LoadData()
        {
            Profile.ServerCfg.MigrateLegacyLayout();

            var modlist = new List<ProfileMod>();
            foreach(var mod in Properties.Settings.Default.armaMods.ArmaMods)
            {
                ProfileMod existingMod = Profile.ProfileMods.Find(m => m.Id == mod.WorkshopId);
                if (existingMod == null)
                {
                    var newProfile = new ProfileMod { Name = mod.Name, Id = mod.WorkshopId, IsLocal = mod.IsLocal};
                    modlist.Add(newProfile);
                    continue;
                }
                else //refresh mods names
                { existingMod.Name = mod.Name; }
                modlist.Add(existingMod);
            }

            Profile.ProfileMods = modlist;

            LoadMissions();
            RefreshA3SRepositories();
        }

        public ObservableCollection<A3SRepository> A3SRepositories { get; } = new();

        public ObservableCollection<string> A3SEventNames { get; } = new();

        public bool A3SSyncBeforeLaunch
        {
            get => Properties.Settings.Default.a3sSyncBeforeLaunch;
            set
            {
                Properties.Settings.Default.a3sSyncBeforeLaunch = value;
                Properties.Settings.Default.Save();
            }
        }

        public void RefreshA3SRepositories()
        {
            A3SRepositories.Clear();
            var repos = Properties.Settings.Default.a3sRepositories?.Repositories;
            if (repos != null)
            {
                foreach (var repo in repos)
                    A3SRepositories.Add(repo);
            }

            // Nudge bindings so the attached repository ComboBox re-selects.
            Profile.RaisePropertyChanged(nameof(ServerProfile.A3SRepositoryId));
            _ = RefreshA3SEventsAsync();
        }

        public A3SRepository? SelectedA3SRepository
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Profile.A3SRepositoryId))
                    return null;
                return A3SRepositories.FirstOrDefault(r =>
                    string.Equals(r.Id, Profile.A3SRepositoryId, StringComparison.OrdinalIgnoreCase));
            }
            set
            {
                Profile.A3SRepositoryId = value?.Id ?? string.Empty;
                _ = RefreshA3SEventsAsync();
            }
        }

        public async Task RefreshA3SEventsAsync()
        {
            A3SEventNames.Clear();
            var repo = SelectedA3SRepository;
            if (repo == null)
                return;

            try
            {
                var events = await new A3SEventService().ListEventsAsync(repo).ConfigureAwait(true);
                foreach (var evt in events)
                    A3SEventNames.Add(evt.Name);
            }
            catch
            {
                // Operator can retry; keep attach fields editable.
            }
        }

        public async Task ApplyAttachedA3SEventAsync()
        {
            if (SelectedA3SRepository == null || string.IsNullOrWhiteSpace(Profile.A3SEventName))
            {
                DisplayMessage("Attach an A3S repository and event name first.");
                return;
            }

            try
            {
                var service = new A3SEventService();
                var events = await service.ListEventsAsync(SelectedA3SRepository).ConfigureAwait(true);
                var evt = events.FirstOrDefault(e =>
                    string.Equals(e.Name, Profile.A3SEventName, StringComparison.OrdinalIgnoreCase));
                if (evt == null)
                {
                    DisplayMessage($"Event \"{Profile.A3SEventName}\" not found in repository metadata.");
                    return;
                }

                LoadData();
                var result = service.ApplyEventToProfile(evt, Profile);
                service.AttachEventToProfile(Profile, SelectedA3SRepository, evt.Name);
                DisplayMessage(
                    $"Applied \"{result.EventName}\": matched {result.MatchedMods.Count}, missing {result.MissingAddons.Count}.");
            }
            catch (Exception ex)
            {
                DisplayMessage($"Apply A3S event failed: {ex.Message}");
            }
        }

        public async Task WriteA3SFavoriteServerAsync()
        {
            if (SelectedA3SRepository == null)
            {
                DisplayMessage("Attach an A3S repository first.");
                return;
            }

            try
            {
                var fav = await new A3SEventService()
                    .WriteFavoriteServerFromProfileAsync(SelectedA3SRepository, Profile)
                    .ConfigureAwait(true);
                DisplayMessage($"Wrote favorite \"{fav.Name}\" ({fav.IpAddress}:{fav.Port}) into autoconfig.");
            }
            catch (Exception ex)
            {
                DisplayMessage($"Write favorite server failed: {ex.Message}");
            }
        }

        public async Task SyncAttachedEventDeployLaunchAsync()
        {
            if (SelectedA3SRepository == null || string.IsNullOrWhiteSpace(Profile.A3SEventName))
            {
                DisplayMessage("Attach an A3S repository and event first.");
                return;
            }

            try
            {
                DisplayMessage($"Sync event → Deploy → Launch: {Profile.A3SEventName}…");
                Action? deploy = null;
                if (MainWindow.HasLoaded())
                    deploy = () => MainWindow.Instance.DeployViewModel.DeployAll();

                await new A3SEventService().SyncEventDeployLaunchAsync(
                        SelectedA3SRepository,
                        Profile,
                        Profile.A3SEventName,
                        deployAll: deploy,
                        launchAsync: async _ =>
                        {
                            SaveProfile();
                            await Task.Yield();
                            LaunchServerCore();
                        })
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                DisplayMessage($"Sync event → Deploy → Launch failed: {ex.Message}");
            }
        }

        public void UnloadData()
        {
            var index = Properties.Settings.Default.Profiles.FindIndex(p => p.Id == Profile.Id);  
            if (index != -1)
            { Properties.Settings.Default.Profiles[index] = Profile; }
        }

        internal void LoadMissions()
        {
            if (!Directory.Exists(Path.Combine(Profile.ArmaPath, "mpmissions"))) return;

            var missionList = new List<ProfileMission>();
            List<string> newMissions = new();

            //Load PBO files
            newMissions.AddRange(Directory.EnumerateFiles(Path.Combine(Profile.ArmaPath, "mpmissions"), "*.pbo",  searchOption: SearchOption.TopDirectoryOnly)
                                                    .Select(mission => mission.Replace(Path.Combine(Profile.ArmaPath, "mpmissions") + "\\", "")));
            //Load folders
            //Credits to Pucker and LinkIsParking
            newMissions.AddRange(Directory.GetDirectories(Path.Combine(Profile.ArmaPath, "mpmissions"))
                                                .Select(mission => mission.Replace(Path.Combine(Profile.ArmaPath, "mpmissions") + "\\", "")));


            foreach (var mission in newMissions)
            {
                ProfileMission existingMission = Profile.ServerCfg.Missions.Find(m => m.Path == mission);
                if (existingMission == null)
                {
                    var newMission = new ProfileMission { Name = mission.Replace(".pbo", ""), Path = mission };
                    missionList.Add(newMission);
                    continue;
                }
                missionList.Add(existingMission);
            }

            Profile.ServerCfg.Missions = missionList;
        }

        internal void ClearModOrder()
        {
            foreach (ProfileMod mod in Profile.ProfileMods)
            { mod.LoadPriority = null; }
        }

        internal void ModsCopyFrom(object to, string from)
        {
            foreach (var mod in Profile.ProfileMods)
            {
                switch (to.ToString())
                {
                    case "Server Only":
                        {
                            if (from == "Server + Client") mod.ServerSideChecked = mod.ClientSideChecked;
                            if (from == "HC") mod.ServerSideChecked = mod.HeadlessChecked;
                            if (from == "Opt") mod.ServerSideChecked = mod.OptChecked;
                            break;
                        }
                    case "Server + Client":
                        {
                            if (from == "Server Only") mod.ClientSideChecked = mod.ServerSideChecked;
                            if (from == "HC") mod.ClientSideChecked = mod.HeadlessChecked;
                            if (from == "Opt") mod.ClientSideChecked = mod.OptChecked;
                            break;
                        }
                    case "HC":
                        {
                            if (from == "Server Only") mod.HeadlessChecked = mod.ServerSideChecked;
                            if (from == "Server + Client") mod.HeadlessChecked = mod.ClientSideChecked;
                            if (from == "Opt") mod.HeadlessChecked = mod.OptChecked;
                            break;
                        }
                    case "Opt":
                        {
                            if (from == "Server Only") mod.OptChecked = mod.ServerSideChecked;
                            if (from == "Server + Client") mod.OptChecked = mod.ClientSideChecked;
                            if (from == "HC") mod.OptChecked = mod.HeadlessChecked;
                            break;
                        }
                }
            }
        }

        internal void ModsSelectAll(object to, bool select)
        {
            foreach (var mod in Profile.ProfileMods)
            {
                switch (to.ToString())
                {
                    case "Server Only":
                        mod.ServerSideChecked = select;
                        break;
                    case "Server + Client":
                        mod.ClientSideChecked = select;
                        break;
                    case "HC":
                        mod.HeadlessChecked = select;
                        break;
                    case "Opt":
                        mod.OptChecked = select;
                        break;
                }
            }
        }

        internal void MissionSelectAll(bool select)
        {
            foreach (var mission in Profile.ServerCfg.Missions)
            { mission.MissionChecked = select; }
        }
    }
}
