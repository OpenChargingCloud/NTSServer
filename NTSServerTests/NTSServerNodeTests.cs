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

using org.GraphDefined.Vanaheimr.Illias;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.NTSServer.Tests
{

    /// <summary>
    /// An NTS server, started on ports nobody else has, and asked by Norn's
    /// own NTS client: over TLS, over SSH, and as the upstream server of
    /// another NTS server.
    /// </summary>
    /// <remarks>
    /// Nothing here leaves this machine: every server asked is one started
    /// here, on the loopback address.
    /// </remarks>
    [TestFixture]
    [NonParallelizable]
    public partial class NTSServerNodeTests
    {

        #region Data

        private readonly List<NTSServerNode>  nodes        = [];
        private readonly List<String>         directories  = [];

        #endregion

        #region SetUp / TearDown

        [TearDown]
        public async Task TearDown()
        {

            foreach (var node in nodes)
            {
                try { await node.DisposeAsync(); } catch { }
            }

            nodes.Clear();

            foreach (var directory in directories)
            {
                try { Directory.Delete(directory, true); } catch { }
            }

            directories.Clear();

        }

        #endregion


        #region (private) Started(Section = null, Configuration = null)

        /// <summary>
        /// An NTS server on the loopback address and on ports nobody has, in a
        /// directory of its own - started.
        /// </summary>
        private async Task<NTSServerNode> Started(JObject?  Section        = null,
                                                  JObject?  Configuration  = null,
                                                  Action<NTSServerNode>? BeforeStart = null,
                                                  ServedClock? Clock       = null,
                                                  Func<TimeServerSettings, TimeServerSettings>? CommandLine = null)
        {

            var directory = Path.Combine(Path.GetTempPath(), "NTSServerTests-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(directory);
            directories.Add(directory);

            var section   = new JObject(
                                new JProperty("listen",     "127.0.0.1"),
                                new JProperty("ntsKEPort",  TestPorts.Free()),
                                new JProperty("ntpPort",    UDPPorts.Free()),
                                new JProperty("hostname",   "localhost")
                            );

            if (Section is not null)
                section.Merge(Section);

            var configuration = Configuration ?? new JObject();
            configuration[TimeServerSettings.SectionName] = section;

            var file = Path.Combine(directory, WWCPConfigFile.DefaultFileName);
            File.WriteAllText(file, configuration.ToString());

            var node = new NTSServerNode(
                           HTTPPort:          IPPort.Parse(TestPorts.Free()),
                           AccountsPath:      Path.Combine(directory, "accounts"),
                           ConfigFile:        new WWCPConfigFile(file),
                           CertificatesPath:  Path.Combine(directory, "certificates"),
                           LogToConsole:      false,
                           BridgeDebugLog:    false,
                           SSH:               new SSHSettings(Enabled: true, Port: IPPort.Parse(TestPorts.Free())),
                           Clock:             Clock,
                           CommandLine:       CommandLine
                       );

            nodes.Add(node);

            BeforeStart?.Invoke(node);

            await node.Start();

            return node;

        }

        #endregion

        #region (private static) ClientOf(Node)

        /// <summary>
        /// Norn's NTS client for the given server, holding its self-signed
        /// TLS identity to its fingerprint - as a client in a local network
        /// would, that was told the fingerprint.
        /// </summary>
        private static NTSClient ClientOf(NTSServerNode Node)

            => new (
                   DomainName.Parse("localhost"),
                   NTSKE_Port:                  Node.Settings.NTSKEPort,
                   NTP_Port:                    Node.Settings.NTPPort,
                   RemoteCertificateValidator:  (sender, certificate, chain, client, errors)
                                                    => certificate is not null &&
                                                       Convert.ToHexString(SHA256.HashData(certificate.RawData)) == Node.Host.Identity.Fingerprint
                                                           ? TLSValidationResult.Success()
                                                           : TLSValidationResult.Failed("not the certificate the server was pinned to"),
                   IPVersionPreference:         IPVersionPreference.IPv4Only,
                   Timeout:                     TimeSpan.FromSeconds(10)
               );

        #endregion

        #region (private static) ConnectOverSSH(Node, Username, Key)

        private static Task<SshClient> ConnectOverSSH(NTSServerNode  Node,
                                                      String         Username,
                                                      ISshHostKey    Key)

            => SshClient.ConnectAsync(
                   "127.0.0.1",
                   Node.SSHPort!.Value.ToUInt16(),
                   new SshClientOptions {
                       Username       = Username,
                       Credentials    = [ Key ],
                       // Pinned: over SSH the host key is the whole of the server's authentication.
                       VerifyHostKey  = blob => Node.SSHHostKey is String hostKey &&
                                                hostKey.EndsWith(SshFingerprint.Sha256(blob), StringComparison.Ordinal)
                   }
               ).AsTask();

        #endregion


        #region LocalMode_KeyExchangeOverTLS_ThenNTP()

        [Test]
        public async Task LocalMode_KeyExchangeOverTLS_ThenNTP()
        {

            var node        = await Started();

            Assert.That(node.Host.IsServing,          Is.True);
            Assert.That(node.Host.Identity.IsSelfSigned, Is.True);

            var client      = ClientOf(node);
            var keyExchange = await client.GetNTSKERecords();

            Assert.That(keyExchange.Success,          Is.True, keyExchange.ErrorMessage?.ToString());

            // The key exchange names this server's NTP port - another than 123 -
            // so that a client finds NTP without being told.
            Assert.That(keyExchange.Response!.NTPv4ServerNames, Does.Contain("localhost"));

            var query       = await client.QueryTime(NTSKEResponse: keyExchange.Response,
                                                     Timeout:       TimeSpan.FromSeconds(10));

            Assert.That(query.Success,                Is.True, query.ErrorMessage);
            Assert.That(query.Response!.Stratum,      Is.EqualTo(1));
            Assert.That(query.Response.LI,            Is.EqualTo(0));
            Assert.That(query.Response.ClockOffset!.Value.Duration(), Is.LessThan(TimeSpan.FromMilliseconds(250)));

            Assert.That(node.Host.Metrics.NTSKEResponsesSent, Is.GreaterThanOrEqualTo(1));
            Assert.That(node.Host.Metrics.NTPResponsesSent,   Is.GreaterThanOrEqualTo(1));

        }

        #endregion

        #region LocalMode_TheSelfSignedIdentityIsKeptAcrossStarts()

        [Test]
        public async Task LocalMode_TheSelfSignedIdentityIsKeptAcrossStarts()
        {

            var first       = await Started();
            var fingerprint = first.Host.Identity.Fingerprint;
            var directory   = Path.GetDirectoryName(first.ConfigFile.Path)!;

            await first.DisposeAsync();
            nodes.Remove(first);

            // Again in the same directory, on the same configuration file.
            var again = new NTSServerNode(
                            HTTPPort:          IPPort.Parse(TestPorts.Free()),
                            AccountsPath:      Path.Combine(directory, "accounts"),
                            ConfigFile:        new WWCPConfigFile(first.ConfigFile.Path),
                            CertificatesPath:  Path.Combine(directory, "certificates"),
                            LogToConsole:      false,
                            BridgeDebugLog:    false,
                            SSH:               new SSHSettings(Enabled: false)
                        );

            nodes.Add(again);

            Assert.That(again.Host.Identity.Fingerprint, Is.EqualTo(fingerprint));

        }

        #endregion

        #region LocalMode_ACookieOutlivesARestart()

        /// <summary>
        /// A client that made its key exchange before a restart is answered
        /// after it with the cookies it holds - the master keys they are
        /// sealed with are kept in the server's directory, the owner's alone.
        /// </summary>
        [Test]
        public async Task LocalMode_ACookieOutlivesARestart()
        {

            var first       = await Started();
            var keyExchange = await ClientOf(first).GetNTSKERecords();

            Assert.That(keyExchange.Success, Is.True, keyExchange.ErrorMessage?.ToString());

            var masterKeyId = first.Host.CurrentMasterKeyId;
            var keyFile     = Path.Combine(first.Directory, TimeServerHost.MasterKeysFileName);
            var configFile  = first.ConfigFile.Path;
            var directory   = Path.GetDirectoryName(configFile)!;

            Assert.That(File.Exists(keyFile), Is.True);

            if (!OperatingSystem.IsWindows())
                Assert.That(File.GetUnixFileMode(keyFile), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));

            await first.DisposeAsync();
            nodes.Remove(first);

            // Again on the same NTP port; the key exchange on another one, as
            // the client makes none.
            var configuration = JObject.Parse(File.ReadAllText(configFile));
            configuration[TimeServerSettings.SectionName]!["ntsKEPort"] = TestPorts.Free();
            File.WriteAllText(configFile, configuration.ToString());

            var again = new NTSServerNode(
                            HTTPPort:          IPPort.Parse(TestPorts.Free()),
                            AccountsPath:      Path.Combine(directory, "accounts"),
                            ConfigFile:        new WWCPConfigFile(configFile),
                            CertificatesPath:  Path.Combine(directory, "certificates"),
                            LogToConsole:      false,
                            BridgeDebugLog:    false,
                            SSH:               new SSHSettings(Enabled: false)
                        );

            nodes.Add(again);

            await again.Start();

            Assert.That(again.Host.CurrentMasterKeyId, Is.EqualTo(masterKeyId));

            var query = await ClientOf(again).QueryTime(NTSKEResponse: keyExchange.Response,
                                                        Timeout:       TimeSpan.FromSeconds(10));

            Assert.That(query.Success, Is.True, query.ErrorMessage);

        }

        #endregion

        #region LocalMode_ACookieOutlivesTheServerMadeAnew()

        /// <summary>
        /// The server is made anew when the description of the clock changes -
        /// in upstream mode, when another upstream server is the closest to
        /// the median. Its clients go on with the cookies they hold.
        /// </summary>
        [Test]
        public async Task LocalMode_ACookieOutlivesTheServerMadeAnew()
        {

            var node        = await Started();
            var client      = ClientOf(node);
            var keyExchange = await client.GetNTSKERecords();

            Assert.That(keyExchange.Success, Is.True, keyExchange.ErrorMessage?.ToString());

            var generations = node.Host.Generations;

            await node.Host.StartAsync(node.Host.Description! with { Stratum = 3 });

            Assert.That(node.Host.Generations, Is.EqualTo(generations + 1));
            Assert.That(node.Host.IsServing,   Is.True);

            var query = await client.QueryTime(NTSKEResponse: keyExchange.Response,
                                               Timeout:       TimeSpan.FromSeconds(10));

            Assert.That(query.Success,           Is.True, query.ErrorMessage);
            Assert.That(query.Response!.Stratum, Is.EqualTo(3));

        }

        #endregion

        #region KeyExchangeOverSSH_ThenNTP()

        [Test]
        public async Task KeyExchangeOverSSH_ThenNTP()
        {

            var node   = await Started();
            var key    = SshHostKey.GenerateEd25519();

            Assert.That(node.NTSKEOverSSH, Is.True);
            Assert.That(node.TrySetSSHClients([ new NTSClientEntry("cs001", [ SshPublicKey.FromHostKey(key, "cs001").ToAuthorizedKeyLine() ]) ], out var error),
                        Is.True, error);

            await using var ssh   = await ConnectOverSSH(node, "cs001", key);

            var client            = ClientOf(node);
            var keyExchange       = await client.GetNTSKERecordsOverSSH(ssh);

            Assert.That(keyExchange.Success,                Is.True, keyExchange.ErrorMessage?.ToString());
            Assert.That(keyExchange.Response!.TLSInfo,      Is.Null);

            var query             = await client.QueryTime(NTSKEResponse: keyExchange.Response,
                                                           Timeout:       TimeSpan.FromSeconds(10));

            Assert.That(query.Success,                      Is.True, query.ErrorMessage);
            Assert.That(query.Response!.Stratum,            Is.EqualTo(1));

            // And it was written down where the client list lives.
            Assert.That(File.ReadAllText(node.ConfigFile.Path), Does.Contain("cs001"));

        }

        #endregion

        #region TheNTSClientsAreShownWithTheFingerprintsOfTheirKeys()

        /// <summary>
        /// What the NTS clients page reads: each key with what it is - its
        /// algorithm, its fingerprint as ssh-keygen -l says it, its comment and
        /// its options - beside the line it was given as, which is what the
        /// page sends back.
        /// </summary>
        [Test]
        public async Task TheNTSClientsAreShownWithTheFingerprintsOfTheirKeys()
        {

            var node  = await Started();
            var key   = SshHostKey.GenerateEd25519();
            var line  = "from=\"192.168.1.0/24\" " + SshPublicKey.FromHostKey(key, "cs001").ToAuthorizedKeyLine();

            Assert.That(node.TrySetSSHClients([ new NTSClientEntry("cs001", [ line ], Enabled: false) ], out var error),
                        Is.True, error);

            var ssh     = node.SSHJSON();
            var client  = (JObject) ssh["clients"]![0]!;
            var shown   = (JObject) client["keys"]![0]!;

            Assert.Multiple(() => {
                Assert.That(ssh["enabled"]!.Value<Boolean>(),      Is.True);
                Assert.That(ssh["subsystem"]!.Value<String>(),     Is.EqualTo(NTSKE_SSH.SubsystemName));
                Assert.That(client["name"]!.Value<String>(),       Is.EqualTo("cs001"));
                Assert.That(client["enabled"]!.Value<Boolean>(),   Is.False);
                Assert.That(shown["line"]!.Value<String>(),        Is.EqualTo(line));
                Assert.That(shown["algorithm"]!.Value<String>(),   Is.EqualTo("ssh-ed25519"));
                Assert.That(shown["fingerprint"]!.Value<String>(), Is.EqualTo(SshPublicKey.FromHostKey(key).Sha256Fingerprint));
                Assert.That(shown["comment"]!.Value<String>(),     Is.EqualTo("cs001"));
                Assert.That(shown["options"]!.Values<String>(),    Does.Contain("from=\"192.168.1.0/24\""));
            });

        }

        #endregion

        #region TheStatusCarriesTheCertificateNTSKEShows()

        /// <summary>
        /// The certificate alone, in PEM, to be copied for a client - chrony's
        /// ntstrustedcerts - and the very one whose fingerprint is shown.
        /// </summary>
        [Test]
        public async Task TheStatusCarriesTheCertificateNTSKEShows()
        {

            var node  = await Started();
            var pem   = node.StatusJSON()["ntsKE"]!["tls"]!["pem"]!.Value<String>()!;

            using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem);

            Assert.Multiple(() => {
                Assert.That(pem,                                               Does.StartWith("-----BEGIN CERTIFICATE-----"));
                Assert.That(pem,                                               Does.Not.Contain("PRIVATE KEY"));
                Assert.That(Convert.ToHexString(SHA256.HashData(certificate.RawData)), Is.EqualTo(node.Host.Identity.Fingerprint));
            });

        }

        #endregion

        #region KeyExchangeOverSSH_AStrangerIsNotLetIn()

        [Test]
        public async Task KeyExchangeOverSSH_AStrangerIsNotLetIn()
        {

            var node      = await Started();
            var key       = SshHostKey.GenerateEd25519();
            var stranger  = SshHostKey.GenerateEd25519();

            Assert.That(node.TrySetSSHClients([ new NTSClientEntry("cs001", [ SshPublicKey.FromHostKey(key).ToAuthorizedKeyLine() ]) ], out var error),
                        Is.True, error);

            Assert.That(async () => await ConnectOverSSH(node, "cs001", stranger), Throws.Exception);
            Assert.That(async () => await ConnectOverSSH(node, "cs002", key),      Throws.Exception);

        }

        #endregion

        #region KeyExchangeOverSSH_ASwitchedOffClientIsNotLetIn()

        [Test]
        public async Task KeyExchangeOverSSH_ASwitchedOffClientIsNotLetIn()
        {

            var node  = await Started();
            var key   = SshHostKey.GenerateEd25519();

            Assert.That(node.TrySetSSHClients([ new NTSClientEntry("cs001", [ SshPublicKey.FromHostKey(key).ToAuthorizedKeyLine() ], Enabled: false) ], out var error),
                        Is.True, error);

            Assert.That(async () => await ConnectOverSSH(node, "cs001", key), Throws.Exception);

        }

        #endregion

        #region KeyExchangeOverSSH_SwitchedOffIsNotServed()

        [Test]
        public async Task KeyExchangeOverSSH_SwitchedOffIsNotServed()
        {

            var node  = await Started(new JObject(new JProperty("ssh", new JObject(new JProperty("enabled", false)))));

            Assert.That(node.NTSKEOverSSH, Is.False);
            Assert.That(node.SSHSubsystems.Select(subsystem => subsystem.Name), Does.Not.Contain(NTSKE_SSH.SubsystemName));

            Assert.That(node.TrySetSSHEnabled(true, out var error), Is.True, error);

            Assert.That(node.NTSKEOverSSH, Is.True);
            Assert.That(node.SSHSubsystems.Select(subsystem => subsystem.Name), Does.Contain(NTSKE_SSH.SubsystemName));

        }

        #endregion

        #region (private static) Certificate(CA, Key, Principals, ValidAfter, ValidBefore)

        /// <summary>
        /// A user certificate of the given CA for the given key, as ssh-keygen -s
        /// signs one, with the extensions OpenSSH gives a user certificate.
        /// </summary>
        private static CertifiedKey Certificate(ISshHostKey      CA,
                                                ISshHostKey      Key,
                                                String[]         Principals,
                                                DateTimeOffset?  ValidAfter   = null,
                                                DateTimeOffset?  ValidBefore  = null)
        {

            var builder = new OpenSshCertificateBuilder {
                              Serial       = 42,
                              Type         = SshCertType.User,
                              KeyId        = "charging station",
                              Principals   = [ .. Principals ],
                              ValidAfter   = ValidAfter  ?? DateTimeOffset.UtcNow.AddDays(-1),
                              ValidBefore  = ValidBefore ?? DateTimeOffset.UtcNow.AddDays(1)
                          };

            foreach (var extension in OpenSshCertificateBuilder.DefaultUserExtensions)
                builder.Extensions.Add(new KeyValuePair<String, Byte[]>(extension.Key, []));

            return new CertifiedKey(Key, builder.Sign(Key.PublicKeyBlob, CA));

        }

        #endregion

        #region (private static) WithinASecond(Condition)

        /// <summary>
        /// The server tells after the answer went out: give it the moment it may take.
        /// </summary>
        private static async Task<Boolean> WithinASecond(Func<Boolean> Condition)
        {

            for (var i = 0; i < 50; i++)
            {
                if (Condition())
                    return true;
                await Task.Delay(20);
            }

            return Condition();

        }

        #endregion


        #region EveryNTSClient_IsShownWithItsLastKeyExchange()

        /// <summary>
        /// Section 8.2 of the white paper: which clients were served, how and when
        /// last, and how many cookies went out - in the status the command line and
        /// the web interface read.
        /// </summary>
        [Test]
        public async Task EveryNTSClient_IsShownWithItsLastKeyExchange()
        {

            var node   = await Started();
            var key    = SshHostKey.GenerateEd25519();

            Assert.That(node.TrySetSSHClients([ new NTSClientEntry("cs001", [ SshPublicKey.FromHostKey(key, "cs001").ToAuthorizedKeyLine() ]),
                                                new NTSClientEntry("cs002", [ SshPublicKey.FromHostKey(SshHostKey.GenerateEd25519()).ToAuthorizedKeyLine() ]) ], out var error),
                        Is.True, error);

            await using var ssh   = await ConnectOverSSH(node, "cs001", key);

            var client            = ClientOf(node);
            var first             = await client.GetNTSKERecordsOverSSH(ssh);
            var second            = await client.GetNTSKERecordsOverSSH(ssh);

            Assert.That(first. Success, Is.True, first. ErrorMessage?.ToString());
            Assert.That(second.Success, Is.True, second.ErrorMessage?.ToString());

            Assert.That(await WithinASecond(() => node.Host.SSHActivity.TryGetValue("cs001", out var seen) && seen.Served == 2), Is.True,
                        "the key exchanges of cs001 were not counted");

            var clients           = (JArray) node.StatusJSON()["ssh"]!["clients"]!;
            var cs001             = (JObject) clients.First(entry => entry["name"]!.Value<String>() == "cs001")["activity"]!;
            var cs002             = (JObject) clients.First(entry => entry["name"]!.Value<String>() == "cs002")["activity"]!;

            // The NTS clients page reads the same beside each client, and no account made a key exchange.
            var shown             = node.SSHJSON();

            Assert.That(JToken.DeepEquals(shown["clients"]!.First(entry => entry["name"]!.Value<String>() == "cs001")["activity"], cs001), Is.True,
                        "the NTS clients page and the status say the key exchanges of cs001 differently");
            Assert.That((JArray) shown["accounts"]!, Is.Empty);

            Assert.Multiple(() => {

                Assert.That(cs001["served"]!.Value<Int64>(),                     Is.EqualTo(2));
                Assert.That(cs001["refused"]!.Value<Int64>(),                    Is.Zero);
                Assert.That(cs001["cookies"]!.Value<Int64>(),                    Is.EqualTo(2 * org.GraphDefined.Vanaheimr.Norn.NTS.NTSServer.MaxCookiesPerResponse));
                Assert.That(cs001["lastServed"]!["method"]!.Value<String>(),     Is.EqualTo("exporter"));
                Assert.That(cs001["lastServed"]!["from"]!.Value<String>(),       Does.StartWith("127.0.0.1"));
                Assert.That(cs001["lastServed"]!["aeadAlgorithm"]!.Value<Int32>(), Is.EqualTo((Int32) first.Response!.AEADAlgorithm));

                Assert.That(cs002["served"]!.Value<Int64>(),                     Is.Zero);
                Assert.That(cs002["lastServed"]!.Type,                           Is.EqualTo(JTokenType.Null));

            });

        }

        #endregion

        #region ACertificateOfTheClientsCA_LetsItIn()

        /// <summary>
        /// Section 8.2 of the white paper: an NTS client let in by the user
        /// certificates of an SSH CA, as OpenSSH's cert-authority line has it -
        /// for its own name, or for a name principals="..." gives.
        /// </summary>
        [Test]
        public async Task ACertificateOfTheClientsCA_LetsItIn()
        {

            var node   = await Started();
            var ca     = SshHostKey.GenerateEd25519();
            var other  = SshHostKey.GenerateEd25519();
            var key    = SshHostKey.GenerateEd25519();
            var caLine = "cert-authority " + SshPublicKey.FromHostKey(ca, "station CA").ToAuthorizedKeyLine();

            Assert.That(node.TrySetSSHClients([ new NTSClientEntry("cs001", [ caLine ]),
                                                new NTSClientEntry("cs003", [ "cert-authority,principals=\"station-3\" " + SshPublicKey.FromHostKey(ca).ToAuthorizedKeyLine() ]) ],
                                              out var error),
                        Is.True, error);

            // For its own name: let in, and served.
            await using (var ssh = await ConnectOverSSH(node, "cs001", Certificate(ca, key, [ "cs001" ])))
            {
                var keyExchange = await ClientOf(node).GetNTSKERecordsOverSSH(ssh);
                Assert.That(keyExchange.Success, Is.True, keyExchange.ErrorMessage?.ToString());
            }

            // For the name principals="..." gives, under another login name.
            await using (var ssh = await ConnectOverSSH(node, "cs003", Certificate(ca, key, [ "station-3" ])))
            {
                var keyExchange = await ClientOf(node).GetNTSKERecordsOverSSH(ssh);
                Assert.That(keyExchange.Success, Is.True, keyExchange.ErrorMessage?.ToString());
            }

            Assert.Multiple(() => {

                // For another client, of another CA, run out, or not yet valid: turned away.
                Assert.That(async () => await ConnectOverSSH(node, "cs001", Certificate(ca,    key, [ "cs002" ])),                         Throws.Exception, "for another name");
                Assert.That(async () => await ConnectOverSSH(node, "cs001", Certificate(other, key, [ "cs001" ])),                         Throws.Exception, "of another CA");
                Assert.That(async () => await ConnectOverSSH(node, "cs001", Certificate(ca,    key, [ "cs001" ],
                                                                                         ValidAfter:  DateTimeOffset.UtcNow.AddDays(-2),
                                                                                         ValidBefore: DateTimeOffset.UtcNow.AddDays(-1))),       Throws.Exception, "run out");
                Assert.That(async () => await ConnectOverSSH(node, "cs003", Certificate(ca,    key, [ "cs003" ])),                         Throws.Exception, "the login name where principals= names another");

                // The CA's key itself is no key to sign in with.
                Assert.That(async () => await ConnectOverSSH(node, "cs001", ca),                                                           Throws.Exception, "the CA's own key");

            });

            var shown = (JObject) node.SSHJSON()["clients"]![0]!["keys"]![0]!;

            Assert.That(shown["certAuthority"]!.Value<Boolean>(), Is.True);

        }

        #endregion

        #region TheMasterKey_IsRotated_AndForgotten()

        /// <summary>
        /// Section 8.2 of the white paper: the cookie master key rotated by an
        /// administrator - the cookies a client holds still answered - and, for
        /// a key that may have been read, the earlier keys forgotten at once.
        /// </summary>
        [Test]
        public async Task TheMasterKey_IsRotated_AndForgotten()
        {

            var node         = await Started();
            var client       = ClientOf(node);
            var keyExchange  = await client.GetNTSKERecords();

            Assert.That(keyExchange.Success, Is.True, keyExchange.ErrorMessage?.ToString());

            var before       = node.Host.CurrentMasterKeyId;
            var rotated      = node.Host.RotateMasterKey();

            Assert.That(rotated,           Is.Not.Null);
            Assert.That(rotated!.Id,       Is.GreaterThan(before!.Value));
            Assert.That(node.Host.CurrentMasterKeyId, Is.EqualTo(rotated.Id));

            var keys         = (JArray) node.StatusJSON()["masterKeys"]!;

            Assert.That(keys.Select(key => (key["id"]!.Value<UInt64>(), key["current"]!.Value<Boolean>())),
                        Is.EqualTo(new[] { (before.Value, false), (rotated.Id, true) }));

            var answered     = await client.QueryTime(NTSKEResponse: keyExchange.Response,
                                                      Timeout:       TimeSpan.FromSeconds(10));

            Assert.That(answered.Success, Is.True, answered.ErrorMessage);

            node.Host.RotateMasterKey(ForgetTheOthers: true);

            Assert.That(node.Host.MasterKeys, Has.Count.EqualTo(1));

            var refused      = await ClientOf(node).QueryTime(NTSKEResponse: keyExchange.Response,
                                                              Timeout:       TimeSpan.FromSeconds(3));

            Assert.That(refused.Success, Is.False, "a cookie of a forgotten master key was answered");

        }

        #endregion

        #region (private static) AccountWithPassword(Node, Name, Role)

        /// <summary>
        /// An account of the node's organization in the group of the given role,
        /// with a password of its own - and an HTTP client signed in as it with
        /// HTTP Basic Auth, as the JSON API takes it.
        /// </summary>
        private static async Task<HttpClient> AccountWithPassword(NTSServerNode  Node,
                                                                  String         Name,
                                                                  String         Role)
        {

            var password = "A-Test-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

            Assert.That(Node.ExtAPI.TryGetOrganization(Organization_Id.Parse(Node.Kind.Organization), out var organization) &&
                        organization is Organization, Is.True, "the node's organization is not there");

            await Node.ExtAPI.CreateUser(User_Id.Parse(Name),
                                         I18NString.Create(Languages.en, Name),
                                         SimpleEMailAddress.Parse($"{Name}@localhost"),
                                         User2OrganizationEdgeLabel.IsMember,
                                         (Organization) organization!,
                                         Password:                  password,
                                         SkipDefaultNotifications:  true,
                                         SkipNewUserEMail:          true,
                                         SkipNewUserNotifications:  true,
                                         AcceptedEULA:              DateTimeOffset.UtcNow.AddSeconds(-1),
                                         IsAuthenticated:           true);

            Assert.That(Node.ExtAPI.TryGetUser     (User_Id.     Parse(Name), out var user),  Is.True, $"the account '{Name}' was not made");
            Assert.That(Node.ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(Role), out var group) &&
                        group is UserGroup, Is.True, $"the node has no group '{Role}'");

            await Node.ExtAPI.AddUserToUserGroup((User) user!,
                                                 User2UserGroupEdgeLabel.IsMember,
                                                 (UserGroup) group!);

            var http = new HttpClient();

            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                                                           "Basic",
                                                           Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{Name}:{password}"))
                                                       );

            return http;

        }

        #endregion

        #region TheMasterKey_IsRotatedOverTheJSONAPI()

        /// <summary>
        /// The web interface's "New master key" and "New key, and forget the
        /// others": POST v1/ntsServer/masterKeys/rotate, for whoever may change
        /// the time server - answered with the new key and the keys kept, never
        /// a value.
        /// </summary>
        [Test]
        public async Task TheMasterKey_IsRotatedOverTheJSONAPI()
        {

            var node     = await Started();
            var before   = node.Host.CurrentMasterKeyId!.Value;
            var url      = $"{node.APIURL}v1/ntsServer/masterKeys/rotate";

            using var anybody  = new HttpClient();
            using var admin    = new HttpClient();

            admin.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                                                            "Basic",
                                                            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{WWCPNode.DefaultAdminUser}:{node.GeneratedPassword}"))
                                                        );

            static StringContent JSON(String Text) => new (Text, System.Text.Encoding.UTF8, "application/json");

            // Nobody signed in: nothing rotated.
            var refused  = await anybody.PostAsync(url, JSON("{}"));

            Assert.That((Int32) refused.StatusCode,   Is.EqualTo(401));
            Assert.That(node.Host.CurrentMasterKeyId, Is.EqualTo(before));

            // An operator, who may read the time server but not change it: nothing rotated.
            using var operatorHTTP = await AccountWithPassword(node, "operator1", NTSServerAccess.Operator.Name);

            var reader   = await operatorHTTP.PostAsync(url, JSON("{}"));

            Assert.That((Int32) reader.StatusCode,    Is.EqualTo(403), await reader.Content.ReadAsStringAsync());
            Assert.That(node.Host.CurrentMasterKeyId, Is.EqualTo(before));

            // "forget" that is no true or false: nothing rotated.
            var bad      = await admin.PostAsync(url, JSON("""{ "forget": "yes" }"""));

            Assert.That((Int32) bad.StatusCode,       Is.EqualTo(400));
            Assert.That(node.Host.CurrentMasterKeyId, Is.EqualTo(before));

            // A new key that keeps the others.
            var kept     = await admin.PostAsync(url, JSON("""{ "forget": false }"""));
            var keptJSON = JObject.Parse(await kept.Content.ReadAsStringAsync());

            Assert.That((Int32) kept.StatusCode, Is.EqualTo(200), keptJSON.ToString());
            Assert.Multiple(() => {
                Assert.That(keptJSON["rotated"]!["id"]!.Value<UInt64>(),       Is.EqualTo(node.Host.CurrentMasterKeyId));
                Assert.That(keptJSON["rotated"]!["current"]!.Value<Boolean>(), Is.True);
                Assert.That(keptJSON["forgotten"]!.Value<Boolean>(),           Is.False);
                Assert.That(((JArray) keptJSON["masterKeys"]!).Select(key => key["id"]!.Value<UInt64>()),
                            Is.EqualTo(new[] { before, node.Host.CurrentMasterKeyId!.Value }));
                Assert.That(keptJSON.ToString(), Does.Not.Contain("value"), "a master key's value went out");
            });

            // Without a body, as the same; then one that forgets the others.
            var plain    = await admin.PostAsync(url, null);

            Assert.That((Int32) plain.StatusCode, Is.EqualTo(200));
            Assert.That(node.Host.MasterKeys,     Has.Count.EqualTo(3));

            var forgot   = await admin.PostAsync(url, JSON("""{ "forget": true }"""));
            var forgotJSON = JObject.Parse(await forgot.Content.ReadAsStringAsync());

            Assert.That((Int32) forgot.StatusCode, Is.EqualTo(200), forgotJSON.ToString());
            Assert.Multiple(() => {
                Assert.That(forgotJSON["forgotten"]!.Value<Boolean>(), Is.True);
                Assert.That(((JArray) forgotJSON["masterKeys"]!).Select(key => key["id"]!.Value<UInt64>()),
                            Is.EqualTo(new[] { node.Host.CurrentMasterKeyId!.Value }));
                Assert.That(node.Host.MasterKeys, Has.Count.EqualTo(1));
            });

        }

        #endregion

        #region TheStatusIsNobodysBusinessWithoutASignIn()

        [Test]
        public async Task TheStatusIsNobodysBusinessWithoutASignIn()
        {

            var node      = await Started();

            using var http = new HttpClient();

            var response  = await http.GetAsync($"{node.APIURL}v1/ntsServer");

            Assert.That((Int32) response.StatusCode, Is.EqualTo(401));

            var status    = node.StatusJSON();

            Assert.That(status["serving"]?.Value<Boolean>(),  Is.True);
            Assert.That(status["timeSource"]?.Value<String>(), Is.EqualTo("local"));

        }

        #endregion

        #region APortThatIsTakenEndsTheStart()

        [Test]
        public async Task APortThatIsTakenEndsTheStart()
        {

            using var taken = new System.Net.Sockets.UdpClient(new IPEndPoint(System.Net.IPAddress.Loopback, 0));
            var port        = ((IPEndPoint) taken.Client.LocalEndPoint!).Port;

            var problem     = await Assert.ThrowsAsync<PortUnavailableException>(async () => await Started(new JObject(new JProperty("ntpPort", port))));

            Assert.That(problem!.Whose, Is.EqualTo(TimeServerHost.NTPPort));

        }

        #endregion


        #region UpstreamMode_FollowsAnotherNTSServer()

        /// <summary>
        /// One NTS server in local mode, and a second one in upstream mode that
        /// follows it - asked over NTS-KE (TLS 1.3), its self-signed identity
        /// held to as a root of the node's own store for "nts", as a local
        /// network without a PKI would hold it.
        /// </summary>
        [Test]
        public async Task UpstreamMode_FollowsAnotherNTSServer()
        {

            var upstream   = await Started(new JObject(new JProperty("local", new JObject(new JProperty("stratum", 1), new JProperty("referenceId", "GPS")))));
            var downstream = await StartedFollowing(upstream);

            Assert.That(downstream.Clock.IsFreeRunning, Is.True);

            // Nothing is answered before the upstream server gave a time.
            for (var i = 0; i < 60 && !downstream.Host.IsServing; i++)
                await Task.Delay(500);

            var status = downstream.Upstream!.Status();

            Assert.That(downstream.Host.IsServing, Is.True, $"not synchronized: {status.LastProblem}");
            Assert.That(status.Synchronized,       Is.True);
            Assert.That(status.SystemPeer,         Is.EqualTo("localhost"));
            Assert.That(status.UpstreamStratum,    Is.EqualTo(1));

            // One below its upstream server, which it names by its address.
            Assert.That(downstream.Host.Description!.Stratum,      Is.EqualTo(2));
            Assert.That(downstream.Host.Description.ReferenceText, Does.Contain("localhost"));

            // And a client of the downstream server gets that time, NTS-authenticated.
            var client      = ClientOf(downstream);
            var keyExchange = await client.GetNTSKERecords();

            Assert.That(keyExchange.Success, Is.True, keyExchange.ErrorMessage?.ToString());

            var query       = await client.QueryTime(NTSKEResponse: keyExchange.Response,
                                                     Timeout:       TimeSpan.FromSeconds(10));

            Assert.That(query.Success,                Is.True, query.ErrorMessage);
            Assert.That(query.Response!.Stratum,      Is.EqualTo(2));
            Assert.That(query.Response.ClockOffset!.Value.Duration(), Is.LessThan(TimeSpan.FromMilliseconds(250)));

        }

        #endregion

        #region UpstreamMode_PassesOnALeapSecondAndMakesItAtMidnight()

        /// <summary>
        /// The system's clock, moved by as much as it is told.
        /// </summary>
        private sealed class ShiftedClock(TimeSpan Shift) : TimeProvider
        {

            public TimeSpan Shift { get; set; } = Shift;

            public override DateTimeOffset GetUtcNow()
                => System.GetUtcNow() + Shift;

        }

        /// <summary>
        /// An upstream server announcing an inserted leap second on the last day
        /// of a month: followed, the announcement is passed on to the clients,
        /// the second made at midnight - 23:59:59 repeated - and announced no
        /// longer from then on.
        /// </summary>
        /// <remarks>
        /// Both servers live ten minutes before the end of last month from their
        /// start, so that their certificates and the master keys of their
        /// cookies are made there and valid there; the upstream server is then
        /// moved on to five seconds before midnight, and followed there.
        /// </remarks>
        [Test]
        public async Task UpstreamMode_PassesOnALeapSecondAndMakesItAtMidnight()
        {

            var now        = DateTimeOffset.UtcNow;
            var midnight   = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var leap       = new LeapSecond(midnight, Inserted: true);
            var shifted    = new ShiftedClock(midnight - TimeSpan.FromMinutes(10) - now);

            var upstream   = await Started(new JObject(new JProperty("local", new JObject(new JProperty("stratum", 1), new JProperty("referenceId", "GPS")))),
                                           Clock: new ServedClock(shifted));

            var downstream = await StartedFollowing(upstream, Clock: new ServedClock(shifted));

            for (var i = 0; i < 60 && !downstream.Host.IsServing; i++)
                await Task.Delay(500);

            Assert.That(downstream.Host.IsServing, Is.True, $"not synchronized: {downstream.Upstream!.Status().LastProblem}");

            // The upstream server announces it - as the kernel of a server would - and
            // is five seconds before midnight now, which the server following it finds
            // further off than the step threshold, and is stepped to on the third round.
            await upstream.Host.StartAsync(upstream.Host.Description! with { LeapIndicator = 1 });

            shifted.Shift += midnight - TimeSpan.FromSeconds(5) - shifted.GetUtcNow();

            UpstreamStatus status = null!;

            for (var round = 0; round < UpstreamDiscipline.StepoutRounds; round++)
                status = await downstream.Upstream!.PollAsync();

            Assert.That(status.AnnouncedLeapIndicator,              Is.EqualTo(1));
            Assert.That(status.ScheduledLeap,                       Is.EqualTo(leap));
            Assert.That(downstream.Host.Description!.LeapIndicator, Is.EqualTo(1));

            var client      = ClientOf(downstream);
            var keyExchange = await client.GetNTSKERecords();

            Assert.That(keyExchange.Success, Is.True, keyExchange.ErrorMessage?.ToString());

            var before      = await client.QueryTime(NTSKEResponse: keyExchange.Response,
                                                     Timeout:       TimeSpan.FromSeconds(10));

            Assert.That(before.Success,       Is.True, before.ErrorMessage);
            Assert.That(before.Response!.LI,  Is.EqualTo(1));

            // Made at midnight, and announced no longer.
            for (var i = 0; i < 150 && !(downstream.Clock.HasMade(leap) && downstream.Host.Description!.LeapIndicator == 0); i++)
                await Task.Delay(100);

            Assert.That(downstream.Clock.HasMade(leap),               Is.True);
            Assert.That(downstream.Clock.LeapSeconds,                 Is.EqualTo(TimeSpan.FromSeconds(-1)));
            Assert.That(downstream.Clock.ScheduledLeap,               Is.Null);
            Assert.That(downstream.Host.Description!.LeapIndicator,  Is.EqualTo(0));

            // The upstream server here made none - local mode leaves leap seconds to the
            // machine's clock - so the time handed out is a second behind it now.
            Assert.That((upstream.Clock.GetUtcNow() - downstream.Clock.GetUtcNow()).TotalSeconds, Is.EqualTo(1).Within(0.1));

            keyExchange     = await client.GetNTSKERecords();

            var after       = await client.QueryTime(NTSKEResponse: keyExchange.Response,
                                                     Timeout:       TimeSpan.FromSeconds(10));

            Assert.That(after.Success,        Is.True, after.ErrorMessage);
            Assert.That(after.Response!.LI,   Is.EqualTo(0));

        }

        #endregion

        #region (private) StartedFollowing(Upstream, TimeSource = "upstream", Clock = null)

        /// <summary>
        /// An NTS server in upstream mode following the given one - asked over
        /// NTS-KE (TLS 1.3), its self-signed identity held to as a root of the
        /// node's own store for "nts", as a local network without a PKI would
        /// hold it - every 16 seconds.
        /// </summary>
        private async Task<NTSServerNode> StartedFollowing(NTSServerNode  Upstream,
                                                           String         TimeSource = "upstream",
                                                           ServedClock?   Clock = null)
        {

            var groups     = new JObject(
                                 new JProperty("nts", new JObject(
                                     new JProperty("groups", new JObject(
                                         new JProperty("legal", new JObject(
                                             new JProperty("servers", new JArray(
                                                 new JObject(
                                                     new JProperty("hostname",   "localhost"),
                                                     new JProperty("ntsKEPort",  Upstream.Settings.NTSKEPort.ToUInt16()),
                                                     new JProperty("ntpPort",    Upstream.Settings.NTPPort.  ToUInt16())
                                                 )
                                             )),
                                             new JProperty("minServers", 1)
                                         ))
                                     ))
                                 ))
                             );

            var pem        = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Upstream.ConfigFile.Path)!, NTSServerNode.DefaultDirectory, NTSKETLSIdentity.SelfSignedFileName));
            var certOnly   = pem[..(pem.IndexOf("-----END CERTIFICATE-----", StringComparison.Ordinal) + "-----END CERTIFICATE-----".Length)];

            var downstream = await Started(
                                 new JObject(
                                     new JProperty("timeSource",  TimeSource),
                                     new JProperty("upstream",    new JObject(new JProperty("pollSeconds", 16)))
                                 ),
                                 groups,
                                 BeforeStart: node => {

                                     // The upstream server's self-signed certificate, as a
                                     // root for "nts" - the one thing that makes it believed.
                                     Assert.That(node.Certificates.Import(System.Text.Encoding.ASCII.GetBytes(certOnly),
                                                                          CertificateKind.TLSRoot,
                                                                          null,
                                                                          "upstream",
                                                                          [ CertificateUsages.NTS ],
                                                                          out _,
                                                                          out var importError),
                                                 Is.True, importError);

                                 },
                                 Clock: Clock
                             );

            return downstream;

        }

        #endregion

        #region UpstreamMode_NothingIsAnsweredWithoutAnUpstreamTime()

        [Test]
        public async Task UpstreamMode_NothingIsAnsweredWithoutAnUpstreamTime()
        {

            // A group whose only server is a port nobody listens on.
            var groups = new JObject(
                             new JProperty("nts", new JObject(
                                 new JProperty("groups", new JObject(
                                     new JProperty("legal", new JObject(
                                         new JProperty("servers", new JArray(
                                             new JObject(
                                                 new JProperty("hostname",   "localhost"),
                                                 new JProperty("ntsKEPort",  TestPorts.Free()),
                                                 new JProperty("ntpPort",    UDPPorts.Free())
                                             )
                                         )),
                                         new JProperty("minServers", 1)
                                     ))
                                 ))
                             ))
                         );

            var node   = await Started(new JObject(new JProperty("timeSource", "upstream")), groups);

            var status = await node.Upstream!.PollAsync();

            Assert.That(status.Synchronized,  Is.False);
            Assert.That(status.LastProblem,   Is.Not.Null);
            Assert.That(node.Host.IsServing,  Is.False);

            // The key exchange over SSH has nobody to answer either.
            Assert.That(node.NTSKEOverSSH,    Is.True);

        }

        #endregion

        #region UpstreamMode_DoesNotFollowAServerThatIsNotSynchronizedItself()

        /// <summary>
        /// An upstream server that says its own clock is not synchronized -
        /// leap indicator 3 - is not followed, however well it authenticates:
        /// its answer counts neither toward the quorum nor in the median, and
        /// nothing is handed out.
        /// </summary>
        /// <remarks>
        /// It is Norn's NTS client that turns the answer away, not this server;
        /// the test is here because this server relies on it, and because the
        /// reason has to reach the log. No NTS server of this kind hands out 3 -
        /// see the remarks of TimeServerHost - so the upstream server is told
        /// to, as the kernel of another server would tell it.
        /// </remarks>
        [Test]
        public async Task UpstreamMode_DoesNotFollowAServerThatIsNotSynchronizedItself()
        {

            var upstream   = await Started(new JObject(new JProperty("local", new JObject(new JProperty("stratum", 1), new JProperty("referenceId", "GPS")))));

            await upstream.Host.StartAsync(upstream.Host.Description! with { LeapIndicator = 3 });

            var downstream = await StartedFollowing(upstream);
            var status     = await downstream.Upstream!.PollAsync();

            Assert.Multiple(() => {
                Assert.That(status.Synchronized,        Is.False,  "a server that is not synchronized itself was followed");
                Assert.That(status.Answered,            Is.EqualTo(0));
                Assert.That(status.LastProblem,         Does.Contain("unsynchronized clock"));
                Assert.That(downstream.Host.IsServing,  Is.False);
            });

        }

        #endregion


        #region (private) SectionInFile(Node)

        /// <summary>
        /// The "ntsServer" section as the configuration file has it now.
        /// </summary>
        private static JObject SectionInFile(NTSServerNode Node)

            => (JObject) JObject.Parse(File.ReadAllText(Node.ConfigFile.Path))[TimeServerSettings.SectionName]!;

        #endregion

        #region Settings_OtherPortsAreAnsweredOnAtOnce()

        /// <summary>
        /// Other ports, while it runs: answered on at once, said to whoever
        /// changed them, and kept.
        /// </summary>
        [Test]
        public async Task Settings_OtherPortsAreAnsweredOnAtOnce()
        {

            var node     = await Started();
            var ntsKE    = TestPorts.Free();
            var ntp      = UDPPorts.Free();

            var change   = await node.ChangeSettingsAsync(new JObject(new JProperty("ntsKEPort", ntsKE), new JProperty("ntpPort", ntp)), "tester");

            Assert.That(change.IsDone,                     Is.True, change.NotApplied ?? change.Refused ?? change.NotSaved);
            Assert.That(change.Changed,                    Is.EquivalentTo(new[] { "ntsKEPort", "ntpPort" }));
            Assert.That(change.Notes,                      Has.Some.Contains($"NTS-KE on TCP {ntsKE}, NTP on UDP {ntp}"));
            Assert.That(node.Settings.NTSKEPort.ToUInt16(), Is.EqualTo(ntsKE));
            Assert.That(node.Host.IsServing,               Is.True);

            // Asked on the new ports - ClientOf asks where the settings say.
            var client      = ClientOf(node);
            var keyExchange = await client.GetNTSKERecords();

            Assert.That(keyExchange.Success, Is.True, keyExchange.ErrorMessage?.ToString());

            var query       = await client.QueryTime(NTSKEResponse: keyExchange.Response, Timeout: TimeSpan.FromSeconds(10));

            Assert.That(query.Success, Is.True, query.ErrorMessage);

            var section     = SectionInFile(node);

            Assert.That(section["ntsKEPort"]!.Value<Int32>(), Is.EqualTo(ntsKE));
            Assert.That(section["ntpPort"]!.  Value<Int32>(), Is.EqualTo(ntp));

            // The same again changes nothing.
            var again       = await node.ChangeSettingsAsync(new JObject(new JProperty("ntpPort", ntp)), "tester");

            Assert.That(again.IsDone,   Is.True);
            Assert.That(again.Changed,  Is.Empty);

        }

        #endregion

        #region Settings_APortSomebodyElseHasChangesNothing()

        /// <summary>
        /// A port somebody else has: refused, with the server that answered
        /// still answering where it did, and nothing written.
        /// </summary>
        [Test]
        public async Task Settings_APortSomebodyElseHasChangesNothing()
        {

            using var taken = new System.Net.Sockets.UdpClient(new IPEndPoint(System.Net.IPAddress.Loopback, 0));
            var port        = ((IPEndPoint) taken.Client.LocalEndPoint!).Port;

            var node        = await Started();
            var before      = node.Settings;
            var inFile      = SectionInFile(node).ToString();

            var change      = await node.ChangeSettingsAsync(new JObject(new JProperty("ntpPort", port), new JProperty("local", new JObject(new JProperty("stratum", 2)))), "tester");

            Assert.That(change.NotApplied,        Is.Not.Null);
            Assert.That(change.Changed,           Is.Empty);
            Assert.That(node.Settings,            Is.SameAs(before));
            Assert.That(node.Host.IsServing,      Is.True);
            Assert.That(node.Host.Settings.NTPPort, Is.EqualTo(before.NTPPort));
            Assert.That(node.Host.Description!.Stratum, Is.EqualTo(1));
            Assert.That(SectionInFile(node).ToString(), Is.EqualTo(inFile));

            var client      = ClientOf(node);
            var keyExchange = await client.GetNTSKERecords();
            var query       = await client.QueryTime(NTSKEResponse: keyExchange.Response, Timeout: TimeSpan.FromSeconds(10));

            Assert.That(query.Success, Is.True, query.ErrorMessage);

        }

        #endregion

        #region Settings_WhatIsNoSettingIsRefused()

        [Test]
        public async Task Settings_WhatIsNoSettingIsRefused()
        {

            var node = await Started();

            foreach (var changes in new[] {
                         new JObject(new JProperty("ntpPort",  "the usual one")),
                         new JObject(new JProperty("local",    new JObject(new JProperty("stratum", 16)))),
                         new JObject(new JProperty("local",    new JObject(new JProperty("stratum", "three")))),
                         new JObject(new JProperty("upstream", new JObject(new JProperty("pollSeconds", 4)))),
                         new JObject(new JProperty("timeSource", "sundial")),
                         new JObject(new JProperty("hostname", "not a host name")),
                         new JObject(new JProperty("anything", 1)),
                         new JObject(new JProperty("ssh",      new JObject(new JProperty("enabled", false))))
                     })
            {

                var change = await node.ChangeSettingsAsync(changes, "tester");

                Assert.That(change.Refused, Is.Not.Null, $"{changes.ToString(Newtonsoft.Json.Formatting.None)} was not refused");
                Assert.That(change.Changed, Is.Empty);

            }

            Assert.That(node.Settings.SSHEnabled, Is.True);

        }

        #endregion

        #region Settings_WhatLocalModeSaysIsAnsweredAtOnce()

        [Test]
        public async Task Settings_WhatLocalModeSaysIsAnsweredAtOnce()
        {

            var node   = await Started();

            var change = await node.ChangeSettingsAsync(new JObject(new JProperty("local", new JObject(new JProperty("stratum", 3), new JProperty("referenceId", "GPS")))), "tester");

            Assert.That(change.IsDone,  Is.True, change.NotApplied ?? change.Refused ?? change.NotSaved);
            Assert.That(change.Changed, Is.EquivalentTo(new[] { "local.stratum", "local.referenceId" }));

            // Nothing to say of upstream mode, whose settings the file leaves at their defaults.
            Assert.That(change.Notes,   Is.Empty);

            Assert.That(node.Host.Description!.Stratum,       Is.EqualTo(3));
            Assert.That(node.Host.Description.ReferenceText,  Is.EqualTo("GPS"));

            var client      = ClientOf(node);
            var keyExchange = await client.GetNTSKERecords();
            var query       = await client.QueryTime(NTSKEResponse: keyExchange.Response, Timeout: TimeSpan.FromSeconds(10));

            Assert.That(query.Success,            Is.True, query.ErrorMessage);
            Assert.That(query.Response!.Stratum,  Is.EqualTo(3));

            var section     = SectionInFile(node);

            Assert.That(section["local"]!["stratum"]!.Value<Int32>(),        Is.EqualTo(3));
            Assert.That(section["local"]!["referenceId"]!.Value<String>(),   Is.EqualTo("GPS"));

            // A hostname taken away is taken out of the file, so that it reads as none.
            var cleared     = await node.ChangeSettingsAsync(new JObject(new JProperty("hostname", JValue.CreateNull())), "tester");

            Assert.That(cleared.Changed,          Is.EquivalentTo(new[] { "hostname" }));
            Assert.That(node.Settings.Hostname,   Is.Null);
            Assert.That(SectionInFile(node).ContainsKey("hostname"), Is.False);

            // How upstream mode would follow its group is kept, and said to be kept.
            var kept        = await node.ChangeSettingsAsync(new JObject(new JProperty("upstream", new JObject(new JProperty("pollSeconds", 128)))), "tester");

            Assert.That(kept.Changed,             Is.EquivalentTo(new[] { "upstream.pollSeconds" }));
            Assert.That(kept.Notes,               Has.Some.Contains("kept for upstream mode"));
            Assert.That(node.Upstream,            Is.Null);

        }

        #endregion

        #region Settings_ASwitchOfThisStartIsNeverWrittenBack()

        /// <summary>
        /// A start with a switch that sets the NTP port: a change of something
        /// else does not write that port into the file, and a change of the
        /// port itself is in effect, saved, and said to be set otherwise by the
        /// switch at the next start.
        /// </summary>
        [Test]
        public async Task Settings_ASwitchOfThisStartIsNeverWrittenBack()
        {

            var switched    = UDPPorts.Free();
            var node        = await Started(CommandLine: settings => settings with { NTPPort = IPPort.Parse(switched) });
            var inFile      = SectionInFile(node)["ntpPort"]!.Value<Int32>();

            Assert.That(node.Settings.NTPPort.ToUInt16(), Is.EqualTo(switched));
            Assert.That(inFile,                           Is.Not.EqualTo(switched));

            var change      = await node.ChangeSettingsAsync(new JObject(new JProperty("rateLimit", true), new JProperty("ntpPort", switched)), "tester");

            Assert.That(change.Changed,                          Is.EquivalentTo(new[] { "rateLimit" }));
            Assert.That(SectionInFile(node)["ntpPort"]!.Value<Int32>(), Is.EqualTo(inFile));
            Assert.That(SectionInFile(node)["rateLimit"]!.Value<Boolean>(), Is.True);

            var other       = UDPPorts.Free();
            var changed     = await node.ChangeSettingsAsync(new JObject(new JProperty("ntpPort", other)), "tester");

            Assert.That(changed.IsDone,                          Is.True, changed.NotApplied);
            Assert.That(node.Settings.NTPPort.ToUInt16(),        Is.EqualTo(other));
            Assert.That(SectionInFile(node)["ntpPort"]!.Value<Int32>(), Is.EqualTo(other));
            Assert.That(changed.Notes,                           Has.Some.Contains("\"ntpPort\" is set otherwise by a switch"));

        }

        #endregion

        #region Settings_AnotherHostnameMakesTheSelfSignedCertificateAnew()

        [Test]
        public async Task Settings_AnotherHostnameMakesTheSelfSignedCertificateAnew()
        {

            var node    = await Started();
            var before  = node.Host.Identity.Fingerprint;

            var change  = await node.ChangeSettingsAsync(new JObject(new JProperty("hostname", "time.example.lan")), "tester");

            Assert.That(change.IsDone,                   Is.True, change.NotApplied ?? change.Refused ?? change.NotSaved);
            Assert.That(node.Host.Identity.Fingerprint,  Is.Not.EqualTo(before));
            Assert.That(node.Host.Identity.Names,        Does.Contain("time.example.lan"));
            Assert.That(change.Notes,                    Has.Some.Contains(node.Host.Identity.Fingerprint));
            Assert.That(node.Host.IsServing,             Is.True);

            Assert.That(node.StatusJSON()["ntsKE"]!["tls"]!["fingerprint"]!.Value<String>(), Is.EqualTo(node.Host.Identity.Fingerprint));

        }

        #endregion

        #region Settings_FromLocalToUpstreamAndBack()

        /// <summary>
        /// A server in local mode told to follow another, and back: following
        /// it on a clock of its own from then on, answering once it has its
        /// time - and back on this machine's clock, answering at once.
        /// </summary>
        [Test]
        public async Task Settings_FromLocalToUpstreamAndBack()
        {

            var upstream = await Started(new JObject(new JProperty("local", new JObject(new JProperty("stratum", 1), new JProperty("referenceId", "GPS")))));
            var node     = await StartedFollowing(upstream, TimeSource: "local");

            Assert.That(node.Upstream,             Is.Null);
            Assert.That(node.Host.Description!.Stratum, Is.EqualTo(1));

            var change   = await node.ChangeSettingsAsync(new JObject(new JProperty("timeSource", "upstream")), "tester");

            Assert.That(change.IsDone,             Is.True, change.NotApplied ?? change.Refused ?? change.NotSaved);
            Assert.That(change.Changed,            Is.EquivalentTo(new[] { "timeSource" }));
            Assert.That(change.Notes,              Has.Some.Contains("Nothing is answered until the NTS group 'legal' has given a time"));
            Assert.That(node.Upstream,             Is.Not.Null);
            Assert.That(node.Clock.IsFreeRunning,  Is.True);

            for (var i = 0; i < 60 && !(node.Host.IsServing && node.Host.Description?.Stratum == 2); i++)
                await Task.Delay(250);

            Assert.That(node.Host.Description?.Stratum, Is.EqualTo(2), $"not following: {node.Upstream!.Status().LastProblem}");
            Assert.That(node.StatusJSON()["timeSource"]!.Value<String>(), Is.EqualTo("upstream"));
            Assert.That(SectionInFile(node)["timeSource"]!.Value<String>(), Is.EqualTo("upstream"));

            var back     = await node.ChangeSettingsAsync(new JObject(new JProperty("timeSource", "local")), "tester");

            Assert.That(back.IsDone,               Is.True, back.NotApplied ?? back.Refused ?? back.NotSaved);
            Assert.That(back.Notes,                Has.Some.Contains("this machine's clock from now on"));
            Assert.That(node.Upstream,             Is.Null);
            Assert.That(node.Clock.IsFreeRunning,  Is.False);
            Assert.That(node.Host.IsServing,       Is.True);
            Assert.That(node.Host.Description!.Stratum, Is.EqualTo(1));

        }

        #endregion

        #region Settings_AnotherPollIntervalIsFollowedFromTheNextRound()

        [Test]
        public async Task Settings_AnotherPollIntervalIsFollowedFromTheNextRound()
        {

            var upstream = await Started();
            var node     = await StartedFollowing(upstream);

            for (var i = 0; i < 60 && !node.Host.IsServing; i++)
                await Task.Delay(250);

            var polls    = node.Upstream!.Status().Polls;

            var change   = await node.ChangeSettingsAsync(new JObject(new JProperty("upstream", new JObject(new JProperty("pollSeconds", 20), new JProperty("holdoverMinutes", 5)))), "tester");

            Assert.That(change.IsDone,                                 Is.True, change.NotApplied ?? change.Refused ?? change.NotSaved);
            Assert.That(change.Changed,                                Is.EquivalentTo(new[] { "upstream.pollSeconds", "upstream.holdoverMinutes" }));
            Assert.That(node.Upstream!.Settings.Poll,                  Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(node.Upstream.Settings.HoldoverTime,           Is.EqualTo(TimeSpan.FromMinutes(5)));

            // Asked at once, rather than after the interval before.
            for (var i = 0; i < 40 && node.Upstream.Status().Polls == polls; i++)
                await Task.Delay(250);

            Assert.That(node.Upstream.Status().Polls,                  Is.GreaterThan(polls));
            Assert.That(SectionInFile(node)["upstream"]!["pollSeconds"]!.Value<Double>(), Is.EqualTo(20));

        }

        #endregion

    }

}
