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

            // RFC 5905 § 7.3: an IPv4 upstream server is named by its address.
            Assert.That(description.ReferenceId, Is.EqualTo(org.GraphDefined.Vanaheimr.Norn.NTP.ReferenceIdentifier.From([ 192, 0, 2, 1 ])));

        }

    }

}
