using FASTER.Models;

using NUnit.Framework;

namespace FASTER.Services.Tests
{
    [TestFixture]
    public class ArmaModWorkshopUpdateTests
    {
        [Test]
        public void IsWorkshopDownloadNeeded_LocalMod_Never()
        {
            Assert.That(
                ArmaMod.IsWorkshopDownloadNeeded(true, ArmaModStatus.Local, 100, 0, true),
                Is.False);
        }

        [Test]
        public void IsWorkshopDownloadNeeded_MissingStaging_Yes()
        {
            Assert.That(
                ArmaMod.IsWorkshopDownloadNeeded(false, ArmaModStatus.UpToDate, 100, 100, false),
                Is.True);
        }

        [Test]
        public void IsWorkshopDownloadNeeded_NotComplete_Yes()
        {
            Assert.That(
                ArmaMod.IsWorkshopDownloadNeeded(false, ArmaModStatus.NotComplete, 100, 100, true),
                Is.True);
        }

        [Test]
        public void IsWorkshopDownloadNeeded_SteamRevisionAhead_Yes()
        {
            Assert.That(
                ArmaMod.IsWorkshopDownloadNeeded(false, ArmaModStatus.UpToDate, steamLastUpdated: 200, localLastUpdated: 100, hasStagingContent: true),
                Is.True);
        }

        [Test]
        public void IsWorkshopDownloadNeeded_ExactSteamRevision_No()
        {
            Assert.That(
                ArmaMod.IsWorkshopDownloadNeeded(false, ArmaModStatus.UpToDate, steamLastUpdated: 100, localLastUpdated: 100, hasStagingContent: true),
                Is.False);
        }

        [Test]
        public void IsWorkshopDownloadNeeded_LegacyWallClockLocal_Yes()
        {
            // Old builds stored UtcNow as LocalLastUpdated, which sits above Steam time_updated
            // and hid Workshop bumps. Inequality forces one refresh/migration.
            Assert.That(
                ArmaMod.IsWorkshopDownloadNeeded(false, ArmaModStatus.UpToDate, steamLastUpdated: 1_700_000_000, localLastUpdated: 1_700_000_500, hasStagingContent: true),
                Is.True);
        }

        [Test]
        public void IsWorkshopDownloadNeeded_UnknownSteamTime_NoForce()
        {
            Assert.That(
                ArmaMod.IsWorkshopDownloadNeeded(false, ArmaModStatus.UpToDate, steamLastUpdated: 0, localLastUpdated: 99, hasStagingContent: true),
                Is.False);
        }
    }
}
