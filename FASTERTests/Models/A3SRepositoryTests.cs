using System;
using System.IO;
using System.Xml.Serialization;

using NUnit.Framework;

namespace FASTER.Models.Tests
{
    [TestFixture]
    public class A3SRepositoryTests
    {
        [Test]
        public void A3SRepository_Defaults_AreSafe()
        {
            var repo = new A3SRepository();
            Assert.That(repo.Id, Is.Not.Null.And.Not.Empty);
            Assert.That(repo.Name, Is.EqualTo(string.Empty));
            Assert.That(repo.AutoconfigUrl, Is.EqualTo(string.Empty));
            Assert.That(repo.LocalPath, Is.EqualTo(string.Empty));
            Assert.That(repo.LastRevision, Is.EqualTo(0));
            Assert.That(repo.LastCheckUtc, Is.Null);
        }

        [Test]
        public void A3SRepository_NameAndUrlConstructor_SetsFields()
        {
            var repo = new A3SRepository("Community", "https://example.test/repo/.a3s/autoconfig");
            Assert.That(repo.Name, Is.EqualTo("Community"));
            Assert.That(repo.AutoconfigUrl, Is.EqualTo("https://example.test/repo/.a3s/autoconfig"));
        }

        [Test]
        public void A3SRepositoryCollection_XmlRoundTrip_PreservesEntries()
        {
            var original = new A3SRepositoryCollection();
            original.Repositories.Add(new A3SRepository("Alpha", "https://alpha.example/.a3s/autoconfig")
            {
                LocalPath = @"C:\Mods\Alpha",
                LastRevision = 42,
                LastCheckUtc = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc),
                ProtocolHint = "HTTP"
            });
            original.Repositories.Add(new A3SRepository("Beta", "ftp://beta.example/repo/.a3s/autoconfig"));

            var serializer = new XmlSerializer(typeof(A3SRepositoryCollection));
            string xml;
            using (var writer = new StringWriter())
            {
                serializer.Serialize(writer, original);
                xml = writer.ToString();
            }

            A3SRepositoryCollection restored;
            using (var reader = new StringReader(xml))
            {
                restored = (A3SRepositoryCollection)serializer.Deserialize(reader)!;
            }

            Assert.That(restored.Repositories, Has.Count.EqualTo(2));
            Assert.That(restored.Repositories[0].Name, Is.EqualTo("Alpha"));
            Assert.That(restored.Repositories[0].AutoconfigUrl, Is.EqualTo("https://alpha.example/.a3s/autoconfig"));
            Assert.That(restored.Repositories[0].LocalPath, Is.EqualTo(@"C:\Mods\Alpha"));
            Assert.That(restored.Repositories[0].LastRevision, Is.EqualTo(42));
            Assert.That(restored.Repositories[0].ProtocolHint, Is.EqualTo("HTTP"));
            Assert.That(restored.Repositories[1].Name, Is.EqualTo("Beta"));
            Assert.That(restored.Repositories[0].Id, Is.EqualTo(original.Repositories[0].Id));
        }

        [Test]
        public void A3SEventRef_Stub_StoresNameAndProfileId()
        {
            var evt = new A3SEventRef("WeekendOps", "profile-123");
            Assert.That(evt.Name, Is.EqualTo("WeekendOps"));
            Assert.That(evt.ProfileId, Is.EqualTo("profile-123"));
        }

        /// <summary>
        /// Live bridge smoke against public host — needs companion JAR + JDK 25.
        /// Unit coverage of bridge-shaped JSON lives in <c>A3SPhase1Tests</c>.
        /// </summary>
        [Test]
        [Category("Integration")]
        [Ignore("Requires a3s-metadata-bridge JAR + JDK 25; public host smoke is manual / companion CI")]
        public void MetadataStore_AutoconfigFixture_RoundTrip()
        {
            Assert.Fail("Live bridge smoke is gated on A3S_METADATA_BRIDGE_JAR.");
        }
    }
}
