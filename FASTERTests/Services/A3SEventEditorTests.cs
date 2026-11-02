using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using FASTER.Models;
using FASTER.Services.A3S;

using NUnit.Framework;

namespace FASTER.Services.Tests
{
    [TestFixture]
    public class A3SEventEditorTests
    {
        [Test]
        public async Task UpsertRenameDelete_RoundTripsClassicEventsFile()
        {
            var root = Path.Combine(Path.GetTempPath(), "faster-a3s-evt-edit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "@CBA_A3", "addons"));
            Directory.CreateDirectory(Path.Combine(root, "@ACE", "addons"));
            await File.WriteAllBytesAsync(Path.Combine(root, "@CBA_A3", "addons", "cba.pbo"), Encoding.UTF8.GetBytes("cba"));
            await File.WriteAllBytesAsync(Path.Combine(root, "@ACE", "addons", "ace.pbo"), Encoding.UTF8.GetBytes("ace"));

            var store = new SerializerA3SMetadataStore();
            var service = new A3SEventService(store, new A3SRepositoryService());
            var repo = new A3SRepository("edit-repo", "https://example.test/repo/.a3s/autoconfig")
            {
                PublishRoot = root,
                LocalPath = root
            };

            try
            {
                var known = await service.ListKnownAddonNamesAsync(repo);
                Assert.That(known, Does.Contain("@CBA_A3"));
                Assert.That(known, Does.Contain("@ACE"));

                var created = await service.UpsertEventAsync(
                    repo,
                    "Weekend",
                    "Main night",
                    new Dictionary<string, bool>
                    {
                        ["@CBA_A3"] = false,
                        ["@ACE"] = true
                    });

                Assert.That(created.Name, Is.EqualTo("Weekend"));
                Assert.That(File.Exists(Path.Combine(root, ".a3s", "events")), Is.True);

                var listed = await service.ListEventsAsync(repo);
                Assert.That(listed.Count, Is.EqualTo(1));
                Assert.That(listed[0].SelectedAddonNames, Is.EquivalentTo(new[] { "@CBA_A3", "@ACE" }));
                Assert.That(listed[0].OptionalAddonNames, Is.EquivalentTo(new[] { "@ACE" }));

                var renamed = await service.UpsertEventAsync(
                    repo,
                    "Weekend Ops",
                    "Renamed",
                    new Dictionary<string, bool> { ["@CBA_A3"] = false },
                    originalName: "Weekend");

                Assert.That(renamed.Name, Is.EqualTo("Weekend Ops"));
                listed = await service.ListEventsAsync(repo);
                Assert.That(listed.Select(e => e.Name), Is.EquivalentTo(new[] { "Weekend Ops" }));
                Assert.That(listed[0].AddonNames.Keys, Is.EquivalentTo(new[] { "@CBA_A3" }));

                Assert.That(await service.DeleteEventAsync(repo, "Weekend Ops"), Is.True);
                listed = await service.ListEventsAsync(repo);
                Assert.That(listed, Is.Empty);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }

        [Test]
        public async Task Build_PreservesExistingEventsInWriteSet()
        {
            var root = Path.Combine(Path.GetTempPath(), "faster-a3s-evt-build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "@alpha", "addons"));
            await File.WriteAllBytesAsync(Path.Combine(root, "@alpha", "addons", "a.pbo"), Encoding.UTF8.GetBytes("a1"));

            var store = new SerializerA3SMetadataStore();
            var service = new A3SEventService(store, new A3SRepositoryService());
            var repo = new A3SRepository("pub", "https://example.test/repo/.a3s/autoconfig")
            {
                PublishRoot = root,
                LocalPath = root,
                NumberOfConnections = 2
            };

            try
            {
                await service.UpsertEventAsync(
                    repo,
                    "Training",
                    "Keep me",
                    new Dictionary<string, bool> { ["@alpha"] = false });

                var build = new A3SBuildService(store);
                var result = await build.BuildAsync(repo);

                Assert.That(result.Revision, Is.EqualTo(1));
                var events = await store.ReadEventsAsync(root);
                Assert.That(events.List.Count, Is.EqualTo(1));
                Assert.That(events.List[0].Name, Is.EqualTo("Training"));
                Assert.That(events.List[0].AddonNames.ContainsKey("@alpha"), Is.True);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }

        [Test]
        public async Task Build_WritesEmptyEventsWhenNoneExist()
        {
            var root = Path.Combine(Path.GetTempPath(), "faster-a3s-evt-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "@beta", "addons"));
            await File.WriteAllBytesAsync(Path.Combine(root, "@beta", "addons", "b.pbo"), Encoding.UTF8.GetBytes("b1"));

            var store = new JsonA3SMetadataStore(fixedEnvelopePath: null, writeRoot: root);
            var repo = new A3SRepository("pub", "https://example.test/repo/.a3s/autoconfig")
            {
                PublishRoot = root,
                LocalPath = root
            };

            try
            {
                var build = new A3SBuildService(store);
                await build.BuildAsync(repo);

                Assert.That(store.LastWriteSet, Is.Not.Null);
                Assert.That(store.LastWriteSet!.Events, Is.Not.Null);
                Assert.That(store.LastWriteSet.Events!.List, Is.Empty);
                Assert.That(File.Exists(Path.Combine(root, ".a3s", "events")), Is.True);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* ignore */ }
            }
        }

        [Test]
        public void NormalizeAddonEventKey_UsesFolderName()
        {
            Assert.That(A3SEventService.NormalizeAddonEventKey(@"mods\@CBA_A3"), Is.EqualTo("@CBA_A3"));
            Assert.That(A3SEventService.NormalizeAddonEventKey("450814997"), Is.EqualTo("450814997"));
            Assert.That(A3SEventService.NormalizeAddonEventKey("  "), Is.EqualTo(string.Empty));
        }
    }
}
