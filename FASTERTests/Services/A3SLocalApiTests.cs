using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using FASTER.Services.A3S;

using NUnit.Framework;

namespace FASTER.Services.Tests
{
    [TestFixture]
    public class A3SLocalApiTests
    {
        [Test]
        public void NormalizeAutoconfigUrl_AcceptsSyncRootAndAutoconfig()
        {
            Assert.That(
                A3SUrlHelpers.NormalizeAutoconfigUrl("https://arma.game.reichel.network/.a3s/autoconfig"),
                Is.EqualTo("https://arma.game.reichel.network/.a3s/autoconfig"));
            Assert.That(
                A3SUrlHelpers.NormalizeAutoconfigUrl("https://arma.game.reichel.network/.a3s/sync"),
                Is.EqualTo("https://arma.game.reichel.network/.a3s/autoconfig"));
            Assert.That(
                A3SUrlHelpers.NormalizeAutoconfigUrl("https://arma.game.reichel.network/.a3s"),
                Is.EqualTo("https://arma.game.reichel.network/.a3s/autoconfig"));
            Assert.That(
                A3SUrlHelpers.NormalizeAutoconfigUrl("https://arma.game.reichel.network"),
                Is.EqualTo("https://arma.game.reichel.network/.a3s/autoconfig"));
        }

        [Test]
        public void ValidateRequest_RequiresEventAndRepoOrUrl()
        {
            Assert.Throws<ArgumentException>(() =>
                A3SRemoteDeployService.ValidateRequest(new A3SRemoteDeployRequest()));

            Assert.Throws<ArgumentException>(() =>
                A3SRemoteDeployService.ValidateRequest(new A3SRemoteDeployRequest { Event = "Arma Domina" }));

            Assert.DoesNotThrow(() =>
                A3SRemoteDeployService.ValidateRequest(new A3SRemoteDeployRequest
                {
                    Event = "Arma Domina",
                    Repo = "reichel"
                }));

            Assert.DoesNotThrow(() =>
                A3SRemoteDeployService.ValidateRequest(new A3SRemoteDeployRequest
                {
                    Event = "Arma Domina",
                    Url = "https://arma.game.reichel.network/.a3s/sync"
                }));
        }

        [Test]
        public async Task LocalApiHost_StatusRequiresBearer_AndDeployValidatesBody()
        {
            var port = 18787 + (Environment.TickCount % 1000);
            var token = "test-token-" + Guid.NewGuid().ToString("N")[..8];
            var deploy = new A3SRemoteDeployService();
            using var host = new FasterLocalApiHost(deploy, token, port);
            host.Start();

            try
            {
                using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

                var unauthorized = await client.GetAsync("v1/status");
                Assert.That(unauthorized.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var statusRes = await client.GetAsync("v1/status");
                Assert.That(statusRes.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                var statusJson = await statusRes.Content.ReadAsStringAsync();
                Assert.That(statusJson, Does.Contain("\"apiListening\":true").IgnoreCase
                    .Or.Contain("\"ApiListening\":true"));

                var badDeploy = await client.PostAsync(
                    "v1/deploy",
                    new StringContent("{\"event\":\"Arma Domina\"}", Encoding.UTF8, "application/json"));
                Assert.That(badDeploy.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                var err = await badDeploy.Content.ReadAsStringAsync();
                Assert.That(err, Does.Contain("repo").IgnoreCase.Or.Contain("autoconfigUrl").IgnoreCase);
            }
            finally
            {
                host.Stop();
            }
        }

        [Test]
        public void DeployRequest_JsonRoundTripsModeAAndModeB()
        {
            var a = JsonSerializer.Deserialize<A3SRemoteDeployRequest>(
                """{"event":"Arma Domina","repo":"reichel","profile":"test","launch":true}""");
            Assert.That(a!.Event, Is.EqualTo("Arma Domina"));
            Assert.That(a.Repo, Is.EqualTo("reichel"));
            Assert.That(a.Profile, Is.EqualTo("test"));

            var b = JsonSerializer.Deserialize<A3SRemoteDeployRequest>(
                """{"event":"Arma Domina","url":"https://arma.game.reichel.network/.a3s/sync"}""");
            Assert.That(b!.ResolvedAutoconfigUrl, Does.Contain("arma.game.reichel.network"));
        }
    }
}
