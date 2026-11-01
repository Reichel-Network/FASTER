using FASTER.Models;

using NUnit.Framework;

namespace FASTER.Models.Tests
{
    [TestFixture]
    public class ServerLaunchStopTests
    {
        [Test]
        public void CommandLineMatchesProfile_ByServersFolderAndPort()
        {
            var profile = new ServerProfile("Test", false) { Port = 2402, Id = "abc123" };

            Assert.That(
                ServerLaunch.CommandLineMatchesProfile(
                    @"C:\arma\arma3server_x64.exe ""-profiles=C:\arma\Servers\abc123"" -port=2402",
                    profile),
                Is.True);
            Assert.That(
                ServerLaunch.CommandLineMatchesProfile(
                    @"C:\arma\arma3server_x64.exe ""-profiles=C:\arma\Servers\abc123_hc1"" -port=2402",
                    profile),
                Is.True);
            Assert.That(
                ServerLaunch.CommandLineMatchesProfile(
                    @"C:\arma\arma3server_x64.exe -port=2402",
                    profile),
                Is.True);
            Assert.That(
                ServerLaunch.CommandLineMatchesProfile(
                    @"C:\arma\arma3server_x64.exe ""-profiles=C:\arma\Servers\other"" -port=2302",
                    profile),
                Is.False);
        }
    }
}
