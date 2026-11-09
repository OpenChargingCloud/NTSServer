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
using System.Security.Cryptography;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Norn.NTP;

#endregion

namespace cloud.charging.open.NTSServer
{

    /// <summary>
    /// What this server says of its clock in every answer - the header fields
    /// of RFC 5905 § 7.3 that describe the clock rather than the exchange.
    /// </summary>
    /// <remarks>
    /// Fixed for as long as one Norn NTS server runs, which takes them when it
    /// is made: a description that changes is a server made anew. So it is
    /// made anew only when the description changes in a way a client has to
    /// know - see <see cref="TimeServerHost"/>.
    /// </remarks>
    /// <param name="Stratum">The stratum: 1 for a primary server, one more than its upstream server otherwise.</param>
    /// <param name="ReferenceId">The reference: four characters at stratum 1, the upstream server's address above it.</param>
    /// <param name="ReferenceText">The reference as a sentence names it: "LOCL", or the upstream server's name.</param>
    /// <param name="RootDelay">The round trip to the primary reference.</param>
    /// <param name="RootDispersion">The largest error admitted relative to the primary reference.</param>
    /// <param name="LeapIndicator">0, or 1 or 2 announcing a leap second; 3 is never handed out, see the remarks of <see cref="TimeServerHost"/>.</param>
    public sealed record ClockDescription(Byte                 Stratum,
                                          ReferenceIdentifier  ReferenceId,
                                          String               ReferenceText,
                                          TimeSpan             RootDelay,
                                          TimeSpan?            RootDispersion,
                                          Byte                 LeapIndicator = 0)
    {

        #region (static) Local(Settings)

        /// <summary>
        /// The clock of this machine, described as the "local" of the section says.
        /// </summary>
        public static ClockDescription Local(LocalClockSettings Settings)

            => new (Settings.Stratum,
                    ReferenceIdentifier.From(Settings.ReferenceId),
                    Settings.ReferenceId,
                    TimeSpan.Zero,
                    Settings.RootDispersion);

        #endregion

        #region (static) ReferenceIdOf(Address)

        /// <summary>
        /// The reference identifier naming an upstream server, RFC 5905 § 7.3:
        /// its IPv4 address, or the first four octets of the MD5 hash of its
        /// IPv6 address - which is not for security, but what lets a server
        /// below find out that it would be its own upstream server.
        /// </summary>
        public static ReferenceIdentifier ReferenceIdOf(IIPAddress? Address)
        {

            if (Address is null)
                return ReferenceIdentifier.From("UPST");

            var bytes = Address.ToDotNet().GetAddressBytes();

            if (bytes.Length == 4)
                return ReferenceIdentifier.From(bytes);

            return ReferenceIdentifier.From(MD5.HashData(bytes)[..4]);

        }

        #endregion

        #region IsSameFor(Other)

        /// <summary>
        /// Whether a client would see the same clock in both: the same stratum,
        /// reference and leap indicator, and root delay and dispersion that
        /// differ by less than a factor of two.
        /// </summary>
        /// <remarks>
        /// Root delay and dispersion move with every measurement, and a server
        /// made anew for each would be made anew every minute. A factor of two
        /// is what changes a client's root distance enough to matter.
        /// </remarks>
        public Boolean IsSameFor(ClockDescription Other)

            => Stratum       == Other.Stratum       &&
               ReferenceId   == Other.ReferenceId   &&
               LeapIndicator == Other.LeapIndicator &&
               Close(RootDelay,      Other.RootDelay)  &&
               Close(RootDispersion ?? TimeSpan.Zero, Other.RootDispersion ?? TimeSpan.Zero);

        private static Boolean Close(TimeSpan A, TimeSpan B)
        {

            var a = Math.Abs(A.TotalSeconds);
            var b = Math.Abs(B.TotalSeconds);

            if (a < 0.001 && b < 0.001)
                return true;

            return Math.Max(a, b) <= 2 * Math.Min(a, b);

        }

        #endregion

        #region ToJSON()

        public JObject ToJSON()

            => new (
                   new JProperty("stratum",           Stratum),
                   new JProperty("referenceId",       ReferenceText),
                   new JProperty("rootDelayMs",       Math.Round(RootDelay.TotalMilliseconds, 3)),
                   new JProperty("rootDispersionMs",  RootDispersion.HasValue ? Math.Round(RootDispersion.Value.TotalMilliseconds, 3) : null),
                   new JProperty("leapIndicator",     LeapIndicator)
               );

        #endregion

        #region (override) ToString()

        /// <remarks>
        /// Invariant throughout, the dispersion included: under a German
        /// culture a nested interpolation wrote "5,838 ms" into an English
        /// sentence.
        /// </remarks>
        public override String ToString()

            => $"stratum {Stratum}, reference {ReferenceText}, " +
               $"root delay {RootDelay.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} ms, " +
               $"root dispersion {(RootDispersion.HasValue ? RootDispersion.Value.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture) + " ms" : "as measured")}";

        #endregion

    }

}
