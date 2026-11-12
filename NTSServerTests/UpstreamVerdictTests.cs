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

using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Norn.Monitoring;
using org.GraphDefined.Vanaheimr.Norn.TimeSync;

#endregion

namespace cloud.charging.open.NTSServer.Tests
{

    /// <summary>
    /// What upstream mode makes of a round whose servers disagree: no time
    /// where no majority of them agrees, said as such, and a description built
    /// only on a server of the majority where one outvoted the rest.
    /// </summary>
    /// <remarks>
    /// The verdicts are Norn's, made from answers written here; what is asked
    /// is what this server makes of them. Under de-DE, as the machines these
    /// servers are written on run, so that a decimal comma would show.
    /// </remarks>
    [TestFixture]
    [SetCulture("de-DE")]
    public class UpstreamVerdictTests
    {

        #region (private static) Answer(Name, OffsetMilliseconds)

        /// <summary>
        /// One server that answered, authenticated, with the given offset.
        /// </summary>
        private static NTSMeasurementResult Answer(String  Name,
                                                   Double  OffsetMilliseconds)

            => new (DomainName.Parse(Name),
                    Guid.Empty) {

                   Success  = true,
                   NTP      = new NTPMeasurementResult {
                                  Success                 = true,
                                  NTSAuthenticationValid  = true,
                                  Offset                  = TimeSpan.FromMilliseconds(OffsetMilliseconds)
                              }

               };

        #endregion


        #region ServersThatDisagreeAreSaidToDisagree()

        /// <summary>
        /// Two servers that both answered, further apart than the group allows,
        /// are no time - and the problem says that they disagree, not that
        /// nobody answered.
        /// </summary>
        [Test]
        public void ServersThatDisagreeAreSaidToDisagree()
        {

            var verdict = TimeSyncVerdict.From(
                              [ Answer("a.example", 1.0), Answer("b.example", 3.2) ],
                              MinServers:    2,
                              MaxDeviation:  TimeSpan.FromMilliseconds(1)
                          );

            Assert.Multiple(() => {

                Assert.That(verdict.Outcome,  Is.EqualTo(TimeSyncOutcome.ServersDisagree),  "the test's own premise");

                Assert.That(UpstreamDiscipline.WhyNotUsable(verdict, "legal"),
                            Is.EqualTo("the servers of the NTS group 'legal' disagree - " +
                                       "2 server(s) answered and disagree: spread 2.2 ms, 1 within the agreed deviation"));

                // The others as they were.
                Assert.That(UpstreamDiscipline.WhyNotUsable(TimeSyncVerdict.From([ Answer("a.example", 1.0) ], MinServers: 2, MaxDeviation: TimeSpan.FromSeconds(60)), "legal"),
                            Is.EqualTo("1 of the NTS group 'legal' answered, and 2 must"));

                Assert.That(UpstreamDiscipline.WhyNotUsable(TimeSyncVerdict.From([], MinServers: 2, MaxDeviation: TimeSpan.FromSeconds(60)), "legal"),
                            Is.EqualTo("nobody of the NTS group 'legal' answered"));

            });

        }

        #endregion

        #region TheSystemPeerIsOneOfTheMajority()

        /// <summary>
        /// Where two servers outvote a third, the time is theirs, and so is the
        /// server this one describes itself by - even when the outvoted one was
        /// named last time.
        /// </summary>
        [Test]
        public void TheSystemPeerIsOneOfTheMajority()
        {

            var a       = Answer("a.example", 1.0);
            var b       = Answer("b.example", 1.4);
            var c       = Answer("c.example", 3.2);

            var verdict = TimeSyncVerdict.From([ a, b, c ], MinServers: 2, MaxDeviation: TimeSpan.FromMilliseconds(1));

            Assert.That(verdict.IsUsable,  Is.True,  "the test's own premise");

            var median  = verdict.Offset!.Value;
            var max     = TimeSpan.FromMilliseconds(1);

            Assert.Multiple(() => {

                // The outvoted one, though it was named last time, is let go of.
                Assert.That(UpstreamDiscipline.SystemPeer([ a, b, c ], "c.example", median, max),  Is.SameAs(a).Or.SameAs(b));

                // One of the majority named last time stays named.
                Assert.That(UpstreamDiscipline.SystemPeer([ a, b, c ], "b.example", median, max),  Is.SameAs(b));

                // Nobody named: the closest of the majority.
                Assert.That(UpstreamDiscipline.SystemPeer([ a, b, c ], null,        median, max),  Is.SameAs(a).Or.SameAs(b));

                // Where all agree, the one named last time, whichever is closer.
                Assert.That(UpstreamDiscipline.SystemPeer([ a, b, c ], "c.example", TimeSpan.FromMilliseconds(1.4), TimeSpan.FromSeconds(60)),
                            Is.SameAs(c));

            });

        }

        #endregion

    }

}
