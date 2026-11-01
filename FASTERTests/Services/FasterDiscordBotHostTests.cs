using System.Linq;

using FASTER.Services.A3S;

using NUnit.Framework;

namespace FASTER.Services.Tests
{
    [TestFixture]
    public class FasterDiscordBotHostTests
    {
        [Test]
        public void ParseIdSet_SplitsCommaSeparatedIds()
        {
            var set = FasterDiscordBotHost.ParseIdSet(" 1, 2,nope, 3 ");
            Assert.That(set.OrderBy(x => x).ToArray(), Is.EqualTo(new ulong[] { 1, 2, 3 }));
        }

        [Test]
        public void ParseIdSet_Empty_ReturnsEmpty()
        {
            Assert.That(FasterDiscordBotHost.ParseIdSet(null), Is.Empty);
            Assert.That(FasterDiscordBotHost.ParseIdSet("  "), Is.Empty);
        }

        [Test]
        public void BuildCommands_RegistersStartStopAndDeploySlashCommands()
        {
            var cmds = FasterDiscordBotHost.BuildCommands();
            Assert.That(cmds.Select(c => c.Name.GetValueOrDefault()).ToArray(),
                Is.EquivalentTo(new[]
                {
                    "faster-status",
                    "faster-events",
                    "faster-deploy",
                    "faster-start",
                    "faster-stop"
                }));
        }
    }
}
