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

namespace cloud.charging.open.NTSServer.Tests
{

    /// <summary>
    /// The clock handed out: stepped, slewed, and never faster or slower than
    /// the slew limit allows.
    /// </summary>
    [TestFixture]
    public class ClockTests
    {

        #region (private) ManualClock

        /// <summary>
        /// A base clock that moves only when it is told to.
        /// </summary>
        private sealed class ManualClock(DateTimeOffset Start) : TimeProvider
        {

            public DateTimeOffset Now { get; set; } = Start;

            public override DateTimeOffset GetUtcNow()
                => Now;

            public void Advance(TimeSpan By)
                => Now += By;

        }

        private static readonly DateTimeOffset start = new (2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

        #endregion


        [Test]
        public void UncorrectedItIsItsBase()
        {

            var manual = new ManualClock(start);
            var clock  = new ServedClock(manual);

            Assert.That(clock.GetUtcNow(), Is.EqualTo(start));

            manual.Advance(TimeSpan.FromHours(1));

            Assert.That(clock.GetUtcNow(), Is.EqualTo(start.AddHours(1)));
            Assert.That(clock.Correction,  Is.EqualTo(TimeSpan.Zero));

        }

        [Test]
        public void AStepJumpsAndThenRunsAtItsFrequency()
        {

            var manual = new ManualClock(start);
            var clock  = new ServedClock(manual);

            var jump   = clock.Step(TimeSpan.FromSeconds(2), 10e-6);

            Assert.That(jump,              Is.EqualTo(TimeSpan.FromSeconds(2)));
            Assert.That(clock.GetUtcNow(), Is.EqualTo(start.AddSeconds(2)));

            manual.Advance(TimeSpan.FromSeconds(1000));

            // 10 ppm of 1000 s is 10 ms on top of the step.
            Assert.That(clock.Correction.TotalMilliseconds, Is.EqualTo(2010).Within(0.001));

        }

        [Test]
        public void ASlewMakesUpTheDifferenceOverTheGivenTimeAndThenRunsAtItsFrequency()
        {

            var manual   = new ManualClock(start);
            var clock    = new ServedClock(manual);

            // 10 ms to make up over at least 64 s: 156 ppm, under the limit.
            var duration = clock.SlewTo(TimeSpan.FromMilliseconds(10), 0, TimeSpan.FromSeconds(64), 500e-6);

            Assert.That(duration,         Is.EqualTo(TimeSpan.FromSeconds(64)));
            Assert.That(clock.IsSlewing,  Is.True);

            manual.Advance(TimeSpan.FromSeconds(32));
            Assert.That(clock.Correction.TotalMilliseconds, Is.EqualTo(5).Within(0.001));

            manual.Advance(TimeSpan.FromSeconds(32));
            Assert.That(clock.Correction.TotalMilliseconds, Is.EqualTo(10).Within(0.001));

            manual.Advance(TimeSpan.FromSeconds(1000));
            Assert.That(clock.Correction.TotalMilliseconds, Is.EqualTo(10).Within(0.001));
            Assert.That(clock.IsSlewing,  Is.False);

        }

        [Test]
        public void ALargeDifferenceIsSlewedNoFasterThanTheLimit()
        {

            var manual   = new ManualClock(start);
            var clock    = new ServedClock(manual);

            // 100 ms at 500 ppm takes 200 s, longer than the 64 s asked for.
            var duration = clock.SlewTo(TimeSpan.FromMilliseconds(-100), 0, TimeSpan.FromSeconds(64), 500e-6);

            Assert.That(duration.TotalSeconds, Is.EqualTo(200).Within(1e-9));

            // Never backwards: the clock handed out still advances, at 0.9995 s per second.
            var before = clock.GetUtcNow();
            manual.Advance(TimeSpan.FromSeconds(1));
            var after  = clock.GetUtcNow();

            Assert.That((after - before).TotalSeconds, Is.EqualTo(0.9995).Within(1e-6));

        }

        [Test]
        public void RunningFreelyStartsWhereTheClockWas()
        {

            var clock  = new ServedClock();
            var before = clock.GetUtcNow();

            clock.RunFreely();

            var after  = clock.GetUtcNow();

            Assert.That(clock.IsFreeRunning,             Is.True);
            Assert.That((after - before).Duration(),     Is.LessThan(TimeSpan.FromMilliseconds(50)));

        }

        [Test]
        public void TheLineThroughTheOffsetsIsTheRateOfTheBase()
        {

            // 20 ppm, starting at +3 ms, measured every 64 s.
            var samples = Enumerable.Range(0, 10).
                                     Select(i => (start.AddSeconds(64 * i), 0.003 + 20e-6 * 64 * i)).
                                     ToList();

            var at = samples[^1].Item1;

            Assert.That(UpstreamDiscipline.TryFit(samples, at, out var slope, out var estimate), Is.True);
            Assert.That(slope,    Is.EqualTo(20e-6).Within(1e-12));
            Assert.That(estimate, Is.EqualTo(0.003 + 20e-6 * 64 * 9).Within(1e-12));

        }

        [Test]
        public void ADescriptionIsTheSameUntilAClientWouldSeeTheDifference()
        {

            var description = new ClockDescription(2,
                                                   ClockDescription.ReferenceIdOf(org.GraphDefined.Vanaheimr.Hermod.IPv4Address.Parse("192.0.2.1")),
                                                   "192.0.2.1",
                                                   TimeSpan.FromMilliseconds(20),
                                                   TimeSpan.FromMilliseconds(10));

            Assert.That(description.IsSameFor(description with { RootDelay      = TimeSpan.FromMilliseconds(30) }), Is.True);
            Assert.That(description.IsSameFor(description with { RootDelay      = TimeSpan.FromMilliseconds(50) }), Is.False);
            Assert.That(description.IsSameFor(description with { RootDispersion = TimeSpan.FromMilliseconds(4)  }), Is.False);
            Assert.That(description.IsSameFor(description with { Stratum        = 3 }),                             Is.False);
            Assert.That(description.IsSameFor(description with { LeapIndicator  = 1 }),                             Is.False);

            // RFC 5905 § 7.3: an IPv4 upstream server is named by its address.
            Assert.That(description.ReferenceId, Is.EqualTo(org.GraphDefined.Vanaheimr.Norn.NTP.ReferenceIdentifier.From([ 192, 0, 2, 1 ])));

        }


        #region Leap seconds

        private static readonly DateTimeOffset newYear = new (2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        [Test]
        public void AnInsertedLeapSecondRepeats235959()
        {

            var manual = new ManualClock(newYear.AddSeconds(-2));
            var clock  = new ServedClock(manual);
            var leap   = new LeapSecond(newYear, Inserted: true);

            clock.Schedule(leap);

            Assert.That(clock.ScheduledLeap, Is.EqualTo(leap));

            manual.Advance(TimeSpan.FromSeconds(1.5));
            Assert.That(clock.GetUtcNow(), Is.EqualTo(newYear.AddSeconds(-0.5)));

            // Midnight on the base: 23:59:59 once more.
            manual.Advance(TimeSpan.FromSeconds(0.5));
            Assert.That(clock.GetUtcNow(), Is.EqualTo(newYear.AddSeconds(-1)));

            manual.Advance(TimeSpan.FromSeconds(0.5));
            Assert.That(clock.GetUtcNow(), Is.EqualTo(newYear.AddSeconds(-0.5)));

            manual.Advance(TimeSpan.FromSeconds(0.5));
            Assert.That(clock.GetUtcNow(), Is.EqualTo(newYear));

            Assert.That(clock.HasMade(leap),  Is.True);
            Assert.That(clock.ScheduledLeap,  Is.Null);
            Assert.That(clock.LeapSeconds,    Is.EqualTo(TimeSpan.FromSeconds(-1)));

            // The correction is what measuring found, and measuring found nothing.
            Assert.That(clock.Correction,     Is.EqualTo(TimeSpan.Zero));

        }

        [Test]
        public void ADeletedLeapSecondSkips235959()
        {

            var manual = new ManualClock(newYear.AddSeconds(-3));
            var clock  = new ServedClock(manual);

            clock.Schedule(new LeapSecond(newYear, Inserted: false));

            manual.Advance(TimeSpan.FromMilliseconds(1999));
            Assert.That(clock.GetUtcNow(), Is.EqualTo(newYear.AddMilliseconds(-1001)));

            manual.Advance(TimeSpan.FromMilliseconds(1));
            Assert.That(clock.GetUtcNow(), Is.EqualTo(newYear));

            Assert.That(clock.LeapSeconds, Is.EqualTo(TimeSpan.FromSeconds(1)));

        }

        [Test]
        public void ALeapSecondIsMadeWhereTheCorrectedClockReachesMidnight()
        {

            var t0     = newYear.AddSeconds(-2);
            var manual = new ManualClock(t0);
            var clock  = new ServedClock(manual);

            clock.Step(TimeSpan.FromSeconds(0.5), 0);
            clock.Schedule(new LeapSecond(newYear, Inserted: true));

            // Scheduled first and slewed afterwards: the instant moves with it.
            // 10 ms over 64 s is 156.25 ppm, so midnight is reached
            // 1.5 / 1.00015625 s after t0 on the base.
            clock.SlewTo(TimeSpan.FromSeconds(0.51), 0, TimeSpan.FromSeconds(64), 500e-6);

            var x = 1.5 / (1 + 0.01 / 64);

            manual.Now = t0.AddSeconds(x - 0.001);
            Assert.That((newYear - clock.GetUtcNow()).TotalMilliseconds, Is.EqualTo(1).Within(0.01));

            manual.Now = t0.AddSeconds(x + 0.001);
            Assert.That((clock.GetUtcNow() - newYear.AddSeconds(-1)).TotalMilliseconds, Is.EqualTo(1).Within(0.01));

        }

        [Test]
        public void ALeapSecondNoLongerScheduledIsNotMade()
        {

            var manual = new ManualClock(newYear.AddSeconds(-2));
            var clock  = new ServedClock(manual);

            clock.Schedule(new LeapSecond(newYear, Inserted: true));
            clock.Schedule(null);

            manual.Advance(TimeSpan.FromSeconds(3));

            Assert.That(clock.GetUtcNow(),   Is.EqualTo(newYear.AddSeconds(1)));
            Assert.That(clock.LeapSeconds,   Is.EqualTo(TimeSpan.Zero));

        }

        [Test]
        public void ALeapSecondIsMadeOnceAndStaysThroughStepsAndSlews()
        {

            var manual = new ManualClock(newYear.AddSeconds(-1));
            var clock  = new ServedClock(manual);
            var leap   = new LeapSecond(newYear, Inserted: true);

            clock.Schedule(leap);
            manual.Advance(TimeSpan.FromSeconds(1.5));

            // Still announced in the repeated second - and not made again.
            clock.Schedule(leap);
            manual.Advance(TimeSpan.FromSeconds(1));

            Assert.That(clock.GetUtcNow(), Is.EqualTo(newYear.AddSeconds(0.5)));

            clock.Step(TimeSpan.FromMilliseconds(20), 0);
            clock.SlewTo(TimeSpan.FromMilliseconds(20), 0, TimeSpan.FromSeconds(64), 500e-6);

            Assert.That(clock.GetUtcNow(),  Is.EqualTo(newYear.AddSeconds(0.52)));
            Assert.That(clock.LeapSeconds,  Is.EqualTo(TimeSpan.FromSeconds(-1)));

            // And at the base time of a measurement before it, none was made yet.
            Assert.That(clock.LeapSecondsAt(newYear.AddSeconds(-0.5)), Is.EqualTo(TimeSpan.Zero));

        }

        [Test]
        public void ALeapSecondCountsOnlyOnTheLastDayOfAMonth()
        {

            Assert.That(LeapSecond.Announced(1, new DateTimeOffset(2026, 12, 31, 10, 0, 0, TimeSpan.Zero)),
                        Is.EqualTo(new LeapSecond(newYear, true)));

            Assert.That(LeapSecond.Announced(2, new DateTimeOffset(2027, 2, 28, 12, 0, 0, TimeSpan.Zero)),
                        Is.EqualTo(new LeapSecond(new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero), false)));

            // A day early, as adjtimex(2) would make it at the end of the day.
            Assert.That(LeapSecond.Announced(1, new DateTimeOffset(2026, 12, 30, 23, 0, 0, TimeSpan.Zero)), Is.Null);

            // The 28th of February is not the last day of a leap year's February.
            Assert.That(LeapSecond.Announced(1, new DateTimeOffset(2028, 2, 28, 12, 0, 0, TimeSpan.Zero)), Is.Null);
            Assert.That(LeapSecond.Announced(1, new DateTimeOffset(2028, 2, 29, 12, 0, 0, TimeSpan.Zero))!.MonthEnd,
                        Is.EqualTo(new DateTimeOffset(2028, 3, 1, 0, 0, 0, TimeSpan.Zero)));

            // In UTC, whatever the offset it is seen at.
            Assert.That(LeapSecond.Announced(1, new DateTimeOffset(2027, 1, 1, 0, 30, 0, TimeSpan.FromHours(1))), Is.Not.Null);

            Assert.That(LeapSecond.Announced(0, new DateTimeOffset(2026, 12, 31, 10, 0, 0, TimeSpan.Zero)), Is.Null);
            Assert.That(LeapSecond.Announced(3, new DateTimeOffset(2026, 12, 31, 10, 0, 0, TimeSpan.Zero)), Is.Null);

        }

        [Test]
        public void ALeapSecondIsTakenUpWhereMoreThanHalfOfTheServersAnnounceIt()
        {

            Assert.That(UpstreamDiscipline.LeapVote([ 1, 1, 1, 0 ]), Is.EqualTo(1));
            Assert.That(UpstreamDiscipline.LeapVote([ 2, 2, 1 ]),    Is.EqualTo(2));
            Assert.That(UpstreamDiscipline.LeapVote([ 1 ]),          Is.EqualTo(1));

            // Half is not more than half, and a server announcing nothing - or the
            // opposite - is a voice against it. An unsynchronized one (3) never
            // gets this far, Norn's client turns it away; were it to, it too would
            // be a voice against.
            Assert.That(UpstreamDiscipline.LeapVote([ 1, 1, 0, 0 ]), Is.EqualTo(0));
            Assert.That(UpstreamDiscipline.LeapVote([ 1, 2 ]),       Is.EqualTo(0));
            Assert.That(UpstreamDiscipline.LeapVote([ 1, 3 ]),       Is.EqualTo(0));
            Assert.That(UpstreamDiscipline.LeapVote([]),             Is.EqualTo(0));

        }

        #endregion

    }

}
