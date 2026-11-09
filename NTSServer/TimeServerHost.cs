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
using System.Net;
using System.Net.Sockets;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Logging;

using cloud.charging.open.NTSServer.SecureShell;

using NornNTSServer = org.GraphDefined.Vanaheimr.Norn.NTS.NTSServer;

#endregion

namespace cloud.charging.open.NTSServer
{

    /// <summary>
    /// The Norn NTS server that answers: NTS-KE over TLS 1.3 on its TCP port,
    /// NTP and NTP+NTS on its UDP port, and NTS-KE over SSH handed to it by
    /// the node's SSH server - and the one place that makes it, starts it and
    /// makes it anew.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Norn server takes the description of its clock - stratum, reference,
    /// root delay and dispersion - and its TLS identity when it is made, and
    /// keeps them. So where either changes, the server is made anew: the old
    /// one is shut down and a new one bound to the same ports. That is the
    /// only cost of it: a client that sends a cookie of the old server is told
    /// so with an NTS NAK and makes a new key exchange, which every NTS client
    /// does by itself. Which is why it is done only where a client has to see
    /// the difference - see <see cref="ClockDescription.IsSameFor"/>.
    /// </para>
    /// <para>
    /// Nothing unsynchronized is ever handed out. Where the time is not
    /// synchronized - upstream mode before its first usable round, and after
    /// the holdover - no server runs at all: a client finds nobody on the
    /// ports and asks somebody else, which is what RFC 5905's leap indicator 3
    /// would have told it to do anyway, without the client having had to make
    /// a key exchange for it first.
    /// </para>
    /// </remarks>
    public sealed class TimeServerHost : IAsyncDisposable
    {

        #region Data

        /// <summary>
        /// The UDP port of NTP, as a sentence says it.
        /// </summary>
        public static readonly NodePort  NTPPort     = new ("The NTP server");

        /// <summary>
        /// The TCP port of NTS-KE, as a sentence says it.
        /// </summary>
        public static readonly NodePort  NTSKEPort   = new ("NTS-KE over TLS");

        /// <summary>
        /// The file the master keys of the cookies are kept in, beside the TLS identity.
        /// </summary>
        public const           String    MasterKeysFileName  = "masterKeys.json";

        private readonly SemaphoreSlim            gate          = new (1, 1);
        private readonly DNSClient                dnsClient;
        private readonly EventLog                 log;
        private volatile NornNTSServer?           server;
        private          NTSServerMetrics         earlier       = new (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        private          CancellationTokenSource? retry;

        private readonly ConcurrentDictionary<String, NTSClientActivity>  sshActivity  = new (StringComparer.Ordinal);

        #endregion

        #region Properties

        /// <summary>
        /// What is listened on.
        /// </summary>
        public TimeServerSettings  Settings      { get; private set; }

        /// <summary>
        /// The clock handed out.
        /// </summary>
        public ServedClock         Clock         { get; }

        /// <summary>
        /// What the clock is described as, or null while nothing runs.
        /// </summary>
        public ClockDescription?   Description   { get; private set; }

        /// <summary>
        /// The certificate NTS-KE over TLS shows.
        /// </summary>
        public NTSKETLSIdentity    Identity      { get; private set; }

        /// <summary>
        /// The NTP server the key exchange names to its clients, or null for none.
        /// </summary>
        public URL?                AdvertisedNTP { get; private set; }

        /// <summary>
        /// Where the master keys of the cookies are kept, or null for in memory only.
        /// </summary>
        public String?             MasterKeysFile { get; }

        /// <summary>
        /// Whether a server runs and answers.
        /// </summary>
        public Boolean             IsServing
            => server is not null;

        /// <summary>
        /// When it was last made.
        /// </summary>
        public DateTimeOffset?     ServingSince  { get; private set; }

        /// <summary>
        /// How often it was made.
        /// </summary>
        public Int32               Generations   { get; private set; }

        /// <summary>
        /// What it has answered since this program started - over every server
        /// made since, not only the one running now.
        /// </summary>
        public NTSServerMetrics    Metrics
            => server?.Metrics is NTSServerMetrics now
                   ? Add(earlier, now)
                   : earlier;

        /// <summary>
        /// The key id the cookies are sealed with now, or null while nothing runs.
        /// </summary>
        public UInt64?             CurrentMasterKeyId
            => server?.CurrentMasterKeyId;

        /// <summary>
        /// The master keys of the cookies, oldest first - their ids and when
        /// they are valid, never their values -, or none while nothing runs.
        /// </summary>
        public IReadOnlyList<MasterKeyInfo>  MasterKeys
            => server?.MasterKeys ?? [];

        /// <summary>
        /// What each login made of NTS-KE over SSH since this program started,
        /// by its name - over every server made since, not only the one running
        /// now.
        /// </summary>
        public IReadOnlyDictionary<String, NTSClientActivity>  SSHActivity
            => sshActivity;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// What answers, before it does.
        /// </summary>
        /// <param name="Settings">What is listened on.</param>
        /// <param name="Clock">The clock handed out.</param>
        /// <param name="Identity">The certificate NTS-KE over TLS shows.</param>
        /// <param name="DNSClient">The node's DNS client.</param>
        /// <param name="Log">Where what happens is written.</param>
        /// <param name="MasterKeysFile">Where the master keys of the cookies are kept, in a directory that is the owner's alone; null for in memory only.</param>
        public TimeServerHost(TimeServerSettings  Settings,
                              ServedClock         Clock,
                              NTSKETLSIdentity    Identity,
                              DNSClient           DNSClient,
                              EventLog            Log,
                              String?             MasterKeysFile   = null)
        {

            this.Settings        = Settings;
            this.Clock           = Clock;
            this.Identity        = Identity;
            this.dnsClient       = DNSClient;
            this.log             = Log;
            this.AdvertisedNTP   = AdvertisedNTPOf(Settings);
            this.MasterKeysFile  = MasterKeysFile;

        }

        #endregion


        #region (static) AdvertisedNTPOf(Settings)

        /// <summary>
        /// The NTP server a key exchange names: "ntpServer" where it is given,
        /// the hostname with the NTP port where that port is not 123 - since a
        /// client that is told no port asks 123 - and nothing otherwise.
        /// </summary>
        public static URL? AdvertisedNTPOf(TimeServerSettings Settings)
        {

            String? host = null;
            IPPort? port = null;

            if (Settings.NTPServer is not null &&
                TimeServerSettings.TrySplitHostAndPort(Settings.NTPServer, out var givenHost, out var givenPort))
            {
                host = givenHost;
                port = givenPort ?? Settings.NTPPort;
            }

            else if (Settings.NTPPort != TimeServerSettings.DefaultNTPPort && Settings.Hostname is not null)
            {
                host = Settings.Hostname;
                port = Settings.NTPPort;
            }

            if (host is null)
                return null;

            var urlHost = host.Contains(':') ? $"[{host}]" : host;

            return URL.Parse($"udp://{urlHost}:{(port ?? Settings.NTPPort).ToUInt16()}");

        }

        #endregion

        #region ProbePorts()

        /// <summary>
        /// Whether both ports can be had now: bound and let go of at once, so
        /// that a start that cannot have them ends there and says why - and
        /// not a minute later, when upstream mode first has a time to hand out.
        /// </summary>
        /// <exception cref="PortUnavailableException">Where one cannot be had.</exception>
        public void ProbePorts()
        {

            var address = ListenAddress();

            Probe(address, SocketType.Dgram,  ProtocolType.Udp, Settings.NTPPort,   NTPPort);
            Probe(address, SocketType.Stream, ProtocolType.Tcp, Settings.NTSKEPort, NTSKEPort);

        }

        private static void Probe(System.Net.IPAddress  Address,
                                  SocketType            SocketType,
                                  ProtocolType          ProtocolType,
                                  IPPort                Port,
                                  NodePort              Whose)
        {

            using var socket = new Socket(Address.AddressFamily, SocketType, ProtocolType);

            if (Address.AddressFamily == AddressFamily.InterNetworkV6)
                socket.DualMode = true;

            try
            {

                socket.Bind(new IPEndPoint(Address, Port.ToUInt16()));

                if (SocketType == SocketType.Stream)
                    socket.Listen(1);

            }
            catch (SocketException problem)
            {
                throw new PortUnavailableException(Port, problem, Whose);
            }

        }

        private System.Net.IPAddress ListenAddress()

            => Settings.Listen is IPvXAddress
                   ? System.Net.IPAddress.IPv6Any
                   : Settings.Listen.ToDotNet();

        #endregion


        #region StartAsync(Description)

        /// <summary>
        /// Answer, describing the clock as given - or, where a server answers
        /// already, answer anew with the description given.
        /// </summary>
        public async Task StartAsync(ClockDescription Description)
        {

            await gate.WaitAsync();

            try
            {

                StopRetrying();

                await ShutdownCurrent();

                this.Description = Description;

                await StartOrRetry();

            }
            finally
            {
                gate.Release();
            }

        }

        #endregion

        #region ReconfigureAsync(Settings)

        /// <summary>
        /// Listen as the given settings say from now on - another address, other
        /// ports, another NTP server named in the key exchange, rate limiting on
        /// or off - by a server made anew where one is wanted.
        /// </summary>
        /// <remarks>
        /// Where the new ports cannot be had, everything stays as it was, the
        /// server that answered among it: a change that would leave nobody
        /// answering is refused rather than made. Where no server is wanted now -
        /// upstream mode before its first time - the ports are only tried.
        /// </remarks>
        /// <param name="Settings">What is listened on from now on.</param>
        /// <returns>Null where it took; why not otherwise.</returns>
        public async Task<String?> ReconfigureAsync(TimeServerSettings Settings)
        {

            await gate.WaitAsync();

            try
            {

                var before = this.Settings;

                StopRetrying();
                await ShutdownCurrent();

                this.Settings       = Settings;
                this.AdvertisedNTP  = AdvertisedNTPOf(Settings);

                String? problem     = null;

                if (Description is not null)
                    problem = await TryStart();

                else
                {
                    try
                    {
                        ProbePorts();
                    }
                    catch (PortUnavailableException unavailable)
                    {
                        problem = unavailable.Message;
                    }
                }

                if (problem is null)
                    return null;

                this.Settings       = before;
                this.AdvertisedNTP  = AdvertisedNTPOf(before);

                if (Description is not null)
                    await StartOrRetry();

                return problem;

            }
            finally
            {
                gate.Release();
            }

        }

        #endregion

        #region RenewIdentityAsync(Identity)

        /// <summary>
        /// Show another certificate from now on - which takes a server made anew,
        /// where one runs.
        /// </summary>
        public async Task RenewIdentityAsync(NTSKETLSIdentity Identity)
        {

            await gate.WaitAsync();

            try
            {

                if (Identity.Fingerprint == this.Identity.Fingerprint)
                    return;

                this.Identity = Identity;

                if (server is null || Description is null)
                    return;

                StopRetrying();
                await ShutdownCurrent();
                await StartOrRetry();

            }
            finally
            {
                gate.Release();
            }

        }

        #endregion

        #region StopAsync(Why = null)

        /// <summary>
        /// Answer nobody any more.
        /// </summary>
        /// <param name="Why">Why, for the log; nothing is said without it.</param>
        public async Task StopAsync(String? Why = null)
        {

            await gate.WaitAsync();

            try
            {

                StopRetrying();

                var wasServing = server is not null;

                await ShutdownCurrent();

                Description = null;

                if (wasServing && Why is not null)
                    log.Warning($"NTS-KE and NTP are no longer answered: {Why}", "ntsserver");

            }
            finally
            {
                gate.Release();
            }

        }

        #endregion

        #region MarkSynchronized(At)

        /// <summary>
        /// When the clock was last checked and found right, which every answer
        /// says as its reference timestamp.
        /// </summary>
        public void MarkSynchronized(DateTimeOffset At)
        {

            if (server is NornNTSServer current)
                current.ClockLastSynchronized = At;

        }

        #endregion


        #region RotateMasterKey(ForgetTheOthers = false)

        /// <summary>
        /// Seal the cookies with a new master key from now on: the earlier keys
        /// still open theirs until their grace period is over - or, with
        /// <paramref name="ForgetTheOthers"/>, never again, for a key that may
        /// have been read. Kept in the key file, so that the next server made
        /// takes the new key up. Null while nothing runs.
        /// </summary>
        public MasterKeyInfo? RotateMasterKey(Boolean ForgetTheOthers = false)

            => server?.RotateMasterKey(ForgetTheOthers);

        #endregion

        #region ServeSSHSubsystemAsync(Context, CancellationToken)

        /// <summary>
        /// One key exchange over SSH, answered by the server running now - or
        /// by nobody, where none runs: the channel is closed without an
        /// answer, as the TCP port of NTS-KE has nobody listening then.
        /// </summary>
        public ValueTask ServeSSHSubsystemAsync(SshSubsystemContext  Context,
                                                CancellationToken    CancellationToken)

            => server is NornNTSServer current
                   ? current.ServeSSHSubsystemAsync(Context, CancellationToken)
                   : ValueTask.CompletedTask;

        #endregion


        #region (private) StartOrRetry()

        /// <summary>
        /// Make a server and start it - and, where its ports cannot be had at
        /// the moment, try again every ten seconds until they can or this is
        /// stopped. At a start the ports were had a moment ago (see
        /// <see cref="ProbePorts"/>); what takes one away later is somebody
        /// else, and they may let go of it again.
        /// </summary>
        private async Task StartOrRetry()
        {

            if (await TryStart() is not String problem)
                return;

            log.Error($"NTS-KE and NTP could not be answered: {problem} Trying again every 10 seconds.", "ntsserver");

            retry = new CancellationTokenSource();
            var token = retry.Token;

            _ = Task.Run(async () => {

                while (!token.IsCancellationRequested)
                {

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    await gate.WaitAsync();

                    try
                    {

                        if (token.IsCancellationRequested || server is not null || Description is null)
                            return;

                        if (await TryStart() is null)
                            return;

                    }
                    finally
                    {
                        gate.Release();
                    }

                }

            }, token);

        }

        #endregion

        #region (private) TryStart()

        /// <summary>
        /// Make a server and start it: null where it answers, why not otherwise.
        /// </summary>
        private async Task<String?> TryStart()
        {

            if (Description is null)
                return "the clock is not described";

            var description = Description;

            var made = new NornNTSServer(
                           Description:                   I18NString.Create("The NTS server of this node"),
                           NTSKEPort:                     Settings.NTSKEPort,
                           NTSPort:                       Settings.NTPPort,
                           ExternalURLs:                  AdvertisedNTP is URL advertised ? [ advertised ] : null,
                           DNSClient:                     dnsClient,
                           ListenIPAddress:               Settings.Listen,
                           EnableDualStack:               true,

                           // Kept in a file, so that the cookies the clients hold
                           // outlive a restart of this program - and the server
                           // made anew whenever the description of the clock
                           // changes, which in upstream mode is not rare. Every
                           // server made reads the keys the one before it wrote.
                           MasterKeysFilePath:            MasterKeysFile,

                           TLSCertificate:                Identity.Certificate,
                           TLSPrivateKey:                 Identity.PrivateKey,
                           TLSServerSubjectName:          Identity.Subject,
                           Stratum:                       description.Stratum,
                           ReferenceIdentifier:           description.ReferenceId,
                           RootDelay:                     description.RootDelay,
                           RootDispersion:                description.RootDispersion,
                           LeapIndicator:                 description.LeapIndicator,
                           RateLimiter:                   Settings.RateLimit
                                                              ? new NTPRateLimiter(TimeProvider: Clock)
                                                              : null,
                           TimeProvider:                  Clock
                       );

            made.OnSSHKeyExchange += Remember;

            try
            {
                await made.Start();
            }
            catch (Exception e)
            {

                made.OnSSHKeyExchange -= Remember;

                try { await made.ShutdownAsync(); } catch { }

                return e is SocketException socketProblem
                           ? $"{socketProblem.Message} ({socketProblem.SocketErrorCode}) on UDP port {Settings.NTPPort} or TCP port {Settings.NTSKEPort}."
                           : e.Message;

            }

            server        = made;
            ServingSince  = Clock.GetUtcNow();
            Generations++;

            log.Notice($"NTS-KE is answered on TCP port {Settings.NTSKEPort} (TLS 1.3, {Identity.Subject}, SHA-256 {Identity.Fingerprint}), " +
                       $"NTP and NTP+NTS on UDP port {Settings.NTPPort}, on {TimeServerSettings.TextOf(Settings.Listen)}" +
                       (AdvertisedNTP is not null ? $"; the key exchange names {AdvertisedNTP} as the NTP server" : "") +
                       $". The clock is described as {description}.",
                       "ntsserver");

            return null;

        }

        #endregion

        #region (private) Remember(Exchange)

        /// <summary>
        /// A key exchange over SSH, for the status of its login and for the log.
        /// </summary>
        private void Remember(NTSKEOverSSHExchange Exchange)
        {

            var name = Exchange.Session.Username;

            sshActivity.AddOrUpdate(name,
                                    _        => NTSClientActivity.None.With(Exchange),
                                    (_, was) => was.With(Exchange));

            if (Exchange.Succeeded)
                log.Info  ($"NTS-KE over SSH: '{name}' from {Exchange.Session.Peer?.ToString() ?? "a tunnel"} got {Exchange.Cookies} cookie(s), " +
                           $"its keys from {NTSClientActivity.TextOf(Exchange.Method)}" +
                           (Exchange.AEADAlgorithm is AEADAlgorithms algorithm ? $", for {algorithm}" : "") + ".",
                           "ntsserver", "ssh");

            else
                log.Notice($"NTS-KE over SSH: '{name}' from {Exchange.Session.Peer?.ToString() ?? "a tunnel"} got no cookies: {Exchange.Problem}.",
                           "ntsserver", "ssh");

        }

        #endregion

        #region (private) ShutdownCurrent()

        private async Task ShutdownCurrent()
        {

            if (server is not NornNTSServer current)
                return;

            server  = null;
            earlier = Add(earlier, current.Metrics);

            current.OnSSHKeyExchange -= Remember;

            try
            {
                await current.ShutdownAsync();
            }
            catch (Exception e)
            {
                log.Warning($"The NTS server did not shut down cleanly: {e.Message}", "ntsserver");
            }

        }

        #endregion

        #region (private) StopRetrying()

        private void StopRetrying()
        {
            retry?.Cancel();
            retry?.Dispose();
            retry = null;
        }

        #endregion

        #region (private static) Add(A, B)

        private static NTSServerMetrics Add(NTSServerMetrics A, NTSServerMetrics B)

            => new (
                   A.NTPRequestsReceived      + B.NTPRequestsReceived,
                   A.NTPRequestsRejected      + B.NTPRequestsRejected,
                   A.NTPRequestsRateLimited   + B.NTPRequestsRateLimited,
                   A.NTPKissesOfDeathSent     + B.NTPKissesOfDeathSent,
                   A.NTPRequestsInvalid       + B.NTPRequestsInvalid,
                   A.NTPResponsesSent         + B.NTPResponsesSent,
                   A.NTPSignedResponsesSent   + B.NTPSignedResponsesSent,
                   A.NTPRequestFailures       + B.NTPRequestFailures,
                   A.NTSKEConnectionsAccepted + B.NTSKEConnectionsAccepted,
                   A.NTSKEConnectionsRejected + B.NTSKEConnectionsRejected,
                   A.NTSKEHandshakeFailures   + B.NTSKEHandshakeFailures,
                   A.NTSKERequestsInvalid     + B.NTSKERequestsInvalid,
                   A.NTSKEResponsesSent       + B.NTSKEResponsesSent
               );

        #endregion


        #region MetricsJSON()

        public JObject MetricsJSON()
        {

            var metrics = Metrics;

            return new JObject(
                       new JProperty("ntpRequestsReceived",       metrics.NTPRequestsReceived),
                       new JProperty("ntpResponsesSent",          metrics.NTPResponsesSent),
                       new JProperty("ntpRequestsInvalid",        metrics.NTPRequestsInvalid),
                       new JProperty("ntpRequestsRejected",       metrics.NTPRequestsRejected),
                       new JProperty("ntpRequestsRateLimited",    metrics.NTPRequestsRateLimited),
                       new JProperty("ntpKissesOfDeathSent",      metrics.NTPKissesOfDeathSent),
                       new JProperty("ntpRequestFailures",        metrics.NTPRequestFailures),
                       new JProperty("ntsKEConnectionsAccepted",  metrics.NTSKEConnectionsAccepted),
                       new JProperty("ntsKEConnectionsRejected",  metrics.NTSKEConnectionsRejected),
                       new JProperty("ntsKEHandshakeFailures",    metrics.NTSKEHandshakeFailures),
                       new JProperty("ntsKERequestsInvalid",      metrics.NTSKERequestsInvalid),
                       new JProperty("ntsKEResponsesSent",        metrics.NTSKEResponsesSent)
                   );

        }

        #endregion

        #region DisposeAsync()

        /// <summary>
        /// Answer nobody any more - as often as asked: a node is let go of by
        /// whoever made it and stopped before, and both end here.
        /// </summary>
        /// <remarks>
        /// The gate is not disposed of: a SemaphoreSlim whose wait handle was
        /// never asked for holds nothing to release, and one disposed of would
        /// make every stop after the first throw instead of doing nothing.
        /// </remarks>
        public async ValueTask DisposeAsync()

            => await StopAsync();

        #endregion

    }

}
