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

using System.Text;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;

using cloud.charging.open.protocols.WWCP.Node;

using cloud.charging.open.NTSServer.CommandLine;

#endregion

namespace cloud.charging.open.NTSServer.Tests
{

    /// <summary>
    /// The NTS server's command line - timeServer, masterKeys and ntsClients
    /// beside the commands every node has - typed at the console of a server
    /// that no program of its own runs, and over SSH, where the node hands it
    /// out by itself.
    /// </summary>
    public partial class NTSServerNodeTests
    {

        #region (private static) AtTheConsole(Node, Line)

        /// <summary>
        /// The given line, typed at the console of the given NTS server: found
        /// among the commands as the console finds it, and run.
        /// </summary>
        private static async Task<String[]> AtTheConsole(NTSServerNode  Node,
                                                         String         Line)
        {

            using var cli    = new NTSServerCLI(Node);
            var       lines  = await cli.Execute(Line);

            TestContext.Out.WriteLine($"> {Line}");
            TestContext.Out.WriteLine(String.Join(Environment.NewLine, lines));

            return lines;

        }

        #endregion

        #region (private sealed class) Screen

        /// <summary>
        /// What a shell over SSH writes, as it arrives.
        /// </summary>
        private sealed class Screen
        {

            private readonly StringBuilder  received  = new ();
            private readonly Lock           padlock   = new ();

            public Screen(SshClientShell Shell)

                => _ = Task.Run(async () => {

                       var buffer = new Byte[4096];

                       try
                       {
                           Int32 read;
                           while ((read = await Shell.Output.ReadAsync(buffer)) > 0)
                               lock (padlock) { received.Append(Encoding.UTF8.GetString(buffer, 0, read)); }
                       }
                       catch { }

                   });

            public String Text
            {
                get { lock (padlock) { return received.ToString(); } }
            }

            /// <summary>
            /// Wait until the given text was written after what was there when
            /// Since was taken, and return what was written since.
            /// </summary>
            public async Task<String> WaitFor(String What, Int32 Since = 0, Int32 Seconds = 10)
            {

                var until = DateTime.UtcNow.AddSeconds(Seconds);

                while (DateTime.UtcNow < until)
                {

                    var text = Text;

                    if (text.IndexOf(What, Math.Min(Since, text.Length), StringComparison.Ordinal) >= 0)
                        return text[Math.Min(Since, text.Length)..];

                    await Task.Delay(20);

                }

                Assert.Fail($"Waited in vain for '{What}'. The session said:\n{Text}");
                return "";

            }

        }

        #endregion

        #region (private static) Shell(Node, Account, Role = null)

        /// <summary>
        /// A shell over SSH, signed in as the given account - made, in the given
        /// role, where it is not root - with a key of its own, at its prompt.
        /// </summary>
        private static async Task<(SshClient Client, SshClientShell Shell, Screen Screen)> Shell(NTSServerNode  Node,
                                                                                                 String         Account,
                                                                                                 String?        Role = null)
        {

            if (Role is not null)
                (await AccountWithPassword(Node, Account, Role)).Dispose();

            var key    = SshHostKey.GenerateEd25519();
            var added  = await Node.AuthorizeSSHKey(Account, SshPublicKey.FromHostKey(key).ToAuthorizedKeyLine());

            Assert.That(added.IsAdded, Is.True, added.Reason);

            var client = await ConnectOverSSH(Node, Account, key);
            var shell  = await client.OpenShellAsync(new SshPty("xterm", new SshWindowSize(160, 40), new Dictionary<Byte, UInt32>()));
            var screen = new Screen(shell);

            await screen.WaitFor($"nts:{Node.HTTPPort}> ");

            return (client, shell, screen);

        }

        #endregion


        #region CommandLine_TimeServer_SaysHowTheTimeServerStands()

        /// <summary>
        /// 'timeServer': where the time comes from, whether it is answered, what
        /// is listened on, and how much was answered - and 'sync' says there is
        /// nobody to ask where the time is this machine's.
        /// </summary>
        [Test]
        public async Task CommandLine_TimeServer_SaysHowTheTimeServerStands()
        {

            var node   = await Started();
            var lines  = await AtTheConsole(node, "timeServer");

            Assert.Multiple(() => {
                Assert.That(lines, Has.Some.EqualTo("time source     this machine's clock"));
                Assert.That(lines, Has.Some.StartsWith("answering       yes, since "));
                Assert.That(lines, Has.Some.StartsWith($"NTS-KE (TLS)    TCP 127.0.0.1:{node.Settings.NTSKEPort}, "));
                Assert.That(lines, Has.Some.EqualTo($"                SHA-256 {node.Host.Identity.Fingerprint}"));
                Assert.That(lines, Has.Some.StartsWith($"NTP             UDP 127.0.0.1:{node.Settings.NTPPort}"));
                Assert.That(lines, Has.Some.StartsWith($"master key      {node.Host.CurrentMasterKeyId}, of 1 kept"));
                Assert.That(lines, Has.Some.EqualTo("signing key     1, of 1 kept - see 'signingKeys'"));
                Assert.That(lines, Has.None.StartsWith("upstream"), "a server in local mode follows nobody");
            });

            Assert.That(await AtTheConsole(node, "timeServer sync"),
                        Is.EqualTo(new[] { "This NTS server hands out the time of this machine: there are no upstream servers to ask." }));

            Assert.That(await AtTheConsole(node, "timeServer now"),
                        Is.EqualTo(new[] { new TimeServerCommand(new NTSServerCLI(node)).Help() }));

        }

        #endregion

        #region CommandLine_TimeServerSync_AsksTheUpstreamServersNow()

        /// <summary>
        /// 'timeServer sync' of a server in upstream mode: a round asked now,
        /// and said as 'timeServer' says how far it is from them.
        /// </summary>
        [Test]
        public async Task CommandLine_TimeServerSync_AsksTheUpstreamServersNow()
        {

            var upstream    = await Started(new JObject(new JProperty("local", new JObject(new JProperty("stratum", 1), new JProperty("referenceId", "GPS")))));
            var downstream  = await StartedFollowing(upstream);
            var polls       = downstream.Upstream!.Status().Polls;

            var lines       = await AtTheConsole(downstream, "timeServer sync");

            Assert.Multiple(() => {
                Assert.That(lines[0], Does.StartWith("upstream        synchronized since ").
                                      And.EndWith(", closest to localhost (stratum 1)"));
                Assert.That(lines,    Has.Some.Matches<String>(line => line.Contains("1 of 1 answered")));
                Assert.That(downstream.Upstream.Status().Polls, Is.GreaterThan(polls), "no round was asked");
            });

            var status      = await AtTheConsole(downstream, "timeServer");

            Assert.That(status, Has.Some.EqualTo("time source     upstream, NTS group 'legal'"));
            Assert.That(status, Has.Some.StartsWith("upstream        synchronized since "));

        }

        #endregion

        #region CommandLine_MasterKeys_AreListedRotatedAndForgotten()

        /// <summary>
        /// 'masterKeys': the keys the cookies are sealed with, by their ids and
        /// never by their values; 'rotate' seals with a new one, 'rotate forget'
        /// forgets the others as well.
        /// </summary>
        [Test]
        public async Task CommandLine_MasterKeys_AreListedRotatedAndForgotten()
        {

            var node    = await Started();
            var first   = node.Host.CurrentMasterKeyId!.Value;

            var listed  = await AtTheConsole(node, "masterKeys");

            Assert.That(listed,    Has.Length.EqualTo(2));
            Assert.That(listed[0], Does.StartWith("1 master key(s), kept in "));
            Assert.That(listed[1], Does.StartWith($"    {first,6}  ").And.EndWith("seals the cookies made now"));

            var rotated = await AtTheConsole(node, "masterKeys rotate");
            var second  = node.Host.CurrentMasterKeyId!.Value;

            Assert.That(second,  Is.Not.EqualTo(first));
            Assert.That(rotated, Is.EqualTo(new[] { $"The cookies are sealed with the new master key {second} from now on. The earlier keys still " +
                                                     "open their cookies until their grace period is over." }));

            listed      = await AtTheConsole(node, "masterKeys");

            Assert.That(listed,    Has.Length.EqualTo(3));
            Assert.That(listed[0], Does.StartWith("2 master key(s)"));
            Assert.That(listed,    Has.Exactly(1).Matches<String>(line => line.EndsWith("seals the cookies made now") && line.Contains($" {second} ")));
            Assert.That(listed,    Has.Exactly(1).Matches<String>(line => line.EndsWith("opens the cookies it sealed") && line.Contains($" {first} ")));

            var forgot  = await AtTheConsole(node, "masterKeys rotate forget");

            Assert.That(forgot[0],             Does.Contain("every earlier key is forgotten"));
            Assert.That(node.Host.MasterKeys,  Has.Count.EqualTo(1));

            Assert.That(await AtTheConsole(node, "masterKeys rotate everything"),
                        Is.EqualTo(new[] { new MasterKeysCommand(new NTSServerCLI(node)).Help() }));

            Assert.That(node.Host.MasterKeys,  Has.Count.EqualTo(1), "a mistyped rotation rotated");

        }

        #endregion

        #region CommandLine_NTSClients_AreLetInSwitchedAndTakenOut()

        /// <summary>
        /// 'ntsClients': a machine let in for NTS-KE over SSH with a key, and
        /// one more, switched off and on, a key of it taken out and then the
        /// machine - each written to the configuration file at once - and the
        /// key exchange over SSH switched off and on.
        /// </summary>
        [Test]
        public async Task CommandLine_NTSClients_AreLetInSwitchedAndTakenOut()
        {

            var node   = await Started();
            var key1   = SshPublicKey.FromHostKey(SshHostKey.GenerateEd25519(), "cs001");
            var key2   = SshPublicKey.FromHostKey(SshHostKey.GenerateEd25519(), "cs001-spare");

            Assert.That(await AtTheConsole(node, "ntsClients"),
                        Has.Some.StartsWith("No NTS client yet: ntsClients add <name> <authorized_keys line>."));

            Assert.That(await AtTheConsole(node, $"ntsClients add cs001 {key1.ToAuthorizedKeyLine()}"),
                        Is.EqualTo(new[] { $"The NTS client 'cs001' may make key exchanges over SSH with {key1.Sha256Fingerprint} (ssh-ed25519)." }));

            Assert.That(await AtTheConsole(node, $"ntsClients add cs001 {key2.ToAuthorizedKeyLine()}"),
                        Is.EqualTo(new[] { $"The NTS client 'cs001' may sign in with {key2.Sha256Fingerprint} (ssh-ed25519) as well." }));

            Assert.That(await AtTheConsole(node, $"ntsClients add cs001 {key2.ToAuthorizedKeyLine()}"),
                        Is.EqualTo(new[] { "The NTS client 'cs001' has that key already." }));

            Assert.That(await AtTheConsole(node, "ntsClients add .cs002 ssh-ed25519 AAAA"),
                        Has.Exactly(1).StartsWith("'.cs002' can not name an NTS client"));

            var listed = await AtTheConsole(node, "ntsClients");

            Assert.Multiple(() => {
                Assert.That(listed, Has.Some.EqualTo("cs001: 2 key(s)"));
                Assert.That(listed, Has.Some.EqualTo($"    {key1.Sha256Fingerprint} (ssh-ed25519)"));
                Assert.That(listed, Has.Some.EqualTo("    no key exchange since the start"));
                Assert.That(node.Settings.SSHClients.Single().Keys, Has.Count.EqualTo(2));
            });

            Assert.That(await AtTheConsole(node, "ntsClients disable cs001"), Is.EqualTo(new[] { "The NTS client 'cs001' is switched off." }));
            Assert.That(node.Settings.SSHClients.Single().Enabled, Is.False);
            Assert.That(await AtTheConsole(node, "ntsClients"),               Has.Some.EqualTo("cs001 (switched off): 2 key(s)"));

            Assert.That(await AtTheConsole(node, "ntsClients enable cs001"),  Is.EqualTo(new[] { "The NTS client 'cs001' is switched on." }));
            Assert.That(node.Settings.SSHClients.Single().Enabled, Is.True);

            // A key by the beginning of its fingerprint, with or without "SHA256:".
            var fingerprint = key2.Sha256Fingerprint["SHA256:".Length..][..10];

            Assert.That(await AtTheConsole(node, $"ntsClients remove cs001 {fingerprint}"),
                        Is.EqualTo(new[] { $"The NTS client 'cs001' no longer signs in with {key2.Sha256Fingerprint} (ssh-ed25519)." }));
            Assert.That(node.Settings.SSHClients.Single().Keys, Is.EqualTo(new[] { key1.ToAuthorizedKeyLine() }));

            Assert.That(await AtTheConsole(node, "ntsClients remove cs001"),  Is.EqualTo(new[] { "The NTS client 'cs001' is taken out." }));
            Assert.That(await AtTheConsole(node, "ntsClients remove cs001"),  Is.EqualTo(new[] { "There is no NTS client 'cs001'." }));
            Assert.That(node.Settings.SSHClients, Is.Empty);
            Assert.That(File.ReadAllText(node.ConfigFile.Path), Does.Not.Contain("cs001"));

            Assert.That(await AtTheConsole(node, "ntsClients ssh off"),       Is.EqualTo(new[] { "NTS-KE is no longer served over SSH." }));
            Assert.That(node.NTSKEOverSSH, Is.False);

            Assert.That(await AtTheConsole(node, "ntsClients ssh on"),        Is.EqualTo(new[] { "NTS-KE is served over SSH from now on." }));
            Assert.That(node.NTSKEOverSSH, Is.True);

        }

        #endregion

        #region CommandLine_OverSSH_IsTheNTSServersOwn()

        /// <summary>
        /// Whoever signs in over SSH gets the NTS server's command line - its
        /// prompt, and timeServer beside the node's commands - from the node
        /// itself: no program runs this server, and nobody set its command
        /// line.
        /// </summary>
        [Test]
        public async Task CommandLine_OverSSH_IsTheNTSServersOwn()
        {

            var node                     = await Started();
            var (client, shell, screen)  = await Shell(node, WWCPNode.DefaultAdminUser);

            await using (client)
            {

                var mark = screen.Text.Length;
                await shell.WriteAsync("timeServer\r");

                var said = await screen.WaitFor($"nts:{node.HTTPPort}> ", mark);

                Assert.That(said, Does.Contain("time source     this machine's clock").
                                  And.Contain($"SHA-256 {node.Host.Identity.Fingerprint}"));

            }

        }

        #endregion

        #region CommandLine_OverSSH_AnOperatorMayLookButNotChange()

        /// <summary>
        /// Over SSH a command asks what the account may do, as the web
        /// interface asks: an operator reads the master keys, but does not
        /// rotate them - and nothing is rotated.
        /// </summary>
        [Test]
        public async Task CommandLine_OverSSH_AnOperatorMayLookButNotChange()
        {

            var node                     = await Started();
            var before                   = node.Host.CurrentMasterKeyId;
            var (client, shell, screen)  = await Shell(node, "olga", NTSServerAccess.Operator.Name);

            await using (client)
            {

                var mark = screen.Text.Length;
                await shell.WriteAsync("masterKeys\r");

                Assert.That(await screen.WaitFor("seals the cookies made now", mark), Does.Contain("1 master key(s)"));

                mark = screen.Text.Length;
                await shell.WriteAsync("masterKeys rotate\r");

                Assert.That(await screen.WaitFor("may not do that here", mark), Does.Contain("'olga' may not do that here: it needs the"));
                Assert.That(node.Host.CurrentMasterKeyId, Is.EqualTo(before), "an operator rotated the master key");

            }

        }

        #endregion

    }

}
