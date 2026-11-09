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

using System.Diagnostics;

#endregion

namespace cloud.charging.open.NTSServer
{

    /// <summary>
    /// A clock that runs on the stopwatch of this machine and on nothing else:
    /// it starts at the wall clock's time and from then on only counts.
    /// </summary>
    /// <remarks>
    /// What upstream mode measures against and corrects. The wall clock of the
    /// operating system is moved by whatever else keeps it - w32time,
    /// systemd-timesyncd, chrony, somebody with date(1) - and every such move
    /// would arrive in the time handed out at once, as a jump, and only in the
    /// next measurement as a cause. A clock that ignores all of them has one
    /// error left, the rate of its oscillator, and that is what measuring is
    /// good at finding.
    /// </remarks>
    public sealed class FreeRunningClock : TimeProvider
    {

        private readonly DateTimeOffset  anchor;
        private readonly Int64           anchorTimestamp;

        /// <summary>
        /// A free-running clock, starting at the time the given clock says now.
        /// </summary>
        /// <param name="StartFrom">Where it starts: the wall clock by default.</param>
        public FreeRunningClock(TimeProvider? StartFrom = null)
        {
            this.anchor           = (StartFrom ?? System).GetUtcNow();
            this.anchorTimestamp  = Stopwatch.GetTimestamp();
        }

        public override DateTimeOffset GetUtcNow()
            => anchor + Stopwatch.GetElapsedTime(anchorTimestamp);

    }


    /// <summary>
    /// The clock this NTS server hands out: a base clock and a correction on
    /// top of it, which is moved smoothly rather than in jumps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In local mode the base is the wall clock of the operating system and the
    /// correction stays zero: what is handed out is exactly what the machine
    /// says. In upstream mode the base is a <see cref="FreeRunningClock"/>, and
    /// the correction is what measuring it against the upstream servers found -
    /// an offset, and the rate at which that offset grows.
    /// </para>
    /// <para>
    /// A correction is <b>slewed</b>: the rate of the clock handed out is
    /// changed for as long as it takes to make up the difference, never by more
    /// than the slew limit - 500 ppm, half a millisecond per second, the limit
    /// of ntpd and of the kernels - so that time handed out never runs
    /// backwards and never jumps. Only a <see cref="Step"/> jumps, which the
    /// discipline does before anything has been handed out, and afterwards
    /// only for a difference too large to slew away in reasonable time.
    /// </para>
    /// <para>
    /// The node itself runs on this clock too: its log, its certificates, and
    /// its own check against the legal time servers - which so measures what
    /// is handed out, rather than the machine's clock below it.
    /// </para>
    /// </remarks>
    public sealed class ServedClock : TimeProvider
    {

        #region (private) Segment

        /// <summary>
        /// One stretch of the correction: from <paramref name="Anchor"/> on it
        /// grows at <paramref name="Rate"/> until <paramref name="SlewEnd"/>,
        /// and at <paramref name="Frequency"/> after it.
        /// </summary>
        private sealed record Segment(DateTimeOffset  Anchor,
                                      Double          Correction,
                                      Double          Rate,
                                      DateTimeOffset  SlewEnd,
                                      Double          Frequency);

        #endregion

        #region Data

        private readonly  Lock          gate = new();
        private volatile  TimeProvider  baseClock;
        private volatile  Segment       segment;

        #endregion

        #region Properties

        /// <summary>
        /// What the correction is applied to.
        /// </summary>
        public TimeProvider  Base
            => baseClock;

        /// <summary>
        /// Whether the base is a <see cref="FreeRunningClock"/>, which only
        /// upstream mode uses.
        /// </summary>
        public Boolean       IsFreeRunning
            => baseClock is FreeRunningClock;

        /// <summary>
        /// The correction now: what is handed out minus what the base says.
        /// </summary>
        public TimeSpan      Correction
            => ToTimeSpan(CorrectionAt(segment, baseClock.GetUtcNow()));

        /// <summary>
        /// The rate the correction grows at once a slew is over, in seconds
        /// per second: how much faster than the base the true time runs.
        /// </summary>
        public Double        Frequency
            => segment.Frequency;

        /// <summary>
        /// Whether a correction is being slewed in now.
        /// </summary>
        public Boolean       IsSlewing
            => baseClock.GetUtcNow() < segment.SlewEnd;

        /// <summary>
        /// How much longer the slew now going on lasts.
        /// </summary>
        public TimeSpan      SlewRemaining
        {
            get
            {
                var remaining = segment.SlewEnd - baseClock.GetUtcNow();
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// A clock handing out the given base, uncorrected.
        /// </summary>
        /// <param name="Base">The base: the system's wall clock by default.</param>
        public ServedClock(TimeProvider? Base = null)
        {

            this.baseClock  = Base ?? System;

            var now         = baseClock.GetUtcNow();
            this.segment    = new Segment(now, 0, 0, now, 0);

        }

        #endregion


        #region TimeProvider

        public override DateTimeOffset GetUtcNow()
        {

            var now = baseClock.GetUtcNow();

            return now + ToTimeSpan(CorrectionAt(segment, now));

        }

        public override TimeZoneInfo LocalTimeZone
            => System.LocalTimeZone;

        public override Int64 TimestampFrequency
            => System.TimestampFrequency;

        public override Int64 GetTimestamp()
            => System.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback  Callback,
                                           Object?        State,
                                           TimeSpan       DueTime,
                                           TimeSpan       Period)

            => System.CreateTimer(Callback, State, DueTime, Period);

        #endregion


        #region RunFreely()

        /// <summary>
        /// Hand out a <see cref="FreeRunningClock"/> from now on, starting where
        /// this clock is now - so that the change cannot be seen.
        /// </summary>
        /// <remarks>
        /// For upstream mode, decided once the configuration has been read and
        /// before anything is measured or handed out.
        /// </remarks>
        public void RunFreely()
        {
            lock (gate)
            {

                if (baseClock is FreeRunningClock)
                    return;

                var freeRunning = new FreeRunningClock(this);
                var now         = freeRunning.GetUtcNow();

                baseClock  = freeRunning;
                segment    = new Segment(now, 0, 0, now, 0);

            }
        }

        #endregion

        #region CorrectionAt(BaseTime)

        /// <summary>
        /// The correction at the given time of the base clock.
        /// </summary>
        public TimeSpan CorrectionAt(DateTimeOffset BaseTime)

            => ToTimeSpan(CorrectionAt(segment, BaseTime));

        #endregion

        #region Step(Correction, Frequency)

        /// <summary>
        /// Jump to the given correction now, and let it grow at the given rate
        /// from here on.
        /// </summary>
        /// <param name="Correction">The correction from now on.</param>
        /// <param name="Frequency">The rate it grows at, in seconds per second.</param>
        /// <returns>By how much the time handed out jumped.</returns>
        public TimeSpan Step(TimeSpan  Correction,
                             Double    Frequency)
        {
            lock (gate)
            {

                var now      = baseClock.GetUtcNow();
                var before   = CorrectionAt(segment, now);

                segment      = new Segment(now, Correction.TotalSeconds, Frequency, now, Frequency);

                return ToTimeSpan(Correction.TotalSeconds - before);

            }
        }

        #endregion

        #region SlewTo(Target, Frequency, MinDuration, MaxSlew)

        /// <summary>
        /// Move the correction to the given one - smoothly, over at least the
        /// given time and never faster than the given rate - and let it grow at
        /// the given frequency from then on.
        /// </summary>
        /// <param name="Target">The correction that is right now.</param>
        /// <param name="Frequency">The rate it grows at, in seconds per second.</param>
        /// <param name="MinDuration">The shortest time to spread the difference over: the poll interval, so that it is made up by the next measurement.</param>
        /// <param name="MaxSlew">The largest rate to make it up at, in seconds per second.</param>
        /// <returns>How long the slew takes.</returns>
        public TimeSpan SlewTo(TimeSpan  Target,
                               Double    Frequency,
                               TimeSpan  MinDuration,
                               Double    MaxSlew)
        {
            lock (gate)
            {

                var now       = baseClock.GetUtcNow();
                var current   = CorrectionAt(segment, now);
                var error     = Target.TotalSeconds - current;

                var duration  = Math.Max(MinDuration.TotalSeconds,
                                         MaxSlew > 0 ? Math.Abs(error) / MaxSlew : 0);

                var slewRate  = duration > 0
                                    ? error / duration
                                    : 0;

                segment       = new Segment(now,
                                            current,
                                            Frequency + slewRate,
                                            now + TimeSpan.FromSeconds(duration),
                                            Frequency);

                return TimeSpan.FromSeconds(duration);

            }
        }

        #endregion


        #region (private static) CorrectionAt(Segment, BaseTime)

        private static Double CorrectionAt(Segment         Segment,
                                           DateTimeOffset  BaseTime)
        {

            var elapsed = (BaseTime - Segment.Anchor).TotalSeconds;

            // A base that went back - a wall clock somebody set back, in local
            // mode, where the correction is zero anyway - holds the correction
            // where it was rather than running it backwards.
            if (elapsed <= 0)
                return Segment.Correction;

            var slewing = (Segment.SlewEnd - Segment.Anchor).TotalSeconds;

            return elapsed <= slewing
                       ? Segment.Correction + Segment.Rate * elapsed
                       : Segment.Correction + Segment.Rate * slewing + Segment.Frequency * (elapsed - slewing);

        }

        #endregion

        #region (private static) ToTimeSpan(Seconds)

        private static TimeSpan ToTimeSpan(Double Seconds)

            => TimeSpan.FromTicks((Int64) Math.Round(Seconds * TimeSpan.TicksPerSecond));

        #endregion

    }

}
