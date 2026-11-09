/*
 * Copyright (c) 2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of NTSServer <https://github.com/OpenChargingCloud/NTSServer>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.SSH;

#endregion

namespace cloud.charging.open.NTSServer.Tests
{

    /// <summary>
    /// The "ntsServer" section of the configuration file: what it says, and
    /// what it is refused for.
    /// </summary>
    [TestFixture]
    public class SettingsTests
    {

        [Test]
        public void NoSectionIsTheDefaults()
        {

            Assert.That(TimeServerSettings.TryParse(null, out var settings, out var error), Is.True, error);

            Assert.That(settings!.TimeSource,          Is.EqualTo(TimeSource.Local));
            Assert.That(settings.NTSKEPort.ToUInt16(), Is.EqualTo(4460));
            Assert.That(settings.NTPPort.  ToUInt16(), Is.EqualTo(123));
            Assert.That(settings.SSHEnabled,           Is.True);
            Assert.That(settings.SSHClients,           Is.Empty);
            Assert.That(settings.Upstream.Group,       Is.EqualTo("legal"));
            Assert.That(settings.Upstream.Poll,        Is.EqualTo(TimeSpan.FromSeconds(64)));

        }

        [Test]
        public void EverythingIsRead()
        {

            var key = SshPublicKey.FromHostKey(SshHostKey.GenerateEd25519(), "cs001").ToAuthorizedKeyLine();

            var json = JObject.Parse($$"""
                {
                  "listen":     "127.0.0.1",
                  "ntsKEPort":  14460,
                  "ntpPort":    11123,
                  "hostname":   "time.lan",
                  "ntpServer":  "ntp.lan:1123",
                  "timeSource": "upstream",
                  "local":      { "stratum": 3, "referenceId": "GPS", "rootDispersionMs": 2.5 },
                  "upstream":   { "group": "ptb", "pollSeconds": 128, "stepThresholdMs": 50, "maxSlewPPM": 100, "holdoverMinutes": 30 },
                  "rateLimit":  true,
                  "ssh":        { "enabled": false, "clients": [ { "name": "cs001", "keys": [ "{{key}}" ] } ] }
                }
                """);

            Assert.That(TimeServerSettings.TryParse(json, out var settings, out var error), Is.True, error);

            Assert.That(settings!.Listen.ToString(),     Is.EqualTo("127.0.0.1"));
            Assert.That(settings.NTSKEPort.ToUInt16(),   Is.EqualTo(14460));
            Assert.That(settings.NTPPort.  ToUInt16(),   Is.EqualTo(11123));
            Assert.That(settings.Hostname,               Is.EqualTo("time.lan"));
            Assert.That(settings.NTPServer,              Is.EqualTo("ntp.lan:1123"));
            Assert.That(settings.TimeSource,             Is.EqualTo(TimeSource.Upstream));
            Assert.That(settings.Local.Stratum,          Is.EqualTo(3));
            Assert.That(settings.Local.ReferenceId,      Is.EqualTo("GPS"));
            Assert.That(settings.Local.RootDispersion,   Is.EqualTo(TimeSpan.FromMilliseconds(2.5)));
            Assert.That(settings.Upstream.Group,         Is.EqualTo("ptb"));
            Assert.That(settings.Upstream.Poll,          Is.EqualTo(TimeSpan.FromSeconds(128)));
            Assert.That(settings.Upstream.Step,          Is.EqualTo(TimeSpan.FromMilliseconds(50)));
            Assert.That(settings.Upstream.Slew,          Is.EqualTo(100e-6).Within(1e-12));
            Assert.That(settings.Upstream.HoldoverTime,  Is.EqualTo(TimeSpan.FromMinutes(30)));
            Assert.That(settings.RateLimit,              Is.True);
            Assert.That(settings.SSHEnabled,             Is.False);
            Assert.That(settings.SSHClients.Single().Name, Is.EqualTo("cs001"));

            // And back: what is written is read as the same.
            Assert.That(TimeServerSettings.TryParse(settings.ToJSON(), out var again, out error), Is.True, error);
            Assert.That(again!.ToJSON().ToString(), Is.EqualTo(settings.ToJSON().ToString()));

        }

        [TestCase("""{ "timeSourc": "upstream" }""",                 "nobody knows")]
        [TestCase("""{ "timeSource": "gps" }""",                     "\"local\" or \"upstream\"")]
        [TestCase("""{ "ntpPort": 0 }""",                            "a port")]
        [TestCase("""{ "ntsKEPort": "4460" }""",                     "a port")]
        [TestCase("""{ "listen": "nowhere" }""",                     "no address")]
        [TestCase("""{ "local": { "stratum": 16 } }""",              "1 to 15")]
        [TestCase("""{ "local": { "referenceId": "TOOLONG" } }""",   "four")]
        [TestCase("""{ "local": { "rootDispersionMs": 0 } }""",      "never 0")]
        [TestCase("""{ "upstream": { "pollSeconds": 1 } }""",        "pollSeconds")]
        [TestCase("""{ "upstream": { "maxSlewPPM": 1000 } }""",      "maxSlewPPM")]
        [TestCase("""{ "ntpServer": "a:b:c:d" }""",                  "ntpServer")]
        [TestCase("""{ "ssh": { "clients": [ { "name": ".x", "keys": [] } ] } }""",                     "can not name")]
        [TestCase("""{ "ssh": { "clients": [ { "name": "cs001", "keys": [ "no key at all" ] } ] } }""", "authorized_keys")]
        public void WhatCannotBeFollowedIsRefused(String JSON, String Says)
        {

            Assert.That(TimeServerSettings.TryParse(JObject.Parse(JSON), out _, out var error), Is.False);
            Assert.That(error, Does.Contain(Says));

        }

        [Test]
        public void TheSameClientTwiceIsRefused()
        {

            var key  = SshPublicKey.FromHostKey(SshHostKey.GenerateEd25519()).ToAuthorizedKeyLine();
            var json = new JObject(new JProperty("ssh", new JObject(new JProperty("clients", new JArray(
                           new JObject(new JProperty("name", "cs001"), new JProperty("keys", new JArray(key))),
                           new JObject(new JProperty("name", "CS001"), new JProperty("keys", new JArray(key)))
                       )))));

            Assert.That(TimeServerSettings.TryParse(json, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("twice"));

        }

        [TestCase(null,            123,   null,         null)]
        [TestCase("time.lan",      123,   null,         null)]
        [TestCase("time.lan",      1123,  null,         "udp://time.lan:1123")]
        [TestCase(null,            1123,  "ntp.lan",    "udp://ntp.lan:1123")]
        [TestCase("time.lan",      123,   "ntp.lan:99", "udp://ntp.lan:99")]
        public void TheKeyExchangeNamesNTPWhereAClientCouldNotGuessIt(String?  Hostname,
                                                                      Int32    NTPPort,
                                                                      String?  NTPServer,
                                                                      String?  Named)
        {

            var settings = TimeServerSettings.Default with {
                               Hostname   = Hostname,
                               NTPPort    = IPPort.Parse(NTPPort),
                               NTPServer  = NTPServer
                           };

            Assert.That(TimeServerHost.AdvertisedNTPOf(settings)?.ToString().TrimEnd('/'), Is.EqualTo(Named));

        }

    }

}
