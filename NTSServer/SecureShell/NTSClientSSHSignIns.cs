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

using System.Collections.Concurrent;

using org.GraphDefined.Vanaheimr.Hermod.SSH;

using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;

#endregion

namespace cloud.charging.open.NTSServer.SecureShell
{

    /// <summary>
    /// The NTS clients of the "ntsServer" section, as logins of the node's SSH
    /// server: a machine signs in under its name with one of its keys, for
    /// NTS-KE over SSH and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only the key exchange.</b> The node confines every such login to the
    /// subsystems named here - no terminal, no command line, no SFTP - and
    /// names none while NTS-KE over SSH is switched off, which is also when no
    /// NTS client is let in at all.
    /// </para>
    /// <para>
    /// <b>Its own keys, as OpenSSH holds them.</b> Each key is an
    /// authorized_keys line, so that from="192.168.1.0/24" and
    /// expiry-time="20271231" in front of it hold.
    /// </para>
    /// <para>
    /// <b>Asked again while a session lasts.</b> A client taken out of the
    /// section, switched off or left without the key it came with has its
    /// session ended - at the next of the node's checks, every few seconds.
    /// </para>
    /// </remarks>
    public sealed class NTSClientSSHSignIns : ISSHSignIns
    {

        #region Data

        /// <summary>
        /// What an NTS client is to the SSH access rules: "ntsclient:cs001".
        /// </summary>
        public const            String    Kind           = "ntsclient";

        /// <summary>
        /// How seldom one refusal of one client is said again.
        /// </summary>
        public static readonly  TimeSpan  SayAgainAfter  = TimeSpan.FromMinutes(1);

        private readonly Func<Boolean>                                 served;
        private readonly TimeProvider                                  timeProvider;
        private readonly ConcurrentDictionary<String, DateTimeOffset>  said     = [];
        private volatile IReadOnlyDictionary<String, (NTSClientEntry Entry, IReadOnlyList<AuthorizedKey> Keys)>  clients
                                                                        = new Dictionary<String, (NTSClientEntry, IReadOnlyList<AuthorizedKey>)>();

        #endregion

        #region Properties

        /// <summary>
        /// "ntsclient".
        /// </summary>
        public String  PrincipalKind
            => Kind;

        /// <summary>
        /// The subsystem the clients get.
        /// </summary>
        public String  Subsystem      { get; }

        /// <summary>
        /// The clients, as they were last set.
        /// </summary>
        public IReadOnlyList<NTSClientEntry> Clients
            => [.. clients.Values.Select(client => client.Entry).OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)];

        #endregion

        #region Events

        /// <summary>
        /// A client's key was let in no further, for a reason somebody can put
        /// right. An unknown key is not said here: the SSH server says every
        /// failed sign-in itself.
        /// </summary>
        public event Action<LogLevel, String>? OnNotice;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The given NTS clients, as SSH logins for the given subsystem.
        /// </summary>
        /// <param name="Clients">The clients.</param>
        /// <param name="Subsystem">The subsystem of NTS-KE over SSH.</param>
        /// <param name="Served">Whether NTS-KE over SSH is served now.</param>
        /// <param name="TimeProvider">The clock keys are held to.</param>
        public NTSClientSSHSignIns(IEnumerable<NTSClientEntry>  Clients,
                                   String                       Subsystem,
                                   Func<Boolean>                Served,
                                   TimeProvider?                TimeProvider = null)
        {

            this.Subsystem     = Subsystem;
            this.served        = Served;
            this.timeProvider  = TimeProvider ?? System.TimeProvider.System;

            Set(Clients);

        }

        #endregion


        #region Set(Clients)

        /// <summary>
        /// The clients from now on. Whoever is no longer among them, or no
        /// longer with the key they came with, is ended at the node's next check.
        /// </summary>
        public void Set(IEnumerable<NTSClientEntry> Clients)
        {

            var parsed = new Dictionary<String, (NTSClientEntry, IReadOnlyList<AuthorizedKey>)>(StringComparer.Ordinal);

            foreach (var client in Clients)
            {

                var keys = new List<AuthorizedKey>();

                foreach (var line in client.Keys)
                    if (AuthorizedKeysFile.TryParseLine(line, out var key) && key is not null && !key.IsCertAuthority)
                        keys.Add(key);

                parsed[client.Name] = (client, keys);

            }

            clients = parsed;

        }

        #endregion


        #region Knows(Username)

        /// <summary>
        /// Whether the name is an NTS client - let in or not.
        /// </summary>
        public Boolean Knows(String Username)

            => clients.ContainsKey(Username);

        #endregion

        #region AuthorizePublicKeyAsync(Request, CancellationToken)

        public ValueTask<Boolean> AuthorizePublicKeyAsync(SshPublicKeyAuthRequest  Request,
                                                          CancellationToken        CancellationToken = default)

            => ValueTask.FromResult(WhyNotLetIn(Request.Username, Request.PublicKeyBlob, Saying: true) is null);

        #endregion

        #region GetRestrictionsAsync(Request, CancellationToken)

        /// <summary>
        /// What the key's own line confines the session to - from= above all.
        /// The node confines it to the key exchange besides, whatever this says.
        /// </summary>
        public ValueTask<SshSessionRestrictions> GetRestrictionsAsync(SshPublicKeyAuthRequest  Request,
                                                                      CancellationToken        CancellationToken = default)

            => ValueTask.FromResult(
                   FindKey(Request.Username, Request.PublicKeyBlob)?.Restrictions
                       ?? SshSessionRestrictions.None
               );

        #endregion

        #region SubsystemsFor(Username) / GroupsOf(Username)

        /// <summary>
        /// NTS-KE over SSH while it is served, and nothing otherwise.
        /// </summary>
        public IReadOnlySet<String> SubsystemsFor(String Username)

            => served()
                   ? new HashSet<String> { Subsystem }
                   : new HashSet<String>();

        /// <summary>
        /// "ntsclients", which is what "group:ntsclients" in the SSH access
        /// rules means.
        /// </summary>
        public IReadOnlyList<String> GroupsOf(String Username)

            => [ "ntsclients" ];

        #endregion

        #region WhyNoLongerIn(Session)

        public String? WhyNoLongerIn(SshSessionInfo Session)
        {

            if (Session.PublicKeyBlob is not Byte[] key)
                return $"the NTS client '{Session.Username}' did not sign in with an SSH key";

            return WhyNotLetIn(Session.Username, key, Saying: false);

        }

        #endregion


        #region (private) FindKey(Name, PublicKeyBlob)

        private AuthorizedKey? FindKey(String  Name,
                                       Byte[]  PublicKeyBlob)
        {

            if (!clients.TryGetValue(Name, out var client))
                return null;

            var now = timeProvider.GetUtcNow();

            return client.Keys.FirstOrDefault(key => key.Matches(PublicKeyBlob) && key.IsValidAt(now));

        }

        #endregion

        #region (private) WhyNotLetIn(Name, PublicKeyBlob, Saying)

        private String? WhyNotLetIn(String   Name,
                                    Byte[]   PublicKeyBlob,
                                    Boolean  Saying)
        {

            if (!served())
                return "NTS-KE over SSH is switched off";

            if (!clients.TryGetValue(Name, out var client))
                return $"there is no NTS client '{Name}' any more";

            // An unknown key is a stranger's, and not worth more than the SSH
            // server's own line.
            if (FindKey(Name, PublicKeyBlob) is null)
                return $"the key {SshFingerprint.Sha256(PublicKeyBlob)} is not one the NTS client '{Name}' signs in with - taken out, or expired";

            if (!client.Entry.Enabled)
            {

                var why = $"the NTS client '{Name}' is switched off";

                if (Saying)
                    Say(Name, why);

                return why;

            }

            return null;

        }

        #endregion

        #region (private) Say(Name, Why)

        private void Say(String Name, String Why)
        {

            var now = timeProvider.GetUtcNow();
            var key = $"{Name}\n{Why}";

            if (said.TryGetValue(key, out var last) && now - last < SayAgainAfter)
                return;

            said[key] = now;

            OnNotice?.Invoke(LogLevel.Warning, $"An NTS client signing in over SSH was turned away: {Why}.");

        }

        #endregion

    }

}
