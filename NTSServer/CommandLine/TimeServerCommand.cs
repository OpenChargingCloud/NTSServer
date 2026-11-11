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

using org.GraphDefined.Vanaheimr.CLI;
using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.NTSServer.CommandLine
{

    /// <summary>
    /// How the time server stands: what it listens on, where its time comes
    /// from, how far that was from the upstream servers, and how much it
    /// answered - or, with "sync", the upstream servers asked now.
    /// </summary>
    /// <param name="CLI">The command line of the NTS server to ask.</param>
    public class TimeServerCommand(NTSServerCLI CLI) : ACLICommand<NTSServerCLI>(CLI),
                                                       ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as: "TimeServerCommand" without its last
        /// seven characters.
        /// </summary>
        public static readonly String CommandName = nameof(TimeServerCommand)[..^7].ToLowerFirstChar();

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)
        {

            if (Arguments.Length == 1)
                return CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                           ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                           : [];

            if (Arguments.Length == 2 && "sync".StartsWith(Arguments[1], StringComparison.OrdinalIgnoreCase))
                return [ SuggestionResponse.ParameterCompleted($"{CommandName} sync") ];

            return [];

        }

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override async Task<String[]> Execute(String[]           Arguments,
                                                     CancellationToken  CancellationToken)
        {

            var node = cli.NTSServer;

            if (Arguments.Length > 1)
            {

                if (!Arguments[1].Equals("sync", StringComparison.OrdinalIgnoreCase))
                    return [ Help() ];

                if (!cli.MayDo(Permission.Run(NodeResources.NTS), CommandName, out var refusedSync))
                    return [ refusedSync ];

                if (node.Upstream is not UpstreamDiscipline upstream)
                    return [ "This NTS server hands out the time of this machine: there are no upstream servers to ask." ];

                var status = await upstream.PollAsync(CancellationToken);

                return [ .. UpstreamLines(status) ];

            }

            if (!cli.MayDo(Permission.Read(NTSServerAccess.TimeServer), CommandName, out var refused))
                return [ refused ];

            var settings  = node.Settings;
            var host      = node.Host;
            var metrics   = host.Metrics;
            var lines     = new List<String> {

                $"time source     {(settings.TimeSource == TimeSource.Local ? "this machine's clock" : $"upstream, NTS group '{settings.Upstream.Group}'")}",
                $"answering       {(host.IsServing ? $"yes, since {host.ServingSince:yyyy-MM-dd HH:mm:ss} UTC" : "no - " + (settings.TimeSource == TimeSource.Upstream ? "the time is not synchronized" : "see the log"))}",
                $"NTS-KE (TLS)    TCP {TimeServerSettings.TextOf(settings.Listen)}:{settings.NTSKEPort}, {host.Identity.Subject}, until {host.Identity.NotAfter:yyyy-MM-dd}",
                $"                SHA-256 {host.Identity.Fingerprint}",
                $"                {host.Identity.Source}",
                $"NTS-KE (SSH)    " + (node.NTSKEOverSSH
                                          ? $"{node.SSHURL}, subsystem {NTSKE_SSH.SubsystemName}, {node.SSHClients.Clients.Count} NTS client(s)"
                                          : settings.SSHEnabled ? "switched on, but the SSH server is off" : "switched off"),
                $"                host key {node.SSHHostKey ?? "not known yet"}",
                $"NTP             UDP {TimeServerSettings.TextOf(settings.Listen)}:{settings.NTPPort}" +
                                  (host.AdvertisedNTP is not null ? $", the key exchange names {host.AdvertisedNTP}" : ""),
                $"clock           {node.Clock.GetUtcNow():yyyy-MM-dd HH:mm:ss.fff} UTC" +
                                  (node.Clock.IsFreeRunning
                                       ? String.Create(CultureInfo.InvariantCulture, $", free-running base corrected by {node.Clock.Correction.TotalMilliseconds:+0.000;-0.000} ms at {node.Clock.Frequency * 1e6:+0.000;-0.000} ppm")
                                       : ", this machine's clock"),
                $"described as    {host.Description?.ToString() ?? "nothing - nothing is answered"}",
                $"master key      " + (host.CurrentMasterKeyId is UInt64 current
                                           ? $"{current}, of {host.MasterKeys.Count} kept - see '{MasterKeysCommand.CommandName}'"
                                           : "none - nothing is answered"),
                $"answered        {metrics.NTPResponsesSent} NTP response(s) to {metrics.NTPRequestsReceived} request(s), " +
                                  $"{metrics.NTSKEResponsesSent} key exchange(s) of {metrics.NTSKEConnectionsAccepted} connection(s)" +
                                  (metrics.NTPRequestsInvalid > 0 || metrics.NTSKEHandshakeFailures > 0
                                       ? $"; {metrics.NTPRequestsInvalid} invalid NTP request(s), {metrics.NTSKEHandshakeFailures} failed handshake(s)"
                                       : "")

            };

            if (node.Upstream is UpstreamDiscipline following)
                lines.AddRange(UpstreamLines(following.Status()));

            return [ .. lines ];

        }

        #endregion

        #region (private static) UpstreamLines(Status)

        private static IEnumerable<String> UpstreamLines(UpstreamStatus Status)
        {

            yield return $"upstream        {(Status.Synchronized ? $"synchronized since {Status.SynchronizedSince:yyyy-MM-dd HH:mm:ss}" : "not synchronized")}" +
                         (Status.SystemPeer is not null ? $", closest to {Status.SystemPeer} (stratum {Status.UpstreamStratum})" : "");

            if (Status.LastOffset.HasValue)
                yield return String.Create(CultureInfo.InvariantCulture,
                                 $"                last offset {Status.LastOffset.Value.TotalMilliseconds:+0.000;-0.000} ms, " +
                                 $"off by {Status.LastError?.TotalMilliseconds ?? 0:+0.000;-0.000} ms, jitter {Status.Jitter?.TotalMilliseconds ?? 0:0.000} ms, " +
                                 $"{Status.Answered} of {Status.Asked} answered");

            yield return $"                {Status.Polls} round(s), {Status.FailedPolls} without a time, {Status.Outliers} outlier(s), {Status.Steps} step(s)";

            if (Status.ScheduledLeap is LeapSecond leap)
                yield return $"                {leap} - announced to the clients, and made at midnight";

            else if (Status.AnnouncedLeapIndicator is 1 or 2)
                yield return $"                a leap second announced by the upstream servers, taken up only on the last day of a month";

            if (Status.LeapSeconds != TimeSpan.Zero)
                yield return String.Create(CultureInfo.InvariantCulture,
                                 $"                leap seconds made since the start: {Status.LeapSeconds.TotalSeconds:+0;-0} s");

            if (Status.LastProblem is not null)
                yield return $"                last problem: {Status.LastProblem}";

        }

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} [sync] - how the time server stands; 'sync' asks the upstream servers now";

        #endregion

    }

}
