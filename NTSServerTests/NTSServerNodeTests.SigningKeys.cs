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
using org.GraphDefined.Vanaheimr.Norn.NTS;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.NTSServer.CommandLine;

#endregion

namespace cloud.charging.open.NTSServer.Tests
{

    /// <summary>
    /// The keys the responses are signed with: a client that asks for a signed
    /// response gets one it can check by the public key its key exchange
    /// handed it - after a restart and after the server is made anew as well -
    /// and the keys are made, handed out beforehand and retired at the prompt,
    /// over the JSON API and in the status.
    /// </summary>
    public partial class NTSServerNodeTests
    {

        #region (private) Signed(Node, KeyExchange, KeyId = null)

        /// <summary>
        /// One query of the given server asking for a signed response - by any
        /// key, or by the given one.
        /// </summary>
        private static Task<NTSQueryResult> Signed(NTSServerNode   Node,
                                                   NTSKE_Response  KeyExchange,
                                                   UInt16?         KeyId   = null)

            => ClientOf(Node).QueryTime(NTSKEResponse:        KeyExchange,
                                        SignedResponseMode:   SignedResponseMode.Scheduled,
                                        SignedResponseKeyId:  KeyId ?? NTSRequestSignedResponseExtension.AnyKey,
                                        Timeout:              TimeSpan.FromSeconds(10));

        #endregion

        #region (private) KeyExchange(Node)

        private static async Task<NTSKE_Response> KeyExchange(NTSServerNode Node)
        {

            var keyExchange = await ClientOf(Node).GetNTSKERecords(RequestNTSPublicKeys: true);

            Assert.That(keyExchange.Success, Is.True, keyExchange.ErrorMessage?.ToString());

            return keyExchange.Response!;

        }

        #endregion

        #region (private) Restarted(First)

        /// <summary>
        /// The given server stopped and started again from the same directory:
        /// on the same NTP port, the key exchange on another one.
        /// </summary>
        private async Task<NTSServerNode> Restarted(NTSServerNode First)
        {

            var configFile = First.ConfigFile.Path;
            var directory  = Path.GetDirectoryName(configFile)!;

            await First.DisposeAsync();
            nodes.Remove(First);

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

            return again;

        }

        #endregion


        #region SigningKeys_AResponseAskedToBeSignedIsSignedAndChecks()

        /// <summary>
        /// The first start makes a signing key, kept in a file only its owner
        /// may read; the key exchange hands out its public key, a response that
        /// asks for a signature carries one that checks by it, and one that
        /// asks for none carries none.
        /// </summary>
        [Test]
        public async Task SigningKeys_AResponseAskedToBeSignedIsSignedAndChecks()
        {

            var node        = await Started();
            var keyFile     = Path.Combine(node.Directory, SigningKeys.FileName);

            Assert.That(File.Exists(keyFile), Is.True);

            if (!OperatingSystem.IsWindows())
                Assert.That(File.GetUnixFileMode(keyFile), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));

            Assert.That(node.Host.SigningKeys.All.Select(key => key.Id), Is.EqualTo(new UInt16[] { 1 }));

            var keyExchange = await KeyExchange(node);

            Assert.That(keyExchange.PublicKeys.Select(key => key.Id), Is.EqualTo(new UInt16[] { 1 }));

            var signed      = await Signed(node, keyExchange);

            Assert.Multiple(() => {
                Assert.That(signed.Success,                Is.True, signed.ErrorMessage);
                Assert.That(signed.SignatureVerification,  Is.EqualTo(NTSSignatureVerification.Verified));
                Assert.That(signed.SignatureKeyId,         Is.EqualTo(1));
            });

            var unsigned    = await ClientOf(node).QueryTime(NTSKEResponse: keyExchange, Timeout: TimeSpan.FromSeconds(10));

            Assert.That(unsigned.SignatureVerification,  Is.EqualTo(NTSSignatureVerification.NotRequested));

            Assert.That(node.Host.Metrics.NTPSignedResponsesSent, Is.EqualTo(1));

        }

        #endregion

        #region SigningKeys_AreKeptAcrossARestartAndAServerMadeAnew()

        /// <summary>
        /// A client that learnt the public key before goes on checking the
        /// responses by it: after the server is made anew - the description
        /// of the clock changed - and after a restart of the program.
        /// </summary>
        [Test]
        public async Task SigningKeys_AreKeptAcrossARestartAndAServerMadeAnew()
        {

            var node        = await Started();
            var keyExchange = await KeyExchange(node);
            var publicKey   = Convert.ToBase64String(node.Host.SigningKeys.All.Single().PublicKey);

            // Made anew: another stratum.
            var generations = node.Host.Generations;

            await node.Host.StartAsync(node.Host.Description! with { Stratum = 3 });

            Assert.That(node.Host.Generations, Is.GreaterThan(generations), "the server was not made anew");

            var afterMadeAnew = await Signed(node, keyExchange);

            Assert.That(afterMadeAnew.SignatureVerification, Is.EqualTo(NTSSignatureVerification.Verified), afterMadeAnew.ErrorMessage);

            // Restarted.
            var again        = await Restarted(node);

            Assert.That(again.Host.SigningKeys.All.Select(key => Convert.ToBase64String(key.PublicKey)), Is.EqualTo(new[] { publicKey }));

            var afterRestart = await Signed(again, keyExchange);

            Assert.Multiple(() => {
                Assert.That(afterRestart.Success,                Is.True, afterRestart.ErrorMessage);
                Assert.That(afterRestart.SignatureVerification,  Is.EqualTo(NTSSignatureVerification.Verified));
                Assert.That(afterRestart.SignatureKeyId,         Is.EqualTo(1));
            });

        }

        #endregion

        #region SigningKeys_ANewKeyIsHandedOutBeforeItSigns()

        /// <summary>
        /// A new key begins a day later: until then the key exchange hands out
        /// its public key beside the one that signs, which goes on signing; a
        /// new key at once signs from now on.
        /// </summary>
        [Test]
        public async Task SigningKeys_ANewKeyIsHandedOutBeforeItSigns()
        {

            var node      = await Started();

            var next      = await node.Host.NewSigningKeyAsync();

            Assert.That(next,                                          Is.Not.Null);
            Assert.That(next!.Id,                                      Is.EqualTo(2));
            Assert.That(node.Host.SigningKeys.StateOf(next),           Is.EqualTo(SigningKeyState.Next));

            var keyExchange = await KeyExchange(node);

            Assert.That(keyExchange.PublicKeys.Select(key => key.Id),  Is.EquivalentTo(new UInt16[] { 1, 2 }));

            var signed    = await Signed(node, keyExchange);

            Assert.That(signed.SignatureVerification,  Is.EqualTo(NTSSignatureVerification.Verified), signed.ErrorMessage);
            Assert.That(signed.SignatureKeyId,         Is.EqualTo(1), "a key that has not begun signed");

            var atOnce    = await node.Host.NewSigningKeyAsync(AtOnce: true);

            Assert.That(atOnce!.Id,                                    Is.EqualTo(3));
            Assert.That(node.Host.SigningKeys.Current!.Id,             Is.EqualTo(3));

            var nowSigned = await Signed(node, await KeyExchange(node));

            Assert.That(nowSigned.SignatureVerification,  Is.EqualTo(NTSSignatureVerification.Verified), nowSigned.ErrorMessage);
            Assert.That(nowSigned.SignatureKeyId,         Is.EqualTo(3));

            // And all three kept in the file, as they are.
            var again     = await Restarted(node);

            Assert.That(again.Host.SigningKeys.All.Select(key => (key.Id, again.Host.SigningKeys.StateOf(key))),
                        Is.EqualTo(new[] { ((UInt16) 1, SigningKeyState.Valid), ((UInt16) 2, SigningKeyState.Next), ((UInt16) 3, SigningKeyState.Signs) }));

        }

        #endregion

        #region SigningKeys_ARetiredKeySignsNothing()

        /// <summary>
        /// A retired key signs nothing from now on, and its public key is no
        /// longer handed out: with no other key, no response is signed - and
        /// it is not taken back by a restart.
        /// </summary>
        [Test]
        public async Task SigningKeys_ARetiredKeySignsNothing()
        {

            var node        = await Started();
            var keyExchange = await KeyExchange(node);

            Assert.That(await node.Host.RetireSigningKeyAsync(1),  Is.Not.Null);
            Assert.That(await node.Host.RetireSigningKeyAsync(1),  Is.Null, "retired twice");
            Assert.That(await node.Host.RetireSigningKeyAsync(9),  Is.Null, "a key nobody has was retired");

            Assert.That(node.Host.SigningKeys.Current,                         Is.Null);
            Assert.That((await KeyExchange(node)).PublicKeys,                  Is.Empty);

            var asked       = await Signed(node, keyExchange);

            Assert.That(asked.Success,                Is.True, asked.ErrorMessage);
            Assert.That(asked.SignatureVerification,  Is.EqualTo(NTSSignatureVerification.NotSigned));

            var again       = await Restarted(node);

            Assert.That(again.Host.SigningKeys.StateOf(again.Host.SigningKeys.All.Single()), Is.EqualTo(SigningKeyState.Retired));
            Assert.That(again.Host.SigningKeys.Current,                                       Is.Null);

        }

        #endregion

        #region SigningKeys_AFileThatCannotBeReadIsLeftAsItIs()

        /// <summary>
        /// Signing keys that cannot be read are not replaced by new ones - the
        /// file is there to be looked at - and nothing is signed: a key whose
        /// public half the clients hold is not to be forgotten by accident.
        /// </summary>
        [Test]
        public async Task SigningKeys_AFileThatCannotBeReadIsLeftAsItIs()
        {

            var node     = await Started();
            var keyFile  = Path.Combine(node.Directory, SigningKeys.FileName);

            File.WriteAllText(keyFile, "{ not what it was");

            var again    = await Restarted(node);

            Assert.Multiple(() => {
                Assert.That(again.Host.SigningKeys.All,          Is.Empty);
                Assert.That(File.ReadAllText(keyFile),           Is.EqualTo("{ not what it was"));
            });

            Assert.That(await again.Host.NewSigningKeyAsync(),   Is.Null, "a key was added to keys that could not be read");
            Assert.That(File.ReadAllText(keyFile),               Is.EqualTo("{ not what it was"));

        }

        #endregion


        #region SigningKeys_AreMadeAndRetiredOverTheJSONAPI()

        /// <summary>
        /// The web interface's "New signing key" and "Retire": POST
        /// v1/ntsServer/signingKeys and v1/ntsServer/signingKeys/retire, for
        /// whoever may change the time server - answered with the key and the
        /// keys kept, never a private key; and the status lists them.
        /// </summary>
        [Test]
        public async Task SigningKeys_AreMadeAndRetiredOverTheJSONAPI()
        {

            var node     = await Started();
            var url      = $"{node.APIURL}v1/ntsServer/signingKeys";

            using var admin = new HttpClient();

            admin.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                                                            "Basic",
                                                            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{WWCPNode.DefaultAdminUser}:{node.GeneratedPassword}"))
                                                        );

            static StringContent JSON(String Text) => new (Text, System.Text.Encoding.UTF8, "application/json");

            // An operator, who may read the time server but not change it: nothing made.
            using var operatorHTTP = await AccountWithPassword(node, "operator1", NTSServerAccess.Operator.Name);

            Assert.That((Int32) (await operatorHTTP.PostAsync(url, JSON("{}"))).StatusCode,                       Is.EqualTo(403));
            Assert.That((Int32) (await operatorHTTP.PostAsync(url + "/retire", JSON("""{ "id": 1 }"""))).StatusCode, Is.EqualTo(403));
            Assert.That(node.Host.SigningKeys.All,  Has.Count.EqualTo(1));

            // The status, as the operator reads it.
            var status   = JObject.Parse(await operatorHTTP.GetStringAsync($"{node.APIURL}v1/ntsServer"));

            Assert.That(status["signingKeys"]![0]!["state"]!.Value<String>(), Is.EqualTo("signs"));
            Assert.That(status.ToString(), Does.Not.Contain("privateKey"), "a private key went out");

            // "atOnce" that is no true or false: nothing made.
            Assert.That((Int32) (await admin.PostAsync(url, JSON("""{ "atOnce": "yes" }"""))).StatusCode, Is.EqualTo(400));

            var made     = await admin.PostAsync(url, JSON("""{ "atOnce": false }"""));
            var madeJSON = JObject.Parse(await made.Content.ReadAsStringAsync());

            Assert.That((Int32) made.StatusCode, Is.EqualTo(200), madeJSON.ToString());
            Assert.Multiple(() => {
                Assert.That(madeJSON["made"]!["id"]!.Value<UInt16>(),       Is.EqualTo(2));
                Assert.That(madeJSON["made"]!["state"]!.Value<String>(),    Is.EqualTo("next"));
                Assert.That(((JArray) madeJSON["signingKeys"]!).Count,      Is.EqualTo(2));
                Assert.That(madeJSON.ToString(), Does.Not.Contain("privateKey"), "a private key went out");
            });

            // An id that is none, and one nobody has.
            Assert.That((Int32) (await admin.PostAsync(url + "/retire", JSON("""{ "id": 0 }"""))).StatusCode,   Is.EqualTo(400));
            Assert.That((Int32) (await admin.PostAsync(url + "/retire", JSON("""{ "id": 9 }"""))).StatusCode,   Is.EqualTo(404));

            var retired  = await admin.PostAsync(url + "/retire", JSON("""{ "id": 1 }"""));
            var retiredJSON = JObject.Parse(await retired.Content.ReadAsStringAsync());

            Assert.That((Int32) retired.StatusCode, Is.EqualTo(200), retiredJSON.ToString());
            Assert.That(retiredJSON["retired"]!["state"]!.Value<String>(), Is.EqualTo("retired"));
            Assert.That(node.Host.SigningKeys.Current, Is.Null, "key 2 begins only tomorrow");

        }

        #endregion

        #region CommandLine_SigningKeys_AreListedMadeAndRetired()

        /// <summary>
        /// 'signingKeys': the keys the responses are signed with, by their ids
        /// and public keys, never by their private keys; 'new' makes one that
        /// begins a day later, 'new now' one that signs at once, 'retire'
        /// retires one.
        /// </summary>
        [Test]
        public async Task CommandLine_SigningKeys_AreListedMadeAndRetired()
        {

            var node    = await Started();
            var key1    = Convert.ToBase64String(node.Host.SigningKeys.All.Single().PublicKey);

            var listed  = await AtTheConsole(node, "signingKeys");

            Assert.That(listed,    Has.Length.EqualTo(2));
            Assert.That(listed[0], Does.StartWith("1 signing key(s), kept in "));
            Assert.That(listed[1], Does.StartWith("        1  always to never ends  signs now  SHA-256withECDSA ").And.EndWith(key1));

            var made    = await AtTheConsole(node, "signingKeys new");

            Assert.That(made,      Has.Length.EqualTo(1));
            Assert.That(made[0],   Does.StartWith("Made the new signing key 2, which signs from ").And.EndWith(" UTC on - its public key is handed out in the key exchange until then."));

            var now     = await AtTheConsole(node, "signingKeys new now");

            Assert.That(now,       Is.EqualTo(new[] { "Made the new signing key 3, which signs from now on." }));

            listed      = await AtTheConsole(node, "signingKeys");

            Assert.That(listed[0], Does.StartWith("3 signing key(s)"));
            Assert.That(listed,    Has.Exactly(1).Matches<String>(line => line.StartsWith("        1  ") && line.Contains("signs where asked for by its id")));
            Assert.That(listed,    Has.Exactly(1).Matches<String>(line => line.StartsWith("        2  ") && line.Contains("not yet - its public key is handed out")));
            Assert.That(listed,    Has.Exactly(1).Matches<String>(line => line.StartsWith("        3  ") && line.Contains("signs now")));
            Assert.That(String.Join("\n", listed), Does.Not.Contain(Convert.ToBase64String(node.Host.SigningKeys.All[0].PrivateKey)), "a private key was shown");

            Assert.That(await AtTheConsole(node, "signingKeys retire 1"),
                        Is.EqualTo(new[] { "The signing key 1 signs nothing from now on, and its public key is no longer handed out." }));

            Assert.That(await AtTheConsole(node, "signingKeys retire 1"),
                        Is.EqualTo(new[] { "There is no signing key 1 that is not retired already." }));

            Assert.That((await AtTheConsole(node, "signingKeys retire one"))[0], Does.StartWith("'one' is no id of a signing key"));

            Assert.That(await AtTheConsole(node, "signingKeys renew"),
                        Is.EqualTo(new[] { new SigningKeysCommand(new NTSServerCLI(node)).Help() }));

            Assert.That(node.Host.SigningKeys.All, Has.Count.EqualTo(3), "a mistyped command made a key");

            Assert.That(await AtTheConsole(node, "timeServer"), Has.Some.EqualTo("signing key     3, of 3 kept - see 'signingKeys'"));

        }

        #endregion

        #region CommandLine_OverSSH_AnOperatorReadsTheSigningKeysButMakesNone()

        /// <summary>
        /// Over SSH an operator lists the signing keys, but makes none.
        /// </summary>
        [Test]
        public async Task CommandLine_OverSSH_AnOperatorReadsTheSigningKeysButMakesNone()
        {

            var node                     = await Started();
            var (client, shell, screen)  = await Shell(node, "olga", NTSServerAccess.Operator.Name);

            await using (client)
            {

                var mark = screen.Text.Length;
                await shell.WriteAsync("signingKeys\r");

                Assert.That(await screen.WaitFor("signs now", mark), Does.Contain("1 signing key(s)"));

                mark = screen.Text.Length;
                await shell.WriteAsync("signingKeys new\r");

                Assert.That(await screen.WaitFor("may not do that here", mark), Does.Contain("'olga' may not do that here: it needs the"));
                Assert.That(node.Host.SigningKeys.All, Has.Count.EqualTo(1), "an operator made a signing key");

            }

        }

        #endregion

    }

}
