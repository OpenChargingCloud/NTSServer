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

using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;

using cloud.charging.open.NTSServer.SecureShell;

#endregion

namespace cloud.charging.open.NTSServer
{

    /// <summary>
    /// One time server for a local network: a WWCP node that answers NTS-KE
    /// over TLS 1.3 and over SSH, and NTP and NTP+NTS - with the time of this
    /// machine, or with the time of a group of upstream NTS servers it follows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is this kind's own is the time server: the Norn NTS server that
    /// answers (see <see cref="TimeServerHost"/>), the clock it hands out (see
    /// <see cref="ServedClock"/>), what keeps that clock to upstream servers in
    /// upstream mode (see <see cref="UpstreamDiscipline"/>), and the machines
    /// that may make key exchanges over SSH (see <see cref="NTSClientSSHSignIns"/>).
    /// </para>
    /// <para>
    /// What every program of this family has - the sign-in, the name servers,
    /// the groups of time servers and the certificates they are held to, the
    /// certificate store, the SSH server with its command line, and the log -
    /// is the node below. The upstream servers are one of its NTS groups, so
    /// that they are written, held to their certificates and looked at where
    /// every node's time servers are. And the SSH server NTS-KE over SSH runs
    /// in is the node's, beside its command line.
    /// </para>
    /// </remarks>
    public class NTSServerNode : WWCPNode
    {

        #region Data

        /// <summary>
        /// The TCP port the web interface listens on, unless another is given.
        /// </summary>
        /// <remarks>
        /// Clear of the ports of its siblings - the vehicle's 2347, the
        /// station's 2348, the CSMS's 2351, the gateway's 2353, the PKI's 2360
        /// and everything between - so that it can run on the same bench as
        /// everything it gives the time to. Its SSH server is then on 22361.
        /// </remarks>
        public static new readonly  IPPort    DefaultHTTPPort   = IPPort.Parse(2361);

        /// <summary>
        /// What kind of node an NTS server is.
        /// </summary>
        /// <remarks>
        /// "NTSServer" after "OpenChargingCloud" in the Server header,
        /// "ntsserver-2026-11-09.log" for a day's log file, and "NTSServer" as
        /// the organization its accounts are in - which a first start writes
        /// into the accounts file and every start after it reads back, so it is
        /// the one that must never change.
        /// </remarks>
        public static readonly      NodeKind  NTSServerKind     = new (
                                                                      Name:           "NTS server",
                                                                      Tag:            "ntsserver",
                                                                      Product:        "NTSServer",
                                                                      Organization:   "NTSServer",
                                                                      LogFilePrefix:  "ntsserver"
                                                                  );

        /// <summary>
        /// The kinds of certificate the node's store keeps: the four of TLS -
        /// the roots and certificates the upstream time servers and the name
        /// servers are held to - and the TLS server identity, which NTS-KE over
        /// TLS shows where one is there for "ntske", and the web interface
        /// where the "web" section asks for one.
        /// </summary>
        public static readonly      IReadOnlyList<CertificateKind>  CertificateKinds  = [ .. CertificateKindExtensions.TLS, CertificateKind.TLSServerIdentity ];

        /// <summary>
        /// The listener a TLS server identity of the store may be told it is
        /// shown on: NTS-KE over TLS. An identity told nothing is shown on every
        /// listener, NTS-KE's among them.
        /// </summary>
        public const                String    NTSKEListener     = "ntske";

        /// <summary>
        /// The directory beside the configuration file the time server keeps its
        /// own secrets in: the self-signed TLS identity of NTS-KE, and the master
        /// keys of its cookies.
        /// </summary>
        public const                String    DefaultDirectory  = "ntsServer";

        /// <summary>
        /// The manifest resource prefix of the embedded web interface (see the
        /// EmbedFrontend target of NTSServer.csproj).
        /// </summary>
        public const                String    HTTPRoot          = "cloud.charging.open.NTSServer.HTTPRoot.";

        private Boolean ntskeOverSSHServed;

        #endregion

        #region Properties

        /// <summary>
        /// The clock handed out - and the one this node runs on.
        /// </summary>
        public ServedClock          Clock         { get; }

        /// <summary>
        /// What the "ntsServer" section and the switches of this start said.
        /// </summary>
        public TimeServerSettings   Settings      { get; private set; }

        /// <summary>
        /// The time server that answers.
        /// </summary>
        public TimeServerHost       Host          { get; }

        /// <summary>
        /// What keeps the clock to the upstream servers, or null in local mode.
        /// </summary>
        public UpstreamDiscipline?  Upstream      { get; }

        /// <summary>
        /// The machines that may make key exchanges over SSH.
        /// </summary>
        public NTSClientSSHSignIns  SSHClients    { get; }

        /// <summary>
        /// The directory the time server keeps its own secrets in.
        /// </summary>
        public String               Directory     { get; }

        /// <summary>
        /// The JSON API.
        /// </summary>
        public NTSServerHTTPAPI     API           { get; }

        /// <summary>
        /// Whether NTS-KE is served over SSH now: switched on in the section,
        /// and the node's SSH server running.
        /// </summary>
        public Boolean              NTSKEOverSSH
            => ntskeOverSSHServed && SSHEnabled;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// One NTS server, with its web interface and its command line over SSH.
        /// </summary>
        /// <param name="HTTPHostname">The address the web interface listens on; 127.0.0.1 by default.</param>
        /// <param name="HTTPPort">The TCP port it listens on; <see cref="DefaultHTTPPort"/> by default.</param>
        /// <param name="HTTPServer">An HTTP server to register within, or null to make one.</param>
        /// <param name="BasePath">What everything of this node sits below; the root by default.</param>
        /// <param name="HTTPRootPath">Where the JSON API sits; "/api" below <paramref name="BasePath"/> by default.</param>
        /// <param name="ExtAPI">An HTTPExt API to sign in against, or null for one of this node's own.</param>
        /// <param name="AccountsPath">The directory the accounts live in between starts.</param>
        /// <param name="ConfigFile">Where the configuration lives between starts.</param>
        /// <param name="DNSClient">How to resolve names, or null to make a client.</param>
        /// <param name="NTSClient">Where the node reads the time for its own check, or null to make a client.</param>
        /// <param name="Frontend">Where the web interface comes from, or null for the embedded bundle.</param>
        /// <param name="CertificatesPath">The directory the node's certificate store lives in between starts.</param>
        /// <param name="NTSServerPath">The directory the time server keeps its own secrets in; "ntsServer" beside the configuration file by default.</param>
        /// <param name="Log">Where everything that happens is written, or null to make a log.</param>
        /// <param name="LogToConsole">Whether the log is also written to the console.</param>
        /// <param name="ConsoleLogLevel">How much of it reaches the console.</param>
        /// <param name="LogPath">The directory the log files are written to, or null to write none.</param>
        /// <param name="BridgeDebugLog">Whether what the libraries below write with DebugX is picked up.</param>
        /// <param name="Clock">The clock handed out, or null to make one on the system clock.</param>
        /// <param name="SSH">What the program says about the SSH server.</param>
        /// <param name="Web">What the program says of HTTPS and of the reverse proxies in front.</param>
        /// <param name="CommandLine">What the switches of this start say over the "ntsServer" section, or null for nothing.</param>
        public NTSServerNode(IIPAddress?                                      HTTPHostname       = null,
                             IPPort?                                          HTTPPort           = null,
                             HTTPServer?                                      HTTPServer         = null,
                             HTTPPath?                                        BasePath           = null,
                             HTTPPath?                                        HTTPRootPath       = null,
                             HTTPExtAPI?                                      ExtAPI             = null,
                             String?                                          AccountsPath       = null,
                             WWCPConfigFile?                                  ConfigFile         = null,
                             DNSClient?                                       DNSClient          = null,
                             NTSClient?                                       NTSClient          = null,
                             IStaticContentSource?                            Frontend           = null,
                             String?                                          CertificatesPath   = null,
                             String?                                          NTSServerPath      = null,
                             EventLog?                                        Log                = null,
                             Boolean                                          LogToConsole       = true,
                             LogLevel                                         ConsoleLogLevel    = LogLevel.Info,
                             String?                                          LogPath            = null,
                             Boolean                                          BridgeDebugLog     = true,
                             ServedClock?                                     Clock              = null,
                             SSHSettings?                                     SSH                = null,
                             WebConfiguration?                                Web                = null,
                             Func<TimeServerSettings, TimeServerSettings>?    CommandLine        = null)

            : base(Kind:                  NTSServerKind,
                   Version:               typeof(NTSServerNode).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                   HTTPPort:              HTTPPort ?? DefaultHTTPPort,
                   HTTPHostname:          HTTPHostname,
                   HTTPServer:            HTTPServer,
                   BasePath:              BasePath,
                   HTTPRootPath:          HTTPRootPath,
                   ExtAPI:                ExtAPI,
                   AccountsPath:          AccountsPath,
                   Resources:             NTSServerAccess.Resources,
                   RoleDefinitions:       NTSServerAccess.Roles,
                   ConfigFile:            ConfigFile,
                   DNSClient:             DNSClient,
                   NTSClient:             NTSClient,
                   Frontend:              Frontend ?? new EmbeddedContentSource(HTTPRoot, typeof(NTSServerNode).Assembly),
                   CertificatesPath:      CertificatesPath,
                   CertificateKinds:      CertificateKinds,
                   CertificateListeners:  [ NTSKEListener, WebServerCertificate.Listener ],
                   Log:                   Log,
                   LogToConsole:          LogToConsole,
                   ConsoleLogLevel:       ConsoleLogLevel,
                   LogPath:               LogPath,
                   BridgeDebugLog:        BridgeDebugLog,

                   // The clock handed out is the node's clock too: its log is
                   // stamped with the time it hands out, and its own check
                   // against the legal time servers measures that time rather
                   // than the machine's below it.
                   TimeProvider:          Clock ?? new ServedClock(),
                   SSH:                   SSH,
                   Web:                   Web)

        {

            this.Clock = (ServedClock) this.TimeProvider;

            #region The "ntsServer" section, and what the switches say over it

            if (!TimeServerSettings.TryParse(ConfigurationDocument[TimeServerSettings.SectionName], out var settings, out var settingsError))
                throw new InvalidOperationException($"'{this.ConfigFile.Path}': {settingsError} Repair or remove '{this.ConfigFile.Path}' and start again.");

            this.Settings   = CommandLine is not null
                                  ? CommandLine(settings)
                                  : settings;

            this.Directory  = NTSServerPath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(this.ConfigFile.Path)) ?? ".", DefaultDirectory);

            #endregion

            #region The clock: the machine's, or a free-running one kept to upstream servers

            if (Settings.TimeSource == TimeSource.Upstream)
                this.Clock.RunFreely();

            #endregion

            #region The TLS identity NTS-KE shows

            var identity = ChooseIdentity(out var identityNote);

            this.Log.Info($"NTS-KE over TLS shows {identity.Subject} ({identity.Source}), valid until {identity.NotAfter:yyyy-MM-dd}, SHA-256 {identity.Fingerprint}." +
                          (identityNote is not null ? $" {identityNote}" : ""),
                          "ntsserver", "tls");

            if (identity.IsSelfSigned)
                this.Log.Notice($"The TLS identity of NTS-KE is self-signed: a client has to be told to trust it - by its fingerprint, or by the file '{Path.Combine(Directory, NTSKETLSIdentity.SelfSignedFileName)}' " +
                                $"as a trusted certificate (chrony: ntstrustedcerts). A certificate of a PKI for the name clients reach this server by, imported as a TLS server identity " +
                                $"(--import-certificate tlsServerIdentity=<file>) and not told to be for another listener, is shown instead.",
                                "ntsserver", "tls");

            #endregion

            #region The time server

            // The master keys of the cookies are kept beside the TLS identity, in a directory
            // nobody but the owner may enter: whoever reads them can open every cookie, and with
            // it the keys a client authenticates its time with.
            OwnerOnly.Directory(Directory, complaint => this.Log.Warning(complaint, "ntsserver"));

            this.Host = new TimeServerHost(
                            Settings,
                            this.Clock,
                            identity,
                            this.DNSClient,
                            this.Log,
                            Path.Combine(Directory, TimeServerHost.MasterKeysFileName)
                        );

            #endregion

            #region NTS-KE over SSH: the subsystem, and the machines let in for it

            this.SSHClients = new NTSClientSSHSignIns(
                                  Settings.SSHClients,
                                  NTSKE_SSH.SubsystemName,
                                  () => NTSKEOverSSH,
                                  this.Clock
                              );

            this.SSHClients.OnNotice += (level, message) => this.Log.Log(level, message, "ntsserver", "ssh", "auth");

            AddSSHSignIns(SSHClients);

            if (Settings.SSHEnabled)
                ServeKeyExchangeOverSSH();

            #endregion

            #region Upstream mode: the servers followed

            if (Settings.TimeSource == TimeSource.Upstream)
            {

                this.Upstream = new UpstreamDiscipline(
                                    Settings.Upstream,
                                    id => NTSGroups.FirstOrDefault(group => group.Id == id),
                                    this.DNSClient,
                                    this.Clock,
                                    this.Log
                                );

                Upstream.OnSynchronized   += description => Host.StartAsync(description);
                Upstream.OnRedescribed    += description => Host.StartAsync(description);
                Upstream.OnUnsynchronized += why         => Host.StopAsync(why);
                Upstream.OnMeasured       += at          => Host.MarkSynchronized(at);

                var group = NTSGroups.FirstOrDefault(group => group.Id == Settings.Upstream.Group);

                if (group is null)
                    this.Log.Error($"Upstream mode follows the NTS group '{Settings.Upstream.Group}', which this node does not have: nothing will be handed out until the \"nts\" section names it. " +
                                   $"The groups it has: {String.Join(", ", NTSGroups.Select(group => group.Id))}.",
                                   "ntsserver", "upstream");

                else
                    this.Log.Info($"Upstream mode: the time handed out follows NTS group '{group.Id}' ({group.Sources.Describe()}, at least {group.Sources.MinServers} of which must answer), " +
                                  $"asked every {Settings.Upstream.Poll.TotalSeconds:F0} s; this machine's clock is not touched.",
                                  "ntsserver", "upstream");

            }

            else
                this.Log.Info($"Local mode: the time handed out is this machine's clock, described as {ClockDescription.Local(Settings.Local)}.", "ntsserver");

            #endregion

            #region A TLS identity of the store that comes or goes

            this.Certificates.OnChanged += () => {

                var chosen = ChooseIdentity(out _);

                if (chosen.Fingerprint != Host.Identity.Fingerprint)
                {

                    this.Log.Notice($"NTS-KE over TLS shows {chosen.Subject} ({chosen.Source}) from now on, SHA-256 {chosen.Fingerprint}.", "ntsserver", "tls");

                    _ = Host.RenewIdentityAsync(chosen);

                }

            };

            #endregion

            #region The JSON API

            this.API = new NTSServerHTTPAPI(
                           HTTPServer:  this.HTTPServer,
                           Node:        this,
                           ExtAPI:      this.ExtAPI,
                           Log:         this.Log,
                           APIPath:     this.HTTPRootPath,
                           Version:     Version
                       );

            #endregion

        }

        #endregion


        #region (private) ChooseIdentity(out Note)

        /// <summary>
        /// The TLS identity of the store for NTS-KE - the newest that is usable,
        /// has an elliptic curve key and can be handed to the TLS stack - or the
        /// self-signed one kept beside the configuration file.
        /// </summary>
        private NTSKETLSIdentity ChooseIdentity(out String? Note)
        {

            Note = null;

            var passedOver = new List<String>();

            foreach (var entry in Certificates.UsableFor(CertificateKind.TLSServerIdentity, NTSKEListener).
                                               OrderByDescending(entry => entry.NotBefore))
            {

                if (!Certificates.TryLoad(entry, out var certificate, out var loadError))
                {
                    passedOver.Add($"'{entry.Label}': {loadError}");
                    continue;
                }

                using (certificate)
                {

                    if (NTSKETLSIdentity.TryFrom(certificate, $"the store's '{entry.Label}'", out var fromStore, out var error))
                    {
                        Note = passedOver.Count > 0 ? $"Passed over: {String.Join("; ", passedOver)}." : null;
                        return fromStore;
                    }

                    passedOver.Add($"'{entry.Label}': {error}");

                }

            }

            var names = new List<String>();

            if (Settings.Hostname is not null)
                names.Add(Settings.Hostname);

            names.Add(Environment.MachineName.ToLowerInvariant());
            names.Add("localhost");

            var selfSigned = NTSKETLSIdentity.SelfSigned(Directory, names, TimeProvider, out var made);

            Note = String.Join(" ", new[] {
                       passedOver.Count > 0 ? $"The store's TLS server identities were passed over: {String.Join("; ", passedOver)}." : null,
                       made is not null     ? $"It was made now: {made}."                                                          : null
                   }.Where(text => text is not null));

            if (Note.Length == 0)
                Note = null;

            return selfSigned;

        }

        #endregion

        #region (private) ServeKeyExchangeOverSSH() / StopServingKeyExchangeOverSSH()

        /// <summary>
        /// NTS-KE on the node's SSH server, answered by whichever Norn server
        /// answers now.
        /// </summary>
        /// <remarks>
        /// Not the node's ServeNTSKEOverSSH(Server), which hands every session to
        /// the one Norn server it was given: this one is made anew where the
        /// description of its clock changes, and a subsystem bound to the first
        /// would answer with the cookies of a server that is gone. The host hands
        /// every session to the one running at the time - and to nobody while
        /// none runs. Everything else is as the node does it, the label included.
        /// </remarks>
        private void ServeKeyExchangeOverSSH()
        {

            if (ntskeOverSSHServed)
                return;

            AddSSHSubsystem(new SSHSubsystem(
                                NTSKE_SSH.SubsystemName,
                                NTSKEOverSSHLabel,
                                Host.ServeSSHSubsystemAsync,
                                Permission.Run(NTSServerAccess.KeyExchange)
                            ));

            ntskeOverSSHServed = true;

        }

        private void StopServingKeyExchangeOverSSH()
        {

            if (!ntskeOverSSHServed)
                return;

            ntskeOverSSHServed = false;

            StopServingNTSKEOverSSH();

        }

        #endregion


        #region (protected override) OnListening()

        /// <summary>
        /// The ports of the time server, had before the node calls itself started:
        /// in local mode answered at once, in upstream mode once the upstream
        /// servers gave a time - and checked now either way.
        /// </summary>
        protected override async Task OnListening()
        {

            Host.ProbePorts();

            if (Settings.TimeSource == TimeSource.Local)
                await Host.StartAsync(ClockDescription.Local(Settings.Local));

        }

        #endregion

        #region (protected override) OnStarted()

        protected override Task OnStarted()
        {

            Log.Info($"The JSON API of the time server is at {APIURL}v1/ntsServer", "web", "http");

            if (Settings.SSHEnabled)
            {
                if (SSHEnabled)
                {

                    Log.Notice($"NTS-KE is served over SSH as well: {SSHURL}, subsystem {NTSKE_SSH.SubsystemName}, host key {SSHHostKey ?? "not known yet"} " +
                               $"({SSHClients.Clients.Count(client => client.Enabled && client.Keys.Count > 0)} NTS client(s) with a key, and every account allowed to run '{NTSServerAccess.KeyExchange}').",
                               "ntsserver", "ssh");

                    // The SSH server listens where the web interface does, and
                    // that is the loopback address unless the start said --any:
                    // NTS-KE over TLS and NTP reach the network, and the same
                    // key exchange over SSH would reach nobody but this machine.
                    if (SSHURL is String sshURL &&
                        (sshURL.Contains("127.0.0.1") || sshURL.Contains("[::1]") || sshURL.Contains("localhost")))
                        Log.Notice("NTS-KE over SSH reaches this machine only: the SSH server listens where the web interface does, on the loopback address. " +
                                   "Start with --any to offer it in the network.",
                                   "ntsserver", "ssh");

                }
                else
                    Log.Warning("NTS-KE over SSH is switched on, but the SSH server of this node is off - so nobody can reach it. Switch SSH on in the \"ssh\" section, or leave out --no-ssh.",
                                "ntsserver", "ssh");
            }

            Upstream?.Start();

            return Task.CompletedTask;

        }

        #endregion

        #region (protected override) OnStopping()

        protected override async Task OnStopping()
        {

            if (Upstream is not null)
                await Upstream.StopAsync();

            await Host.StopAsync();

            await base.OnStopping();

        }

        #endregion


        #region TrySetSSHClients(Clients, out Error)

        /// <summary>
        /// The machines that may make key exchanges over SSH from now on - kept
        /// in the "ssh" of the "ntsServer" section.
        /// </summary>
        public Boolean TrySetSSHClients(IEnumerable<NTSClientEntry>       Clients,
                                        [NotNullWhen(false)] out String?  Error)
        {

            var clients = Clients.ToArray();

            var updated = Settings with { SSHClients = clients };

            if (!ConfigFile.TryMergeSection(TimeServerSettings.SectionName,
                                            new JObject(new JProperty("ssh", updated.SSHToJSON())),
                                            out Error))
                return false;

            Settings = updated;
            SSHClients.Set(clients);

            // Whoever is no longer let in is ended now rather than at the next check.
            RecheckSSHSessions();

            return true;

        }

        #endregion

        #region TrySetSSHEnabled(Enabled, out Error)

        /// <summary>
        /// Serve NTS-KE over SSH from now on, or not - kept in the "ssh" of the
        /// "ntsServer" section.
        /// </summary>
        public Boolean TrySetSSHEnabled(Boolean                           Enabled,
                                        [NotNullWhen(false)] out String?  Error)
        {

            var updated = Settings with { SSHEnabled = Enabled };

            if (!ConfigFile.TryMergeSection(TimeServerSettings.SectionName,
                                            new JObject(new JProperty("ssh", updated.SSHToJSON())),
                                            out Error))
                return false;

            Settings = updated;

            if (Enabled)
                ServeKeyExchangeOverSSH();
            else
                StopServingKeyExchangeOverSSH();

            Log.Notice($"NTS-KE over SSH is switched {(Enabled ? "on" : "off")}.", "ntsserver", "ssh");

            return true;

        }

        #endregion


        #region StatusJSON()

        /// <summary>
        /// How the time server stands, as the JSON API and the command line say it.
        /// </summary>
        public JObject StatusJSON()
        {

            var now       = Clock.GetUtcNow();
            var upstream  = Upstream?.Status();

            return new JObject(
                       new JProperty("timeSource",    TimeServerSettings.TextOf(Settings.TimeSource)),
                       new JProperty("serving",       Host.IsServing),
                       new JProperty("servingSince",  Host.ServingSince?.ToString("o")),
                       new JProperty("generations",   Host.Generations),
                       new JProperty("listen",        TimeServerSettings.TextOf(Settings.Listen)),
                       new JProperty("ntsKE",         new JObject(
                           new JProperty("port",          Settings.NTSKEPort.ToUInt16()),
                           new JProperty("tls",           new JObject(
                               new JProperty("subject",       Host.Identity.Subject),
                               new JProperty("names",         new JArray(Host.Identity.Names)),
                               new JProperty("fingerprint",   Host.Identity.Fingerprint),
                               new JProperty("notAfter",      Host.Identity.NotAfter.ToString("o")),
                               new JProperty("source",        Host.Identity.Source),
                               new JProperty("selfSigned",    Host.Identity.IsSelfSigned),
                               new JProperty("pem",           Host.Identity.CertificatePEM)
                           ))
                       )),
                       new JProperty("ntp",           new JObject(
                           new JProperty("port",          Settings.NTPPort.ToUInt16()),
                           new JProperty("advertised",    Host.AdvertisedNTP?.ToString())
                       )),
                       new JProperty("ssh",           new JObject(
                           new JProperty("enabled",       Settings.SSHEnabled),
                           new JProperty("served",        NTSKEOverSSH),
                           new JProperty("subsystem",     NTSKE_SSH.SubsystemName),
                           new JProperty("url",           SSHURL),
                           new JProperty("hostKey",       SSHHostKey),
                           new JProperty("clients",       new JArray(SSHClients.Clients.Select(client => new JObject(
                                                              new JProperty("name",      client.Name),
                                                              new JProperty("enabled",   client.Enabled),
                                                              new JProperty("keys",      client.Keys.Count),
                                                              new JProperty("activity",  Host.SSHActivity.TryGetValue(client.Name, out var activity)
                                                                                             ? activity.ToJSON()
                                                                                             : NTSClientActivity.None.ToJSON())
                                                          )))),
                           // Accounts allowed to run "ntske" that made key exchanges.
                           new JProperty("accounts",      new JArray(Host.SSHActivity.
                                                                         Where  (activity => !SSHClients.Knows(activity.Key)).
                                                                         OrderBy(activity => activity.Key, StringComparer.OrdinalIgnoreCase).
                                                                         Select (activity => new JObject(
                                                                                                 new JProperty("name",      activity.Key),
                                                                                                 new JProperty("activity",  activity.Value.ToJSON())
                                                                                             ))))
                       )),
                       new JProperty("masterKeys",    new JArray(Host.MasterKeys.Select(key => new JObject(
                                                          new JProperty("id",         key.Id),
                                                          new JProperty("notBefore",  key.NotBefore.ToString("o")),
                                                          new JProperty("notAfter",   key.NotAfter. ToString("o")),
                                                          new JProperty("current",    key.IsCurrent)
                                                      )))),
                       new JProperty("clock",         new JObject(
                           new JProperty("now",           now.ToString("o")),
                           new JProperty("base",          Clock.IsFreeRunning ? "free-running" : "system"),
                           new JProperty("correctionMs",  Math.Round(Clock.Correction.TotalMilliseconds, 3)),
                           new JProperty("frequencyPPM",  Math.Round(Clock.Frequency * 1e6, 3)),
                           new JProperty("description",   Host.Description?.ToJSON())
                       )),
                       new JProperty("upstream",      upstream?.ToJSON(Clock.Base.GetUtcNow())),
                       new JProperty("metrics",       Host.MetricsJSON())
                   );

        }

        #endregion

        #region SSHJSON()

        /// <summary>
        /// NTS-KE over SSH as the NTS clients page shows it: whether it is
        /// switched on and served, where a client reaches it and what host key
        /// it holds the server to - and the NTS clients, each key with what it
        /// is: its algorithm, its fingerprint, its comment and its options,
        /// beside the line it was given as, which is what is sent back.
        /// </summary>
        public JObject SSHJSON()

            => new (
                   new JProperty("enabled",    Settings.SSHEnabled),
                   new JProperty("served",     NTSKEOverSSH),
                   new JProperty("subsystem",  NTSKE_SSH.SubsystemName),
                   new JProperty("url",        SSHURL),
                   new JProperty("hostKey",    SSHHostKey),
                   new JProperty("clients",    new JArray(Settings.SSHClients.Select(client => new JObject(
                       new JProperty("name",       client.Name),
                       new JProperty("enabled",    client.Enabled),
                       new JProperty("keys",       new JArray(client.Keys.Select(KeyJSON)))
                   ))))
               );

        private static JObject KeyJSON(String Line)
        {

            // Every line was parsed before it was taken in; one that does not
            // parse now is said as it stands, with nothing made up about it.
            if (!TimeServerSettings.IsAuthorizedKeyLine(Line, out _) ||
                !org.GraphDefined.Vanaheimr.Hermod.SSH.AuthorizedKeysFile.TryParseLine(Line, out var key) ||
                key is null)
            {
                return new JObject(new JProperty("line", Line));
            }

            return new JObject(
                       new JProperty("line",         Line),
                       new JProperty("algorithm",    key.PublicKey.Algorithm),
                       new JProperty("fingerprint",  key.PublicKey.Sha256Fingerprint),
                       new JProperty("comment",      key.PublicKey.Comment.Length > 0 ? key.PublicKey.Comment : null),
                       new JProperty("notAfter",     key.NotAfter?.ToString("o")),
                       new JProperty("options",      new JArray(key.Options)),
                       new JProperty("certAuthority",  key.IsCertAuthority),
                       new JProperty("principals",     key.Principals.Count > 0 ? new JArray(key.Principals) : null)
                   );

        }

        #endregion

        #region ConfigurationJSON()

        /// <summary>
        /// What this NTS server is, as the Configuration page reads it: what the
        /// node below says of itself, and on top the time server.
        /// </summary>
        public override JObject ConfigurationJSON()
        {

            var json = base.ConfigurationJSON();

            json.AddFirst(new JProperty("ntsServer", new JObject(
                              new JProperty("version",   Version),
                              new JProperty("createdAt", CreatedAt.ToString("o")),
                              new JProperty("machine",   Environment.MachineName),
                              new JProperty("runtime",   Environment.Version.ToString()),
                              new JProperty("os",        Environment.OSVersion.ToString()),
                              new JProperty("settings",  Settings.ToJSON()),
                              new JProperty("status",    StatusJSON())
                          )));

            return json;

        }

        #endregion


        #region DisposeAsync()

        /// <summary>
        /// The node first - which stops, and so ends the upstream servers being
        /// asked and the time being answered - and then what is left of them.
        /// </summary>
        public override async ValueTask DisposeAsync()
        {

            await base.DisposeAsync();

            if (Upstream is not null)
                await Upstream.DisposeAsync();

            await Host.DisposeAsync();

            GC.SuppressFinalize(this);

        }

        #endregion

    }

}
