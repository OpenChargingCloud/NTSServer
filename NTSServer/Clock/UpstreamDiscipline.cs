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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Norn.Monitoring;
using org.GraphDefined.Vanaheimr.Norn.TimeSync;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Logging;

#endregion

namespace cloud.charging.open.NTSServer
{

    /// <summary>
    /// How following the upstream servers stands: whether the time handed out
    /// is synchronized, and how far it was from them at the last measurement.
    /// </summary>
    public sealed record UpstreamStatus(String               Group,
                                        Boolean              Synchronized,
                                        DateTimeOffset?      SynchronizedSince,
                                        DateTimeOffset?      LastPoll,
                                        DateTimeOffset?      LastGood,
                                        String?              LastProblem,
                                        TimeSpan?            LastOffset,
                                        TimeSpan?            LastError,
                                        TimeSpan?            Jitter,
                                        TimeSpan?            Spread,
                                        String?              SystemPeer,
                                        Byte?                UpstreamStratum,
                                        Int32?               Answered,
                                        Int32?               Asked,
                                        Double               FrequencyPPM,
                                        TimeSpan             Correction,
                                        TimeSpan             SlewRemaining,
                                        Int64                Polls,
                                        Int64                FailedPolls,
                                        Int64                Outliers,
                                        Int64                Steps,
                                        ClockDescription?    Description,
                                        Byte?                AnnouncedLeapIndicator,
                                        LeapSecond?          ScheduledLeap,
                                        TimeSpan             LeapSeconds)
    {

        public JObject ToJSON(DateTimeOffset Now)

            => new (
                   new JProperty("group",              Group),
                   new JProperty("synchronized",       Synchronized),
                   new JProperty("synchronizedSince",  SynchronizedSince?.ToString("o")),
                   new JProperty("lastPoll",           LastPoll?.ToString("o")),
                   new JProperty("lastGood",           LastGood?.ToString("o")),
                   new JProperty("lastGoodAgeSeconds", LastGood.HasValue ? Math.Round((Now - LastGood.Value).TotalSeconds, 1) : null),
                   new JProperty("lastProblem",        LastProblem),
                   new JProperty("offsetMs",           Ms(LastOffset)),
                   new JProperty("errorMs",            Ms(LastError)),
                   new JProperty("jitterMs",           Ms(Jitter)),
                   new JProperty("spreadMs",           Ms(Spread)),
                   new JProperty("systemPeer",         SystemPeer),
                   new JProperty("upstreamStratum",    UpstreamStratum),
                   new JProperty("answered",           Answered),
                   new JProperty("asked",              Asked),
                   new JProperty("frequencyPPM",       Math.Round(FrequencyPPM, 3)),
                   new JProperty("correctionMs",       Ms(Correction)),
                   new JProperty("slewRemainingSeconds",
                                                       Math.Round(SlewRemaining.TotalSeconds, 1)),
                   new JProperty("polls",              Polls),
                   new JProperty("failedPolls",        FailedPolls),
                   new JProperty("outliers",           Outliers),
                   new JProperty("steps",              Steps),
                   new JProperty("description",        Description?.ToJSON()),
                   new JProperty("leapIndicatorAnnounced",
                                                       AnnouncedLeapIndicator),
                   new JProperty("leapSecond",         ScheduledLeap?.ToJSON()),
                   new JProperty("leapSecondsMade",    (Int32) Math.Round(LeapSeconds.TotalSeconds))
               );

        private static Double? Ms(TimeSpan? Value)

            => Value.HasValue ? Math.Round(Value.Value.TotalMilliseconds, 3) : null;

    }


    /// <summary>
    /// The clock handed out in upstream mode, kept to a group of upstream NTS
    /// servers: they are asked over NTS-KE and NTP+NTS every poll interval,
    /// and what they say steers the <see cref="ServedClock"/> - never the
    /// clock of this machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The group is one of the node's NTS groups - "legal" unless said
    /// otherwise - so that the servers, their quorum, the certificates they
    /// are held to and everything else about them is written where every node
    /// writes its time servers, the "nts" section, and checked by what checks
    /// them there. Each round asks the group as the node does, band by band,
    /// and takes the median of the authenticated answers once the quorum has
    /// answered: a single server that lies cannot move the clock.
    /// </para>
    /// <para>
    /// The first usable round <b>steps</b> the clock handed out to it - before
    /// anything has been handed out, so there is nobody to see the jump. From
    /// then on every round <b>slews</b>: the rate of the clock handed out is
    /// changed until the next round would find no difference, never by more
    /// than the slew limit. The rate of the free-running base itself is
    /// estimated by a straight line through the last sixteen offsets, which is
    /// what keeps the clock where it was found while nobody answers.
    /// </para>
    /// <para>
    /// A round that finds the clock further off than the step threshold is
    /// passed over as an outlier - a path that just became congested, a
    /// server that just stepped. Three in a row are no outlier but the truth,
    /// and the clock is stepped, which is written down as a warning. Without
    /// a usable round for longer than the holdover, the time is no longer
    /// handed out as synchronized at all.
    /// </para>
    /// <para>
    /// A leap second the upstream servers announce is announced to the
    /// clients and made by the clock handed out - when more than half of the
    /// authenticated answers of a round announce it, and only on the last day
    /// of a month (see <see cref="LeapSecond"/>). It is made at the instant
    /// the clock handed out reaches it, whether the servers are reachable
    /// then or not, and announced no longer from that instant on. The
    /// offsets are measured leap seconds aside, so that the second does not
    /// end up in the rate of the base; where the servers did not make the
    /// second after all, the clock is found a second off and stepped, as it
    /// would be for any other reason.
    /// </para>
    /// </remarks>
    public sealed class UpstreamDiscipline : IAsyncDisposable
    {

        #region Data

        /// <summary>
        /// How many offsets the rate of the base is estimated from.
        /// </summary>
        public const   Int32   MaxSamples      = 16;

        /// <summary>
        /// How many rounds in a row have to be further off than the step
        /// threshold before the clock is stepped.
        /// </summary>
        public const   Int32   StepoutRounds   = 3;

        /// <summary>
        /// The largest rate of the base believed: 500 ppm, as ntpd's. An
        /// oscillator further off than that is broken rather than slow.
        /// </summary>
        public const   Double  MaxFrequency    = 500e-6;

        /// <summary>
        /// The frequency tolerance of RFC 5905 § 7.3: 15 ppm, what a clock may
        /// be assumed to wander by between two measurements.
        /// </summary>
        public const   Double  PHI             = 15e-6;

        private readonly  Func<String, NTSGroup?>                      findGroup;
        private readonly  DNSClient                                    dnsClient;
        private readonly  ServedClock                                  clock;
        private readonly  EventLog                                     log;
        private readonly  MeasurementEngine                            engine;
        private readonly  SemaphoreSlim                                pollGate   = new (1, 1);
        private readonly  List<(DateTimeOffset At, Double Offset)>     samples    = [];
        private readonly  Queue<Double>                                residuals  = new ();

        private           CancellationTokenSource?                     cts;
        private           Task?                                        loop;
        private           Int32                                        outliersInARow;
        private           DateTimeOffset?                              lastDescribed;

        private           Boolean                                      synchronized;
        private           DateTimeOffset?                              synchronizedSince;
        private           DateTimeOffset?                              lastPoll;
        private           DateTimeOffset?                              lastGood;
        private           String?                                      lastProblem;
        private           TimeSpan?                                    lastOffset;
        private           TimeSpan?                                    lastError;
        private           TimeSpan?                                    spread;
        private           String?                                      systemPeer;
        private           Byte?                                        upstreamStratum;
        private           Int32?                                       answered;
        private           Int32?                                       asked;
        private           Int64                                        polls;
        private           Int64                                        failedPolls;
        private           Int64                                        outliers;
        private           Int64                                        steps;
        private           Byte?                                        announced;
        private           Boolean                                      heldBack;
        private           CancellationTokenSource?                     leapWatch;
        private           TaskCompletionSource                         nudge      = new (TaskCreationOptions.RunContinuationsAsynchronously);

        #endregion

        #region Properties

        /// <summary>
        /// How the servers are followed - read anew by every round, so that a
        /// change is followed from the next one on (see <see cref="Reconfigure"/>).
        /// </summary>
        public UpstreamSettings   Settings      { get; private set; }

        /// <summary>
        /// What the time handed out is described as while it is synchronized,
        /// or null before it ever was.
        /// </summary>
        public ClockDescription?  Description   { get; private set; }

        /// <summary>
        /// Whether the time handed out is synchronized now.
        /// </summary>
        public Boolean            IsSynchronized
            => synchronized;

        /// <summary>
        /// When the last usable round was, on the base clock.
        /// </summary>
        public DateTimeOffset?    LastGood
            => lastGood;

        #endregion

        #region Events

        /// <summary>
        /// The time handed out is synchronized: for the first time, or again
        /// after it was not - with what it is to be described as.
        /// </summary>
        public event Func<ClockDescription, Task>?  OnSynchronized;

        /// <summary>
        /// The time handed out is still synchronized, but described differently
        /// enough for a client to have to know: another stratum, another
        /// server, root delay or dispersion off by a factor of two.
        /// </summary>
        public event Func<ClockDescription, Task>?  OnRedescribed;

        /// <summary>
        /// The time handed out is no longer synchronized: no usable round for
        /// longer than the holdover - with why.
        /// </summary>
        public event Func<String, Task>?            OnUnsynchronized;

        /// <summary>
        /// A round was usable: when, on the clock handed out.
        /// </summary>
        public event Action<DateTimeOffset>?        OnMeasured;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Follow the servers of an NTS group of the node.
        /// </summary>
        /// <param name="Settings">How they are followed.</param>
        /// <param name="FindGroup">The node's NTS group of a name, asked every round - so that a group changed on the node's NTS page is followed as it is now.</param>
        /// <param name="DNSClient">How their names are resolved.</param>
        /// <param name="Clock">The clock that is steered; its base is what is measured.</param>
        /// <param name="Log">Where what happens is written.</param>
        public UpstreamDiscipline(UpstreamSettings         Settings,
                                  Func<String, NTSGroup?>  FindGroup,
                                  DNSClient                DNSClient,
                                  ServedClock              Clock,
                                  EventLog                 Log)
        {

            this.Settings   = Settings;
            this.findGroup  = FindGroup;
            this.dnsClient  = DNSClient;
            this.clock      = Clock;
            this.log        = Log;

            // On the base and not on the clock handed out: what is measured is
            // how far the base is off, and the correction is what that makes
            // of it. Measuring the clock handed out would measure the
            // correction against itself.
            this.engine     = new MeasurementEngine(
                                  new MonitoringConfig {
                                      DroneId       = "ntsserver",
                                      NTPTimeout    = TimeSpan.FromSeconds(5),
                                      NTSKETimeout  = TimeSpan.FromSeconds(10)
                                  },
                                  Clock.Base
                              );

        }

        #endregion


        #region Start()

        /// <summary>
        /// Begin asking: at once, and then every poll interval - every 16
        /// seconds while the time was never synchronized, so that a start does
        /// not wait a minute for its second try.
        /// </summary>
        public void Start()
        {

            if (loop is not null)
                return;

            cts  = new CancellationTokenSource();
            var token = cts.Token;

            loop = Task.Run(async () => {

                while (!token.IsCancellationRequested)
                {

                    try
                    {
                        await PollAsync(token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        log.Warning($"Asking the upstream time servers failed: {e.Message}", "ntsserver", "upstream", "clock");
                    }

                    var wait = synchronized
                                   ? Settings.Poll
                                   : TimeSpan.FromTicks(Math.Min(Settings.Poll.Ticks, UpstreamSettings.MinPollInterval.Ticks));

                    // Or until the settings change: another group is asked at
                    // once, and not after up to 17 minutes of the old interval.
                    await Task.WhenAny(Task.Delay(wait, token), Volatile.Read(ref nudge).Task);

                    if (token.IsCancellationRequested)
                        break;

                }

            }, token);

        }

        #endregion

        #region Reconfigure(Settings)

        /// <summary>
        /// Follow the servers as the given settings say from now on - another
        /// group, another poll interval, step threshold, slew limit or holdover -
        /// and ask them at once.
        /// </summary>
        /// <remarks>
        /// What was measured is kept: the offsets are offsets of this machine's
        /// oscillator, whichever group measured them. A group that has another
        /// time than the one before is found further off than the step threshold,
        /// and stepped to on the third round, as for any other reason.
        /// </remarks>
        public void Reconfigure(UpstreamSettings Settings)
        {

            this.Settings = Settings;

            Interlocked.Exchange(ref nudge, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).
                        TrySetResult();

        }

        #endregion

        #region StopAsync()

        public async Task StopAsync()
        {

            StopWatching();

            if (cts is null)
                return;

            cts.Cancel();

            try
            {
                if (loop is not null)
                    await loop.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch
            { }

            cts.Dispose();
            cts  = null;
            loop = null;

        }

        #endregion


        #region PollAsync(CancellationToken)

        /// <summary>
        /// One round: ask the group, and steer the clock by what it said.
        /// </summary>
        /// <returns>The status after the round.</returns>
        public async Task<UpstreamStatus> PollAsync(CancellationToken CancellationToken = default)
        {

            await pollGate.WaitAsync(CancellationToken);

            try
            {
                await Round(CancellationToken);
            }
            finally
            {
                pollGate.Release();
            }

            return Status();

        }

        #endregion

        #region (private) Round(CancellationToken)

        private async Task Round(CancellationToken CancellationToken)
        {

            var group = findGroup(Settings.Group);

            if (group is null)
            {
                await Failed($"the node has no NTS group '{Settings.Group}' - see the \"nts\" section of the configuration file");
                return;
            }

            if (!group.Enabled)
            {
                await Failed($"the NTS group '{group.Id}' is switched off");
                return;
            }

            var verdict  = await group.Sources.Measure(engine, dnsClient, CancellationToken);
            var at       = clock.Base.GetUtcNow();

            Interlocked.Increment(ref polls);

            lastPoll     = at;
            answered     = verdict.Answered;
            asked        = verdict.Results.Count;
            spread       = verdict.Spread;

            if (!verdict.IsUsable || verdict.Offset is null)
            {
                await Failed(WhyNotUsable(verdict, group.Id.ToString()));
                return;
            }

            var median   = verdict.Offset.Value.TotalSeconds;

            // Leap seconds aside: after one is made, the upstream servers and
            // the clock handed out are a second further from the base, and
            // both the same second - the correction is what is left.
            var offset   = median - clock.LeapSecondsAt(at).TotalSeconds;

            // The server the reference identifier names, and whose stratum,
            // root delay and root dispersion this server's are built on - see
            // SystemPeer.
            var trusted  = verdict.Results.
                                   Where  (TimeSyncVerdict.CanBeTrusted).
                                   ToArray();

            var peer     = SystemPeer(trusted, systemPeer, verdict.Offset.Value, group.Sources.MaxDeviation);

            lastGood         = at;
            lastProblem      = null;
            lastOffset       = TimeSpan.FromSeconds(offset);
            systemPeer       = peer.ServerHostname.ToString().TrimEnd('.');
            upstreamStratum  = peer.NTP!.Stratum;

            #region Not synchronized yet - or not any more: step

            if (!synchronized)
            {

                var jump = clock.Step(TimeSpan.FromSeconds(offset), clock.Frequency);

                Interlocked.Increment(ref steps);

                samples.Clear();
                lock (residuals) residuals.Clear();
                samples.Add((at, offset));

                outliersInARow     = 0;
                lastError          = TimeSpan.Zero;
                synchronized       = true;
                synchronizedSince  = at;

                Announce(trusted);

                Description        = Describe(peer, verdict);
                lastDescribed      = at;

                log.Notice(String.Create(CultureInfo.InvariantCulture,
                               $"The time handed out is synchronized to NTS group '{group.Id}': {verdict}, stepped by {jump.TotalMilliseconds:+0.000;-0.000} ms. " +
                               $"It is described as {Description}."),
                           "ntsserver", "upstream", "clock");

                OnMeasured?.Invoke(clock.GetUtcNow());

                if (OnSynchronized is not null)
                    await OnSynchronized(Description);

                return;

            }

            #endregion

            var error = offset - clock.CorrectionAt(at).TotalSeconds;

            lastError = TimeSpan.FromSeconds(error);

            #region Further off than the step threshold: an outlier, or three of them a step

            if (Math.Abs(error) > Settings.Step.TotalSeconds)
            {

                outliersInARow++;
                Interlocked.Increment(ref outliers);

                if (outliersInARow < StepoutRounds)
                {

                    log.Info(String.Create(CultureInfo.InvariantCulture,
                                 $"The upstream time servers found the time handed out {error * 1000:+0.000;-0.000} ms off, more than the step threshold of {Settings.Step.TotalMilliseconds} ms: " +
                                 $"passed over as an outlier ({outliersInARow} of {StepoutRounds} in a row before the clock is stepped)."),
                             "ntsserver", "upstream", "clock");

                    return;

                }

                var jump = clock.Step(TimeSpan.FromSeconds(offset), clock.Frequency);

                Interlocked.Increment(ref steps);

                samples.Clear();
                lock (residuals) residuals.Clear();
                samples.Add((at, offset));
                outliersInARow = 0;

                Announce(trusted);

                log.Warning(String.Create(CultureInfo.InvariantCulture,
                                $"The time handed out was stepped by {jump.TotalMilliseconds:+0.000;-0.000} ms: the upstream time servers found it further off than the step threshold " +
                                $"of {Settings.Step.TotalMilliseconds} ms {StepoutRounds} times in a row ({verdict})."),
                            "ntsserver", "upstream", "clock");

                OnMeasured?.Invoke(clock.GetUtcNow());

                await Redescribe(peer, verdict, at);

                return;

            }

            #endregion

            #region Within it: estimate the rate, and slew

            outliersInARow = 0;

            samples.Add((at, offset));

            var oldest = at - Settings.Poll * (MaxSamples * 2);

            while (samples.Count > MaxSamples || (samples.Count > 1 && samples[0].At < oldest))
                samples.RemoveAt(0);

            lock (residuals)
            {

                residuals.Enqueue(error);

                while (residuals.Count > 8)
                    residuals.Dequeue();

            }

            var frequency  = clock.Frequency;
            var target     = offset;

            if (samples.Count >= 4 &&
                (samples[^1].At - samples[0].At) >= Settings.Poll * 3 &&
                TryFit(samples, at, out var slope, out var estimate))
            {
                frequency  = Math.Clamp(slope, -MaxFrequency, MaxFrequency);
                target     = estimate;
            }

            clock.SlewTo(TimeSpan.FromSeconds(target),
                         frequency,
                         Settings.Poll,
                         Settings.Slew);

            Announce(trusted);

            OnMeasured?.Invoke(clock.GetUtcNow());

            await Redescribe(peer, verdict, at);

            #endregion

        }

        #endregion

        #region (private) Failed(Why)

        /// <summary>
        /// A round that produced no time - and, past the holdover, the end of
        /// handing it out as synchronized.
        /// </summary>
        private async Task Failed(String Why)
        {

            Interlocked.Increment(ref failedPolls);

            var now       = clock.Base.GetUtcNow();
            var firstTime = lastProblem != Why;

            lastPoll      = now;
            lastProblem   = Why;

            if (!synchronized)
            {
                if (firstTime)
                    log.Warning($"The upstream time servers gave no time, and nothing is handed out until they do: {Why}.", "ntsserver", "upstream", "clock");
                return;
            }

            if (lastGood.HasValue && now - lastGood.Value > Settings.HoldoverTime)
            {

                synchronized = false;

                var message = $"The time handed out is no longer synchronized: the upstream time servers gave no usable time for more than " +
                              $"{Settings.HoldoverTime.TotalMinutes:F0} minute(s), the holdover. Last: {Why}.";

                log.Error(message, "ntsserver", "upstream", "clock");

                if (OnUnsynchronized is not null)
                    await OnUnsynchronized(message);

                return;

            }

            if (firstTime)
                log.Warning($"The upstream time servers gave no time: {Why}. The time handed out runs on, at the rate last measured, " +
                            $"for up to {Settings.HoldoverTime.TotalMinutes:F0} minute(s).",
                            "ntsserver", "upstream", "clock");

        }

        #endregion

        #region (private) Redescribe(Peer, Verdict, At)

        /// <summary>
        /// Describe the clock anew, and say so where a client would see the
        /// difference - at most every fifteen minutes, since every new
        /// description is a Norn server made anew.
        /// </summary>
        private async Task Redescribe(NTSMeasurementResult  Peer,
                                      TimeSyncVerdict       Verdict,
                                      DateTimeOffset        At)
        {

            var description = Describe(Peer, Verdict);

            if (Description is null)
            {
                Description   = description;
                lastDescribed = At;
                return;
            }

            if (description.IsSameFor(Description))
                return;

            // Another stratum at once, since a client chooses by it, and a leap
            // second announced or no longer announced, since a client acts on
            // it; another reference, root delay and dispersion at most every
            // fifteen minutes - every new description is a server made anew.
            var mustSay = description.Stratum       != Description.Stratum ||
                          description.LeapIndicator != Description.LeapIndicator;

            if (!mustSay && lastDescribed.HasValue && At - lastDescribed.Value < TimeSpan.FromMinutes(15))
                return;

            log.Info($"The time handed out is described anew: {description} (was {Description}).", "ntsserver", "upstream", "clock");

            Description   = description;
            lastDescribed = At;

            if (OnRedescribed is not null)
                await OnRedescribed(description);

        }

        #endregion

        #region (private) Describe(Peer, Verdict)

        /// <summary>
        /// What this server is, built on the server its time is closest to:
        /// one stratum below it, named by its address, its root delay plus the
        /// way there, and its root dispersion plus what the measuring and the
        /// time until the next one add.
        /// </summary>
        private ClockDescription Describe(NTSMeasurementResult  Peer,
                                          TimeSyncVerdict       Verdict)
        {

            var ntp             = Peer.NTP!;

            var jitter          = Jitter()?.TotalSeconds ?? 0;

            var rootDelay       = TimeSpan.FromSeconds(Math.Max(0, ntp.RootDelaySeconds)) + ntp.RoundTripDelay;

            // RFC 5905 § 11.3 adds up the same things: the upstream server's own
            // dispersion, how far the servers disagreed, how much the
            // measurements jitter - never less than a millisecond, which is the
            // granularity a round trip across a network is honest to - and
            // what the clock may wander by until it is measured again, four
            // poll intervals of it, since this description is not renewed
            // with every round.
            var rootDispersion  = Math.Max(0, ntp.RootDispersionSeconds) +
                                  (Verdict.Spread?.TotalSeconds ?? 0) / 2  +
                                  Math.Max(jitter, 0.001)                  +
                                  PHI * Settings.Poll.TotalSeconds * 4;

            return new ClockDescription(
                       Stratum:         (Byte) Math.Clamp(ntp.Stratum + 1, 2, 15),
                       ReferenceId:     ClockDescription.ReferenceIdOf(ntp.RemoteAddress),
                       ReferenceText:   $"{Peer.ServerHostname.ToString().TrimEnd('.')}{(ntp.RemoteAddress is not null ? $" ({ntp.RemoteAddress})" : "")}",
                       RootDelay:       rootDelay,
                       RootDispersion:  TimeSpan.FromSeconds(rootDispersion),
                       LeapIndicator:   clock.ScheduledLeap?.LeapIndicator ?? 0
                   );

        }

        #endregion


        #region (private) Announce(Trusted)

        /// <summary>
        /// Take up the leap second the authenticated answers of a round
        /// announce - or let go of one they no longer announce.
        /// </summary>
        private void Announce(IReadOnlyList<NTSMeasurementResult> Trusted)
        {

            announced = LeapVote([.. Trusted.Select(result => result.NTP!.LeapIndicator)]);

            var leap      = LeapSecond.Announced(announced.Value, clock.GetUtcNow());
            var scheduled = clock.ScheduledLeap;

            // Still announced in the second an inserted one repeats, which is
            // still the last day of the month - and already made.
            if (leap is not null && clock.HasMade(leap))
                leap = null;

            if (announced is 1 or 2 && leap is null && !heldBack && scheduled is null)
            {
                heldBack = true;
                log.Info($"The upstream time servers announce a leap second (leap indicator {announced}), which is not passed on: only on the last day of a month, at whose end it is made.",
                         "ntsserver", "upstream", "clock");
            }

            if (announced is 0 or 3)
                heldBack = false;

            if (leap == scheduled)
                return;

            clock.Schedule(leap);

            if (leap is not null)
            {

                log.Notice($"The upstream time servers announce {leap}: it is announced to the clients from now on, and made by the time handed out at midnight.",
                           "ntsserver", "upstream", "clock");

                WatchFor(leap);

            }

            else
            {

                log.Warning($"The upstream time servers no longer announce {scheduled}: it is not made, and no longer announced.",
                            "ntsserver", "upstream", "clock");

                StopWatching();

            }

        }

        #endregion

        #region (private) WatchFor(Leap) / StopWatching()

        /// <summary>
        /// Wait for the clock handed out to make the given leap second, and
        /// stop announcing it then.
        /// </summary>
        private void WatchFor(LeapSecond Leap)
        {

            StopWatching();

            var watch = new CancellationTokenSource();
            var token = watch.Token;

            leapWatch = watch;

            _ = Task.Run(async () => {

                try
                {

                    while (!clock.HasMade(Leap))
                    {

                        if (clock.ScheduledLeap != Leap && !clock.HasMade(Leap))
                            return;

                        // On the timers of this machine, which the clock handed
                        // out may run faster or slower than by a thousandth:
                        // never past the instant, and the last second in the
                        // smallest steps they take.
                        var left = Leap.At - clock.GetUtcNow();

                        await Task.Delay(left > TimeSpan.FromSeconds(1)
                                             ? left - TimeSpan.FromTicks(Math.Max(left.Ticks / 500, TimeSpan.TicksPerMillisecond * 500))
                                             : TimeSpan.FromMilliseconds(1),
                                         token);

                    }

                    await LeapMade(Leap);

                }
                catch (OperationCanceledException)
                { }
                catch (Exception e)
                {
                    log.Warning($"Waiting for {Leap} failed: {e.Message}", "ntsserver", "upstream", "clock");
                }

            }, token);

        }

        private void StopWatching()
        {

            var watch = Interlocked.Exchange(ref leapWatch, null);

            if (watch is null)
                return;

            watch.Cancel();
            watch.Dispose();

        }

        #endregion

        #region (private) LeapMade(Leap)

        /// <summary>
        /// The clock handed out made the given leap second: announce it no
        /// longer, at once.
        /// </summary>
        /// <remarks>
        /// Without waiting for a round that may be asking the upstream servers
        /// for the next ten seconds: after a deleted second the next day has
        /// begun, and a client still told to delete one would delete another at
        /// its end. A round that describes the clock at the same time describes
        /// it without the leap second as well, since the clock no longer has
        /// one scheduled.
        /// </remarks>
        private async Task LeapMade(LeapSecond Leap)
        {

            log.Notice($"The time handed out made {Leap}: 23:59:59 was {(Leap.Inserted ? "repeated" : "skipped")}.",
                       "ntsserver", "upstream", "clock");

            if (!synchronized || Description is not ClockDescription described || described.LeapIndicator == 0)
                return;

            Description   = described with { LeapIndicator = 0 };
            lastDescribed = clock.Base.GetUtcNow();

            if (OnRedescribed is not null)
                await OnRedescribed(Description);

        }

        #endregion

        #region (internal static) LeapVote(LeapIndicators)

        /// <summary>
        /// The leap indicator of a round: 1 or 2 where more than half of the
        /// authenticated answers say so, 0 otherwise - so that a single server
        /// cannot have a leap second made.
        /// </summary>
        /// <remarks>
        /// An unsynchronized server (3) is not among the answers: Norn's NTS
        /// client turns its answer away, as RFC 5905 asks. Were one to get
        /// here, it would be a voice against the leap second like any other.
        /// </remarks>
        internal static Byte LeapVote(IReadOnlyList<Byte> LeapIndicators)
        {

            foreach (Byte leapIndicator in (Byte[]) [ 1, 2 ])
                if (LeapIndicators.Count(indicator => indicator == leapIndicator) * 2 > LeapIndicators.Count)
                    return leapIndicator;

            return 0;

        }

        #endregion

        #region (internal static) SystemPeer(Trusted, Named, Median, MaxDeviation)

        /// <summary>
        /// The server this server's description is built on: the one named
        /// last time, for as long as it gives an authenticated answer that
        /// agrees with the time - and otherwise the one whose answer the
        /// median is closest to.
        /// </summary>
        /// <remarks>
        /// Which of four servers of the same institution is a tenth of a
        /// millisecond closer changes from round to round, and a server that
        /// renamed its reference with every round would be described anew with
        /// every round. The time is the median's either way; the system peer
        /// only describes it - and so it is one of the servers the median is
        /// made of. Where a majority outvoted the rest, Norn takes the median
        /// of that majority only, and a server further than the group's
        /// MaxDeviation from it is one of the outvoted: its stratum and root
        /// dispersion say nothing about the time handed out.
        /// </remarks>
        /// <param name="Trusted">The authenticated answers of the round.</param>
        /// <param name="Named">The server named last time, if any.</param>
        /// <param name="Median">The time of the round, as an offset.</param>
        /// <param name="MaxDeviation">How far apart the group allows its servers to be.</param>
        internal static NTSMeasurementResult SystemPeer(IReadOnlyList<NTSMeasurementResult>  Trusted,
                                                        String?                              Named,
                                                        TimeSpan                             Median,
                                                        TimeSpan                             MaxDeviation)
        {

            var agreeing = Trusted.Where  (result => (result.NTP!.Offset - Median).Duration() < MaxDeviation).
                                   ToArray();

            // Never empty while the median is the median of answers - the one
            // closest to it is within any deviation that let it be one - but
            // were it, the closest of all is still a better description than
            // none.
            if (agreeing.Length == 0)
                agreeing = [.. Trusted];

            return agreeing.FirstOrDefault(result => result.ServerHostname.ToString().TrimEnd('.') == Named)
                       ?? agreeing.OrderBy(result => (result.NTP!.Offset - Median).Duration()).
                                   First();

        }

        #endregion

        #region (internal static) WhyNotUsable(Verdict, GroupId)

        /// <summary>
        /// Why a round produced no time, as LastProblem says it.
        /// </summary>
        /// <remarks>
        /// Servers that all answered and disagree are not "nobody answered",
        /// which is what this said when Norn first told them apart.
        /// </remarks>
        internal static String WhyNotUsable(TimeSyncVerdict  Verdict,
                                            String           GroupId)
        {

            var problems = Verdict.Results.
                                   Where (result => !TimeSyncVerdict.CanBeTrusted(result)).
                                   Select(result => $"{result.ServerHostname.ToString().TrimEnd('.')}: {result.ErrorMessage?.ToString() ?? result.NTP?.ErrorMessage?.ToString() ?? "no authenticated answer"}").
                                   ToArray();

            var why      = problems.Length > 0
                               ? $" ({String.Join("; ", problems)})"
                               : "";

            return Verdict.Outcome switch {
                       TimeSyncOutcome.TooFewServers    => $"{Verdict.Answered} of the NTS group '{GroupId}' answered, and {Verdict.Required} must{why}",
                       TimeSyncOutcome.ServersDisagree  => $"the servers of the NTS group '{GroupId}' disagree - {Verdict}{why}",
                       _                                => $"nobody of the NTS group '{GroupId}' answered{why}"
                   };

        }

        #endregion

        #region (internal static) TryFit(Samples, At, out Slope, out Estimate)

        /// <summary>
        /// The straight line through the offsets: how fast they grow - the rate
        /// of the base - and where it is at the given time.
        /// </summary>
        internal static Boolean TryFit(IReadOnlyList<(DateTimeOffset At, Double Offset)>  Samples,
                                       DateTimeOffset                                     At,
                                       out Double                                         Slope,
                                       out Double                                         Estimate)
        {

            Slope     = 0;
            Estimate  = 0;

            if (Samples.Count < 2)
                return false;

            var n      = (Double) Samples.Count;
            var meanX  = Samples.Average(sample => (sample.At - At).TotalSeconds);
            var meanY  = Samples.Average(sample => sample.Offset);

            Double sxx = 0, sxy = 0;

            foreach (var (sampleAt, offset) in Samples)
            {
                var dx  = (sampleAt - At).TotalSeconds - meanX;
                sxx    += dx * dx;
                sxy    += dx * (offset - meanY);
            }

            if (sxx <= 0)
                return false;

            Slope     = sxy / sxx;
            Estimate  = meanY + Slope * (0 - meanX);

            return Double.IsFinite(Slope) && Double.IsFinite(Estimate);

        }

        #endregion


        #region Status()

        /// <summary>
        /// How it stands now.
        /// </summary>
        public UpstreamStatus Status()
        {

            var jitter = Jitter();

            return new UpstreamStatus(
                       Group:              Settings.Group,
                       Synchronized:       synchronized,
                       SynchronizedSince:  synchronizedSince,
                       LastPoll:           lastPoll,
                       LastGood:           lastGood,
                       LastProblem:        lastProblem,
                       LastOffset:         lastOffset,
                       LastError:          lastError,
                       Jitter:             jitter,
                       Spread:             spread,
                       SystemPeer:         systemPeer,
                       UpstreamStratum:    upstreamStratum,
                       Answered:           answered,
                       Asked:              asked,
                       FrequencyPPM:       clock.Frequency * 1e6,
                       Correction:         clock.Correction,
                       SlewRemaining:      clock.SlewRemaining,
                       Polls:              Interlocked.Read(ref polls),
                       FailedPolls:        Interlocked.Read(ref failedPolls),
                       Outliers:           Interlocked.Read(ref outliers),
                       Steps:              Interlocked.Read(ref steps),
                       Description:        Description,
                       AnnouncedLeapIndicator:
                                           announced,
                       ScheduledLeap:      clock.ScheduledLeap,
                       LeapSeconds:        clock.LeapSeconds
                   );

        }

        #endregion

        #region (private) Jitter()

        /// <summary>
        /// The root mean square of the last differences found: how much the
        /// measurements scatter around the clock handed out.
        /// </summary>
        private TimeSpan? Jitter()
        {
            lock (residuals)
            {
                return residuals.Count > 0
                           ? TimeSpan.FromSeconds(Math.Sqrt(residuals.Average(residual => residual * residual)))
                           : null;
            }
        }

        #endregion

        #region DisposeAsync()

        /// <summary>
        /// Stop asking - as often as asked. The gate is not disposed of, for the
        /// reason TimeServerHost.DisposeAsync gives.
        /// </summary>
        public async ValueTask DisposeAsync()

            => await StopAsync();

        #endregion

    }

}
