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
using org.GraphDefined.Vanaheimr.Hermod.DNS;
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
    public class NTSServerNodeTests
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
                                                  Action<NTSServerNode>? BeforeStart = null)
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
                           SSH:               new SSHSettings(Enabled: true, Port: IPPort.Parse(TestPorts.Free()))
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

            var groups     = new JObject(
                                 new JProperty("nts", new JObject(
                                     new JProperty("groups", new JObject(
                                         new JProperty("legal", new JObject(
                                             new JProperty("servers", new JArray(
                                                 new JObject(
                                                     new JProperty("hostname",   "localhost"),
                                                     new JProperty("ntsKEPort",  upstream.Settings.NTSKEPort.ToUInt16()),
                                                     new JProperty("ntpPort",    upstream.Settings.NTPPort.  ToUInt16())
                                                 )
                                             )),
                                             new JProperty("minServers", 1)
                                         ))
                                     ))
                                 ))
                             );

            var pem        = File.ReadAllText(Path.Combine(Path.GetDirectoryName(upstream.ConfigFile.Path)!, NTSServerNode.DefaultDirectory, NTSKETLSIdentity.SelfSignedFileName));
            var certOnly   = pem[..(pem.IndexOf("-----END CERTIFICATE-----", StringComparison.Ordinal) + "-----END CERTIFICATE-----".Length)];

            var downstream = await Started(
                                 new JObject(
                                     new JProperty("timeSource",  "upstream"),
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

                                 }
                             );

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

    }

}
