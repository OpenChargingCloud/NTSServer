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

using System.Net;
using System.Security.Cryptography;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.NTSServer.Tests
{

    /// <summary>
    /// What every node has to answer, asked of an NTS server - the conformance
    /// suite of WWCP_Node_TestKit.
    /// </summary>
    /// <remarks>
    /// The time server listens on two ports of its own beside the node's, and
    /// on the loopback address only: given ports nobody has, so that the
    /// suite's nodes do not fight over 4460 and 123.
    /// </remarks>
    public class NTSServerConformance : NodeConformanceTests
    {

        protected override WWCPNode NewNode(String   Directory,
                                            JObject  Configuration)
            => Make(Directory, Configuration, null);

        protected override WWCPNode? NewNodeWritingLogFiles(String   Directory,
                                                            JObject  Configuration,
                                                            String   LogPath)
            => Make(Directory, Configuration, LogPath);

        private static NTSServerNode Make(String   Directory,
                                          JObject  Configuration,
                                          String?  LogPath)
        {

            if (Configuration[TimeServerSettings.SectionName] is not JObject)
                Configuration[TimeServerSettings.SectionName] = new JObject(
                                                                    new JProperty("listen",     "127.0.0.1"),
                                                                    new JProperty("ntsKEPort",  TestPorts.Free()),
                                                                    new JProperty("ntpPort",    UDPPorts.Free())
                                                                );

            var file = Path.Combine(Directory, WWCPConfigFile.DefaultFileName);
            File.WriteAllText(file, Configuration.ToString());

            return new NTSServerNode(
                       HTTPPort:          IPPort.Parse(TestPorts.Free()),
                       AccountsPath:      Path.Combine(Directory, "accounts"),
                       ConfigFile:        new WWCPConfigFile(file),
                       CertificatesPath:  Path.Combine(Directory, "certificates"),
                       LogToConsole:      false,
                       LogPath:           LogPath,
                       BridgeDebugLog:    false
                   );

        }


        #region TheTimeServersSettingsAreChangedOverTheJSONAPI()

        /// <summary>
        /// The time server's own settings, changed by whoever may edit it:
        /// answered with what changed - and what is no setting refused.
        /// </summary>
        [Test]
        public async Task TheTimeServersSettingsAreChangedOverTheJSONAPI()
        {

            using var http          = await SignedIn();

            var (status, answer)    = await Send(http, HttpMethod.Put, "api/v1/ntsServer/settings",
                                                 new JObject(new JProperty("local", new JObject(new JProperty("stratum", 2)))));

            Assert.That(status,                                                   Is.EqualTo(HttpStatusCode.OK), answer.ToString());
            Assert.That(answer["changed"]!.Values<String>(),                      Is.EquivalentTo(new[] { "local.stratum" }));
            Assert.That(answer["settings"]!["local"]!["stratum"]!.Value<Int32>(), Is.EqualTo(2));
            Assert.That(((NTSServerNode) Node).Host.Description!.Stratum,         Is.EqualTo(2));

            var settings            = await GetJSON(http, "api/v1/ntsServer/settings");

            Assert.That(settings["local"]!["stratum"]!.Value<Int32>(),            Is.EqualTo(2));

            var (refused, why)      = await Send(http, HttpMethod.Put, "api/v1/ntsServer/settings",
                                                 new JObject(new JProperty("ntpPort", 0)));

            Assert.That(refused,                                                  Is.EqualTo(HttpStatusCode.BadRequest), why.ToString());

        }

        #endregion

        #region AnOperatorMayReadTheTimeServersSettingsButNotChangeThem()

        [Test]
        public async Task AnOperatorMayReadTheTimeServersSettingsButNotChangeThem()
        {

            var password            = "An-Operator-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

            await AccountIn("operator1", NTSServerAccess.Operator.Name, password);

            using var http          = await SignedInAs("operator1", password);

            var settings            = await GetJSON(http, "api/v1/ntsServer/settings");

            Assert.That(settings["local"]!["stratum"]!.Value<Int32>(),  Is.EqualTo(1));

            var (status, answer)    = await Send(http, HttpMethod.Put, "api/v1/ntsServer/settings",
                                                 new JObject(new JProperty("local", new JObject(new JProperty("stratum", 2)))));

            Assert.That(status,                                         Is.EqualTo(HttpStatusCode.Forbidden), answer.ToString());
            Assert.That(((NTSServerNode) Node).Settings.Local.Stratum,  Is.EqualTo(1));

        }

        #endregion

    }

}
