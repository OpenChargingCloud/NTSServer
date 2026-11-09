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

using System.Globalization;
using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;

#endregion

namespace cloud.charging.open.NTSServer
{

    /// <summary>
    /// Where the time this server hands out comes from.
    /// </summary>
    public enum TimeSource
    {

        /// <summary>
        /// The clock of this machine, as the operating system keeps it.
        /// </summary>
        Local,

        /// <summary>
        /// A group of upstream NTS servers, asked over NTS-KE (TLS 1.3) and
        /// NTP+NTS: this machine's clock is measured against them and the time
        /// handed out is corrected by what was found - the machine's own clock
        /// is never set.
        /// </summary>
        Upstream

    }


    /// <summary>
    /// What this server says of its clock in local mode, RFC 5905 § 7.3.
    /// </summary>
    /// <param name="Stratum">The stratum it reports: 1 by default, a primary server reading its reference directly.</param>
    /// <param name="ReferenceId">The four characters of its reference: "LOCL" by default, an uncalibrated local clock.</param>
    /// <param name="RootDispersion">The largest error it admits to, or null for what Norn measures: the clock's resolution and one millisecond on top.</param>
    public sealed record LocalClockSettings(Byte       Stratum         = 1,
                                            String     ReferenceId     = "LOCL",
                                            TimeSpan?  RootDispersion  = null)
    {

        /// <summary>
        /// What local mode is when the file says nothing.
        /// </summary>
        public static LocalClockSettings Default { get; } = new ();

    }


    /// <summary>
    /// How the upstream time servers are followed.
    /// </summary>
    /// <param name="Group">The NTS group of the node whose servers are followed - "legal" by default, the PTB's four unless the "nts" section says otherwise.</param>
    /// <param name="PollInterval">How often they are asked: 64 seconds by default, RFC 5905's MINPOLL.</param>
    /// <param name="StepThreshold">How far off a measurement may be before it is taken for an outlier - and, repeated, for a step: 128 ms by default, as ntpd's.</param>
    /// <param name="MaxSlew">The largest rate the time handed out is moved at to catch up: 500 ppm by default, as ntpd's and the kernel's.</param>
    /// <param name="Holdover">How long the time is still handed out as synchronized after the last usable answer: one hour by default.</param>
    public sealed record UpstreamSettings(String    Group          = UpstreamSettings.DefaultGroup,
                                          TimeSpan  PollInterval   = default,
                                          TimeSpan  StepThreshold  = default,
                                          Double    MaxSlew        = UpstreamSettings.DefaultMaxSlew,
                                          TimeSpan  Holdover       = default)
    {

        #region Data

        public const           String    DefaultGroup          = "legal";
        public const           Double    DefaultMaxSlew        = 500e-6;
        public static readonly TimeSpan  DefaultPollInterval   = TimeSpan.FromSeconds(64);
        public static readonly TimeSpan  MinPollInterval       = TimeSpan.FromSeconds(16);
        public static readonly TimeSpan  MaxPollInterval       = TimeSpan.FromSeconds(1024);
        public static readonly TimeSpan  DefaultStepThreshold  = TimeSpan.FromMilliseconds(128);
        public static readonly TimeSpan  DefaultHoldover       = TimeSpan.FromHours(1);

        #endregion

        #region Properties

        /// <summary>
        /// The poll interval actually used: the default where none was given.
        /// </summary>
        public TimeSpan  Poll
            => PollInterval  > TimeSpan.Zero ? PollInterval  : DefaultPollInterval;

        /// <summary>
        /// The step threshold actually used: the default where none was given.
        /// </summary>
        public TimeSpan  Step
            => StepThreshold > TimeSpan.Zero ? StepThreshold : DefaultStepThreshold;

        /// <summary>
        /// The holdover actually used: the default where none was given.
        /// </summary>
        public TimeSpan  HoldoverTime
            => Holdover      > TimeSpan.Zero ? Holdover      : DefaultHoldover;

        /// <summary>
        /// The largest slew rate actually used, as a fraction: 500 ppm is 0.0005.
        /// </summary>
        public Double    Slew
            => MaxSlew is > 0 and <= DefaultMaxSlew ? MaxSlew : DefaultMaxSlew;

        #endregion

        /// <summary>
        /// What upstream mode is when the file says nothing.
        /// </summary>
        public static UpstreamSettings Default { get; } = new ();

    }


    /// <summary>
    /// A machine that may make NTS key exchanges over SSH: its login name and
    /// the authorized_keys lines it signs in with.
    /// </summary>
    /// <param name="Name">The login name it signs in with.</param>
    /// <param name="Keys">Its keys, each as an authorized_keys line - options such as from="10.0.0.0/8" in front of it hold as OpenSSH holds them.</param>
    /// <param name="Enabled">Whether it is let in at all.</param>
    public sealed record NTSClientEntry(String                 Name,
                                        IReadOnlyList<String>  Keys,
                                        Boolean                Enabled = true)
    {

        public JObject ToJSON()

            => new (
                   new JProperty("name",     Name),
                   new JProperty("keys",     new JArray(Keys)),
                   new JProperty("enabled",  Enabled)
               );

    }


    /// <summary>
    /// The "ntsServer" section of the configuration file: what this time
    /// server listens on, where its time comes from, and whom it lets make
    /// key exchanges over SSH.
    /// </summary>
    /// <remarks>
    /// <code>
    /// "ntsServer": {
    ///   "listen":     "0.0.0.0",
    ///   "ntsKEPort":  4460,
    ///   "ntpPort":    123,
    ///   "hostname":   "time.lan",
    ///   "ntpServer":  "time.lan",
    ///   "timeSource": "local",
    ///   "local":      { "stratum": 1, "referenceId": "LOCL" },
    ///   "upstream":   { "group": "legal", "pollSeconds": 64 },
    ///   "rateLimit":  false,
    ///   "ssh":        { "enabled": true,
    ///                   "clients": [ { "name": "cs001", "keys": [ "ssh-ed25519 AAAA... cs001" ] } ] }
    /// }
    /// </code>
    /// Everything is optional; what is left out is the default.
    /// </remarks>
    public sealed record TimeServerSettings
    {

        #region Data

        /// <summary>
        /// The name of the section in the configuration file.
        /// </summary>
        public const           String  SectionName       = "ntsServer";

        /// <summary>
        /// The TCP port of NTS-KE, RFC 8915 § 6: 4460.
        /// </summary>
        public static readonly IPPort  DefaultNTSKEPort  = IPPort.NTSKE;

        /// <summary>
        /// The UDP port of NTP: 123.
        /// </summary>
        public static readonly IPPort  DefaultNTPPort    = IPPort.NTP;

        private static readonly HashSet<String> knownKeys = [
            "listen", "ntsKEPort", "ntpPort", "hostname", "ntpServer", "timeSource",
            "local", "upstream", "rateLimit", "ssh"
        ];

        #endregion

        #region Properties

        /// <summary>
        /// The address both listeners are bound to: every IPv4 address by
        /// default. "::" or "any" for IPv6 as well.
        /// </summary>
        public IIPAddress                     Listen       { get; init; } = IPv4Address.Any;

        /// <summary>
        /// The TCP port of NTS-KE over TLS 1.3.
        /// </summary>
        public IPPort                         NTSKEPort    { get; init; } = DefaultNTSKEPort;

        /// <summary>
        /// The UDP port of NTP and NTS.
        /// </summary>
        public IPPort                         NTPPort      { get; init; } = DefaultNTPPort;

        /// <summary>
        /// The name clients reach this server by: what the self-signed TLS
        /// identity of NTS-KE is made out to, beside the machine's own name.
        /// </summary>
        public String?                        Hostname     { get; init; }

        /// <summary>
        /// The NTP server the key exchange names to its clients, "host" or
        /// "host:port" - RFC 8915 § 4.1.7 and § 4.1.8. Without it nothing is
        /// named, and a client asks the host it made the key exchange with, on
        /// port 123; with an NTP port other than 123 and a hostname, that
        /// hostname and the port are named.
        /// </summary>
        public String?                        NTPServer    { get; init; }

        /// <summary>
        /// Where the time comes from.
        /// </summary>
        public TimeSource                     TimeSource   { get; init; } = TimeSource.Local;

        /// <summary>
        /// What is said of the clock in local mode.
        /// </summary>
        public LocalClockSettings             Local        { get; init; } = LocalClockSettings.Default;

        /// <summary>
        /// How the upstream servers are followed in upstream mode.
        /// </summary>
        public UpstreamSettings               Upstream     { get; init; } = UpstreamSettings.Default;

        /// <summary>
        /// Whether a client asking too often is answered with a RATE
        /// kiss-o'-death rather than the time - RFC 8633 § 5.4, at Norn's
        /// defaults.
        /// </summary>
        public Boolean                        RateLimit    { get; init; }

        /// <summary>
        /// Whether NTS-KE is also served over SSH, as the subsystem
        /// "ntske/1@open.charging.cloud" of the node's SSH server.
        /// </summary>
        public Boolean                        SSHEnabled   { get; init; } = true;

        /// <summary>
        /// The machines that may make key exchanges over SSH.
        /// </summary>
        public IReadOnlyList<NTSClientEntry>  SSHClients   { get; init; } = [];

        /// <summary>
        /// What this section is when the file says nothing.
        /// </summary>
        public static TimeServerSettings      Default      { get; } = new ();

        #endregion


        #region (static) TryParse(Section, out Settings, out Error)

        /// <summary>
        /// The "ntsServer" section, or the defaults where there is none.
        /// </summary>
        /// <remarks>
        /// A key nobody knows is refused rather than passed over: a typo in
        /// "timeSource" would otherwise be a server that quietly serves the
        /// local clock while somebody believes it follows the PTB.
        /// </remarks>
        /// <param name="Section">The section, or null.</param>
        /// <param name="Settings">What it says.</param>
        /// <param name="Error">Why it cannot be followed.</param>
        public static Boolean TryParse(JToken?                                      Section,
                                       [NotNullWhen(true)]  out TimeServerSettings? Settings,
                                       [NotNullWhen(false)] out String?             Error)
        {

            Settings  = null;
            Error     = null;

            if (Section is null || Section.Type == JTokenType.Null)
            {
                Settings = Default;
                return true;
            }

            if (Section is not JObject json)
            {
                Error = $"The \"{SectionName}\" section is no JSON object.";
                return false;
            }

            if (json.Properties().FirstOrDefault(property => !knownKeys.Contains(property.Name)) is JProperty unknown)
            {
                Error = $"The \"{SectionName}\" section has a key \"{unknown.Name}\" nobody knows: {String.Join(", ", knownKeys)}.";
                return false;
            }

            var settings = Default;

            #region listen

            if (json["listen"] is JToken listenToken && listenToken.Type != JTokenType.Null)
            {

                if (!TryParseListen(listenToken.Value<String>(), out var listen))
                {
                    Error = $"\"listen\" of the \"{SectionName}\" section is no address to listen on: '{listenToken}'. " +
                             "An IPv4 or IPv6 address, or \"any\" for both families.";
                    return false;
                }

                settings = settings with { Listen = listen };

            }

            #endregion

            #region ntsKEPort / ntpPort

            if (!TryParsePort(json, "ntsKEPort", out var ntsKEPort, out Error))
                return false;

            if (!TryParsePort(json, "ntpPort",   out var ntpPort,   out Error))
                return false;

            settings = settings with {
                           NTSKEPort = ntsKEPort ?? settings.NTSKEPort,
                           NTPPort   = ntpPort   ?? settings.NTPPort
                       };

            #endregion

            #region hostname / ntpServer

            var hostname   = json.Value<String>("hostname")?.Trim();
            var ntpServer  = json.Value<String>("ntpServer")?.Trim();

            if (hostname  is { Length: 0 }) hostname  = null;
            if (ntpServer is { Length: 0 }) ntpServer = null;

            if (hostname is not null && !IsHostname(hostname))
            {
                Error = $"\"hostname\" of the \"{SectionName}\" section is no host name: '{hostname}'.";
                return false;
            }

            if (ntpServer is not null && !TrySplitHostAndPort(ntpServer, out _, out _))
            {
                Error = $"\"ntpServer\" of the \"{SectionName}\" section is neither \"host\" nor \"host:port\": '{ntpServer}'.";
                return false;
            }

            settings = settings with {
                           Hostname   = hostname,
                           NTPServer  = ntpServer
                       };

            #endregion

            #region timeSource

            if (json["timeSource"] is JToken sourceToken && sourceToken.Type != JTokenType.Null)
            {

                if (!TryParseTimeSource(sourceToken.Value<String>(), out var timeSource))
                {
                    Error = $"\"timeSource\" of the \"{SectionName}\" section is \"local\" or \"upstream\", not '{sourceToken}'.";
                    return false;
                }

                settings = settings with { TimeSource = timeSource.Value };

            }

            #endregion

            #region local

            if (json["local"] is JToken localToken && localToken.Type != JTokenType.Null)
            {

                if (localToken is not JObject local)
                {
                    Error = $"\"local\" of the \"{SectionName}\" section is no JSON object.";
                    return false;
                }

                var stratum = local.Value<Int32?>("stratum") ?? LocalClockSettings.Default.Stratum;

                if (stratum is < 1 or > 15)
                {
                    Error = $"\"local\".\"stratum\" of the \"{SectionName}\" section is {stratum}, and a server that is synchronized has a stratum from 1 to 15.";
                    return false;
                }

                var referenceId = local.Value<String>("referenceId") ?? LocalClockSettings.Default.ReferenceId;

                if (referenceId.Length is < 1 or > 4 || referenceId.Any(c => c is < ' ' or > '~'))
                {
                    Error = $"\"local\".\"referenceId\" of the \"{SectionName}\" section is one to four printable ASCII characters - \"LOCL\", \"GPS\", \"PPS\" - not '{referenceId}'.";
                    return false;
                }

                var rootDispersionMs = local.Value<Double?>("rootDispersionMs");

                if (rootDispersionMs is not null && (rootDispersionMs <= 0 || rootDispersionMs > 16000))
                {
                    Error = $"\"local\".\"rootDispersionMs\" of the \"{SectionName}\" section is more than 0 and at most 16000 - never 0, which would claim a perfect clock.";
                    return false;
                }

                settings = settings with {
                               Local = new LocalClockSettings(
                                           (Byte) stratum,
                                           referenceId,
                                           rootDispersionMs.HasValue ? TimeSpan.FromMilliseconds(rootDispersionMs.Value) : null
                                       )
                           };

            }

            #endregion

            #region upstream

            if (json["upstream"] is JToken upstreamToken && upstreamToken.Type != JTokenType.Null)
            {

                if (upstreamToken is not JObject upstream)
                {
                    Error = $"\"upstream\" of the \"{SectionName}\" section is no JSON object.";
                    return false;
                }

                var group            = upstream.Value<String>("group")?.Trim() ?? UpstreamSettings.DefaultGroup;
                var pollSeconds      = upstream.Value<Double?>("pollSeconds");
                var stepThresholdMs  = upstream.Value<Double?>("stepThresholdMs");
                var maxSlewPPM       = upstream.Value<Double?>("maxSlewPPM");
                var holdoverMinutes  = upstream.Value<Double?>("holdoverMinutes");

                if (group.Length == 0)
                {
                    Error = $"\"upstream\".\"group\" of the \"{SectionName}\" section names no NTS group.";
                    return false;
                }

                if (pollSeconds is not null &&
                   (pollSeconds < UpstreamSettings.MinPollInterval.TotalSeconds || pollSeconds > UpstreamSettings.MaxPollInterval.TotalSeconds))
                {
                    Error = $"\"upstream\".\"pollSeconds\" of the \"{SectionName}\" section is from {UpstreamSettings.MinPollInterval.TotalSeconds} to {UpstreamSettings.MaxPollInterval.TotalSeconds}: " +
                             "public time servers ask not to be asked more often, and a clock asked less often has drifted too far in between.";
                    return false;
                }

                if (stepThresholdMs is not null && (stepThresholdMs < 1 || stepThresholdMs > 3600000))
                {
                    Error = $"\"upstream\".\"stepThresholdMs\" of the \"{SectionName}\" section is from 1 to 3600000.";
                    return false;
                }

                if (maxSlewPPM is not null && (maxSlewPPM <= 0 || maxSlewPPM > 500))
                {
                    Error = $"\"upstream\".\"maxSlewPPM\" of the \"{SectionName}\" section is more than 0 and at most 500.";
                    return false;
                }

                if (holdoverMinutes is not null && (holdoverMinutes < 1 || holdoverMinutes > 10080))
                {
                    Error = $"\"upstream\".\"holdoverMinutes\" of the \"{SectionName}\" section is from 1 to 10080 (a week).";
                    return false;
                }

                settings = settings with {
                               Upstream = new UpstreamSettings(
                                              group,
                                              pollSeconds.HasValue      ? TimeSpan.FromSeconds     (pollSeconds.    Value) : default,
                                              stepThresholdMs.HasValue  ? TimeSpan.FromMilliseconds(stepThresholdMs.Value) : default,
                                              maxSlewPPM.HasValue       ? maxSlewPPM.Value * 1e-6                          : UpstreamSettings.DefaultMaxSlew,
                                              holdoverMinutes.HasValue  ? TimeSpan.FromMinutes     (holdoverMinutes.Value) : default
                                          )
                           };

            }

            #endregion

            #region rateLimit

            if (json["rateLimit"] is JToken rateToken && rateToken.Type != JTokenType.Null)
            {

                if (rateToken.Type != JTokenType.Boolean)
                {
                    Error = $"\"rateLimit\" of the \"{SectionName}\" section is true or false.";
                    return false;
                }

                settings = settings with { RateLimit = rateToken.Value<Boolean>() };

            }

            #endregion

            #region ssh

            if (json["ssh"] is JToken sshToken && sshToken.Type != JTokenType.Null)
            {

                if (sshToken is not JObject ssh)
                {
                    Error = $"\"ssh\" of the \"{SectionName}\" section is no JSON object.";
                    return false;
                }

                var enabled  = ssh.Value<Boolean?>("enabled") ?? true;
                var clients  = new List<NTSClientEntry>();

                if (ssh["clients"] is JToken clientsToken && clientsToken.Type != JTokenType.Null)
                {

                    if (clientsToken is not JArray clientsArray)
                    {
                        Error = $"\"ssh\".\"clients\" of the \"{SectionName}\" section is a list of {{ \"name\", \"keys\" }}.";
                        return false;
                    }

                    foreach (var clientToken in clientsArray)
                    {

                        if (!TryParseClient(clientToken, out var client, out Error))
                            return false;

                        if (clients.Any(known => String.Equals(known.Name, client.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            Error = $"The NTS client '{client.Name}' is listed twice in \"ssh\".\"clients\" of the \"{SectionName}\" section.";
                            return false;
                        }

                        clients.Add(client);

                    }

                }

                settings = settings with {
                               SSHEnabled  = enabled,
                               SSHClients  = clients
                           };

            }

            #endregion

            Settings = settings;
            return true;

        }

        #endregion

        #region (static) TryParseClient(JSON, out Client, out Error)

        /// <summary>
        /// One NTS client: { "name": "cs001", "keys": [ "ssh-ed25519 AAAA..." ], "enabled": true }.
        /// </summary>
        public static Boolean TryParseClient(JToken?                                  JSON,
                                             [NotNullWhen(true)]  out NTSClientEntry? Client,
                                             [NotNullWhen(false)] out String?         Error)
        {

            Client = null;

            if (JSON is not JObject json)
            {
                Error = $"An entry of \"ssh\".\"clients\" of the \"{SectionName}\" section is no JSON object.";
                return false;
            }

            var name = json.Value<String>("name")?.Trim() ?? "";

            if (!IsClientName(name))
            {
                Error = $"'{name}' can not name an NTS client: letters, digits and . _ @ + -, not beginning with a dot, at most 128 characters.";
                return false;
            }

            var keys = new List<String>();

            if (json["keys"] is JArray keysArray)
            {
                foreach (var keyToken in keysArray)
                {

                    var line = keyToken.Value<String>()?.Trim() ?? "";

                    if (!IsAuthorizedKeyLine(line, out var why))
                    {
                        Error = $"A key of the NTS client '{name}' is no authorized_keys line: {why}";
                        return false;
                    }

                    keys.Add(line);

                }
            }

            else if (json["keys"] is JToken other && other.Type != JTokenType.Null)
            {
                Error = $"\"keys\" of the NTS client '{name}' is a list of authorized_keys lines.";
                return false;
            }

            Client = new NTSClientEntry(
                         name,
                         keys,
                         json.Value<Boolean?>("enabled") ?? true
                     );

            Error  = null;
            return true;

        }

        #endregion


        #region ToJSON()

        /// <summary>
        /// The section as the configuration file has it - every value, the
        /// defaults included, so that whoever reads the file sees what is in
        /// effect.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("listen",      TextOf(Listen)),
                   new JProperty("ntsKEPort",   NTSKEPort.ToUInt16()),
                   new JProperty("ntpPort",     NTPPort.  ToUInt16()),
                   new JProperty("hostname",    Hostname),
                   new JProperty("ntpServer",   NTPServer),
                   new JProperty("timeSource",  TextOf(TimeSource)),
                   new JProperty("local",       new JObject(
                       new JProperty("stratum",            Local.Stratum),
                       new JProperty("referenceId",        Local.ReferenceId),
                       new JProperty("rootDispersionMs",   Local.RootDispersion?.TotalMilliseconds)
                   )),
                   new JProperty("upstream",    new JObject(
                       new JProperty("group",              Upstream.Group),
                       new JProperty("pollSeconds",        Upstream.Poll.TotalSeconds),
                       new JProperty("stepThresholdMs",    Upstream.Step.TotalMilliseconds),
                       new JProperty("maxSlewPPM",         Math.Round(Upstream.Slew * 1e6, 3)),
                       new JProperty("holdoverMinutes",    Upstream.HoldoverTime.TotalMinutes)
                   )),
                   new JProperty("rateLimit",   RateLimit),
                   new JProperty("ssh",         SSHToJSON())
               );

        /// <summary>
        /// The "ssh" of the section: whether NTS-KE is served over SSH, and to whom.
        /// </summary>
        public JObject SSHToJSON()

            => new (
                   new JProperty("enabled",  SSHEnabled),
                   new JProperty("clients",  new JArray(SSHClients.Select(client => client.ToJSON())))
               );

        #endregion

        #region Flat()

        /// <summary>
        /// Every setting of the section but "ssh", each at its key - "ntpPort",
        /// "upstream.group" - and with the value the file has for it.
        /// </summary>
        public IReadOnlyDictionary<String, JToken> Flat()
        {

            var flat = new Dictionary<String, JToken>();

            foreach (var property in ToJSON().Properties())
            {

                if (property.Name == "ssh")
                    continue;

                if (property.Value is JObject inner)
                    foreach (var innerProperty in inner.Properties())
                        flat[$"{property.Name}.{innerProperty.Name}"] = innerProperty.Value;

                else
                    flat[property.Name] = property.Value;

            }

            return flat;

        }

        #endregion

        #region (static) Differences(A, B)

        /// <summary>
        /// The settings - "ssh" aside - that are not the same in both, each at
        /// its key in <see cref="Flat"/>.
        /// </summary>
        public static IReadOnlyList<String> Differences(TimeServerSettings  A,
                                                        TimeServerSettings  B)
        {

            var a = A.Flat();
            var b = B.Flat();

            return [ .. a.Keys.Where(key => !b.TryGetValue(key, out var other) || !JToken.DeepEquals(a[key], other)) ];

        }

        #endregion


        #region Helpers

        /// <summary>
        /// What "listen" says: an address, or "any" for every address of both
        /// families.
        /// </summary>
        public static Boolean TryParseListen(String?                                Text,
                                             [NotNullWhen(true)] out IIPAddress?    Address)
        {

            Address = null;

            var text = Text?.Trim() ?? "";

            if (text.Equals("any", StringComparison.OrdinalIgnoreCase) || text == "::")
            {
                Address = IPvXAddress.Any;
                return true;
            }

            return org.GraphDefined.Vanaheimr.Hermod.IPAddress.TryParse(text, out Address);

        }

        /// <summary>
        /// "local" or "upstream".
        /// </summary>
        public static Boolean TryParseTimeSource(String? Text, [NotNullWhen(true)] out TimeSource? Source)
        {

            Source = (Text?.Trim().ToLowerInvariant()) switch {
                         "local"     => TimeSource.Local,
                         "upstream"  => TimeSource.Upstream,
                         _           => null
                     };

            return Source.HasValue;

        }

        public static String TextOf(TimeSource Source)

            => Source == TimeSource.Upstream ? "upstream" : "local";

        public static String TextOf(IIPAddress Address)

            => Address is IPvXAddress ? "any" : Address.ToString() ?? "";

        /// <summary>
        /// Whether the given text names a host: a DNS name or an address.
        /// </summary>
        public static Boolean IsHostname(String Text)

            => Text.Length is > 0 and <= 253 &&
               (org.GraphDefined.Vanaheimr.Hermod.IPAddress.TryParse(Text.Trim('[', ']'), out _) ||
                Text.Split('.').All(label => label.Length is > 0 and <= 63 &&
                                             label.All(c => Char.IsAsciiLetterOrDigit(c) || c == '-') &&
                                             label[0] != '-' && label[^1] != '-'));

        /// <summary>
        /// "host" or "host:port" - an IPv6 address in brackets where a port follows it.
        /// </summary>
        public static Boolean TrySplitHostAndPort(String                              Text,
                                                  [NotNullWhen(true)] out String?     Host,
                                                  out IPPort?                         Port)
        {

            Host = null;
            Port = null;

            var text = Text.Trim();

            if (text.StartsWith('['))
            {

                var close = text.IndexOf(']');

                if (close < 0)
                    return false;

                Host = text[1..close];
                var rest = text[(close + 1)..];

                if (rest.Length > 0)
                {
                    if (!rest.StartsWith(':') || !UInt16.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var bracketPort) || bracketPort == 0)
                        return false;
                    Port = IPPort.Parse(bracketPort);
                }

                return IsHostname(Host);

            }

            var colons = text.Count(c => c == ':');

            if (colons == 1)
            {

                var parts = text.Split(':');

                if (!UInt16.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port == 0)
                    return false;

                Host = parts[0];
                Port = IPPort.Parse(port);

                return IsHostname(Host);

            }

            // No colon, or an IPv6 address without brackets and so without a port.
            Host = text;
            return IsHostname(Host);

        }

        /// <summary>
        /// What a login name of an NTS client may be: what the node's accounts
        /// may be called.
        /// </summary>
        public static Boolean IsClientName(String Name)

            => protocols.WWCP.Node.SecureShell.AuthorizedKeysStore.IsAccountName(Name);

        /// <summary>
        /// Whether the line is one authorized_keys line Hermod can hold a key to:
        /// a client's own key, or with cert-authority the key of an SSH CA whose
        /// user certificates let the client in.
        /// </summary>
        public static Boolean IsAuthorizedKeyLine(String                            Line,
                                                  [NotNullWhen(false)] out String?  Why)
        {

            if (Line.Length == 0 || Line.Contains('\n'))
            {
                Why = "it is empty, or more than one line.";
                return false;
            }

            if (!org.GraphDefined.Vanaheimr.Hermod.SSH.AuthorizedKeysFile.TryParseLine(Line, out var entry) || entry is null)
            {
                Why = $"'{(Line.Length > 40 ? Line[..40] + "..." : Line)}' could not be read as one.";
                return false;
            }

            Why = null;
            return true;

        }

        private static Boolean TryParsePort(JObject                           JSON,
                                            String                            Key,
                                            out IPPort?                       Port,
                                            [NotNullWhen(false)] out String?  Error)
        {

            Port  = null;
            Error = null;

            if (JSON[Key] is not JToken token || token.Type == JTokenType.Null)
                return true;

            if (token.Type != JTokenType.Integer || token.Value<Int64>() is < 1 or > 65535)
            {
                Error = $"\"{Key}\" of the \"{SectionName}\" section is a port from 1 to 65535, not '{token}'.";
                return false;
            }

            Port = IPPort.Parse(token.Value<Int32>());
            return true;

        }

        #endregion

    }

}
