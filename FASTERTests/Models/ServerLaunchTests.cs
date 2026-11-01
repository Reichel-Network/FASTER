using System;
using System.IO;
using System.Linq;

using FASTER.Models;

using NUnit.Framework;

namespace FASTER.Models.Tests
{
    [TestFixture]
    public class ServerLaunchTests
    {
        [Test]
        public void ResolveExecutable_PrefersExistingX64PreferredPath()
        {
            var root = CreateTempServerRoot(withX64: true, withX86: true);
            try
            {
                var preferred = Path.Combine(root, "arma3server_x64.exe");
                var resolved = ServerLaunch.ResolveExecutable(preferred, root);
                Assert.That(resolved, Is.EqualTo(Path.GetFullPath(preferred)));
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ResolveExecutable_FallsBackToX64UnderServerPath_WhenPreferredMissing()
        {
            var root = CreateTempServerRoot(withX64: true, withX86: true);
            try
            {
                var missing = Path.Combine(root, "does-not-exist", "arma3server_x64.exe");
                var resolved = ServerLaunch.ResolveExecutable(missing, root);
                Assert.That(resolved, Is.EqualTo(Path.GetFullPath(Path.Combine(root, "arma3server_x64.exe"))));
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ResolveExecutable_FallsBackToX86_WhenX64Missing()
        {
            var root = CreateTempServerRoot(withX64: false, withX86: true);
            try
            {
                var resolved = ServerLaunch.ResolveExecutable(null, root);
                Assert.That(resolved, Is.EqualTo(Path.GetFullPath(Path.Combine(root, "arma3server.exe"))));
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ResolveExecutable_ReturnsNull_WhenNothingFound()
        {
            var root = Path.Combine(Path.GetTempPath(), "faster-launch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Assert.That(ServerLaunch.ResolveExecutable(null, root), Is.Null);
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void IsValidServerExecutable_AcceptsArma3ServerNamesOnly()
        {
            Assert.That(ServerLaunch.IsValidServerExecutable(@"C:\a\arma3server_x64.exe"), Is.True);
            Assert.That(ServerLaunch.IsValidServerExecutable(@"C:\a\arma3server.exe"), Is.True);
            Assert.That(ServerLaunch.IsValidServerExecutable(@"C:\a\arma3serverprofiling_x64.exe"), Is.True);
            Assert.That(ServerLaunch.IsValidServerExecutable(@"C:\a\arma3_x64.exe"), Is.False);
            Assert.That(ServerLaunch.IsValidServerExecutable(@"C:\a\server.exe"), Is.False);
            Assert.That(ServerLaunch.IsValidServerExecutable(null), Is.False);
        }

        [Test]
        public void CreateStartInfo_SetsWorkingDirectoryToArmaRoot()
        {
            var exe = @"E:\SteamLibrary\steamapps\common\Arma 3\arma3server_x64.exe";
            var args = @"-port=2302 ""-mod=@CBA_A3;""";
            var work = @"E:\SteamLibrary\steamapps\common\Arma 3";

            var psi = ServerLaunch.CreateStartInfo(exe, args, work);

            Assert.That(psi.FileName, Is.EqualTo(exe));
            Assert.That(psi.Arguments, Is.EqualTo(args));
            Assert.That(psi.WorkingDirectory, Is.EqualTo(work));
            Assert.That(psi.UseShellExecute, Is.False);
        }

        [Test]
        public void CreateStartInfo_DefaultsWorkingDirectoryFromExecutable()
        {
            var exe = @"D:\A3Server\arma3server_x64.exe";
            var psi = ServerLaunch.CreateStartInfo(exe, "-port=2302");
            Assert.That(psi.WorkingDirectory, Is.EqualTo(@"D:\A3Server"));
        }

        [Test]
        public void ValidateForLaunch_FailsWhenConfigMissing()
        {
            var root = CreateTempServerRoot(withX64: true, withX86: false);
            try
            {
                var profile = new ServerProfile("LaunchTest", createFolder: false)
                {
                    Executable = Path.Combine(root, "arma3server_x64.exe"),
                    Id = "_testhost"
                };

                var result = ServerLaunch.ValidateForLaunch(profile);
                Assert.That(result.Ok, Is.False);
                Assert.That(result.Message, Does.Contain("Save the profile"));
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void ValidateForLaunch_SucceedsWhenConfigsExist()
        {
            var root = CreateTempServerRoot(withX64: true, withX86: false);
            try
            {
                var profile = new ServerProfile("LaunchTest", createFolder: false)
                {
                    Executable = Path.Combine(root, "arma3server_x64.exe")
                };
                var dir = Path.Combine(root, "Servers", profile.Id);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "server_config.cfg"), "// cfg");
                File.WriteAllText(Path.Combine(dir, "server_basic.cfg"), "// basic");

                var result = ServerLaunch.ValidateForLaunch(profile);
                Assert.That(result.Ok, Is.True);
            }
            finally { TryDelete(root); }
        }

        [Test]
        public void CommandLine_IncludesCheckedClientAndServerMods()
        {
            var profile = new ServerProfile("Mods", createFolder: false)
            {
                Executable = @"E:\A3\arma3server_x64.exe",
                ProfileMods =
                {
                    new ProfileMod { Name = "CBA_A3", Id = 1, ClientSideChecked = true, LoadPriority = 1 },
                    new ProfileMod { Name = "ACE", Id = 2, ClientSideChecked = true, LoadPriority = 2 },
                    new ProfileMod { Name = "ServerOnly", Id = 3, ServerSideChecked = true, LoadPriority = 1 },
                    new ProfileMod { Name = "Unchecked", Id = 4 }
                }
            };

            var cmd = profile.CommandLine;
            Assert.That(cmd, Does.Contain("-mod="));
            Assert.That(cmd, Does.Contain("@CBA_A3"));
            Assert.That(cmd, Does.Contain("@ACE"));
            Assert.That(cmd, Does.Contain("-serverMod="));
            Assert.That(cmd, Does.Contain("@ServerOnly"));
            Assert.That(cmd, Does.Not.Contain("@Unchecked"));
        }

        [Test]
        public void CountSelectedLaunchMods_CountsClientServerHc()
        {
            var profile = new ServerProfile("Count", createFolder: false)
            {
                ProfileMods =
                {
                    new ProfileMod { Name = "A", ClientSideChecked = true },
                    new ProfileMod { Name = "B", ServerSideChecked = true },
                    new ProfileMod { Name = "C", HeadlessChecked = true },
                    new ProfileMod { Name = "D", OptChecked = true },
                    new ProfileMod { Name = "E" }
                }
            };

            Assert.That(ServerLaunch.CountSelectedLaunchMods(profile), Is.EqualTo(3));
        }

        [Test]
        public void MissingDeployedMods_ReportsUncheckedSymlinks()
        {
            var root = CreateTempServerRoot(withX64: true, withX86: false);
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "@CBA_A3"), root);

                var profile = new ServerProfile("Deploy", createFolder: false)
                {
                    Executable = Path.Combine(root, "arma3server_x64.exe"),
                    ProfileMods =
                    {
                        new ProfileMod { Name = "CBA_A3", ClientSideChecked = true },
                        new ProfileMod { Name = "ACE", ClientSideChecked = true }
                    }
                };

                var missing = ServerLaunch.MissingDeployedMods(profile);
                Assert.That(missing.Any(m => m.Equals("@ACE", StringComparison.OrdinalIgnoreCase)), Is.True);
                Assert.That(missing.Any(m => m.Equals("@CBA_A3", StringComparison.OrdinalIgnoreCase)), Is.False);
            }
            catch (IOException)
            {
                Assert.Ignore("Symlinks require admin or Developer Mode on this host.");
            }
            finally { TryDelete(root); }
        }

        private static string CreateTempServerRoot(bool withX64, bool withX86)
        {
            var root = Path.Combine(Path.GetTempPath(), "faster-launch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            if (withX64)
                File.WriteAllBytes(Path.Combine(root, "arma3server_x64.exe"), new byte[] { 0x4D, 0x5A });
            if (withX86)
                File.WriteAllBytes(Path.Combine(root, "arma3server.exe"), new byte[] { 0x4D, 0x5A });
            return root;
        }

        private static void TryDelete(string root)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
