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

#endregion

namespace cloud.charging.open.NTSServer
{

    /// <summary>
    /// A leap second at the end of a UTC month: inserted, so that its last
    /// minute has 61 seconds - leap indicator 1 - or deleted, so that it has
    /// 59 - leap indicator 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The NTP timescale has no 23:59:60. An inserted second repeats
    /// 23:59:59: the clock handed out is set back by a second at the midnight
    /// that ends the month, the instant the kernels of the upstream servers
    /// do the same - so that measuring them finds the same difference before
    /// and after. A deleted second skips 23:59:59: the clock is set forward by
    /// a second when it reaches it.
    /// </para>
    /// <para>
    /// An announcement counts only on the last day of a month. A client that
    /// hands the leap indicator to its kernel - adjtimex(2)'s STA_INS and
    /// STA_DEL - has the second inserted or deleted at the end of the current
    /// day, whatever day that is, so an announcement passed on a day early
    /// would have it made a day early. And an upstream server still announcing
    /// on the first day of the next month, after the second was made, is not
    /// taken to announce another one at its end.
    /// </para>
    /// </remarks>
    /// <param name="MonthEnd">The midnight that ends the month: the first instant of the next one, UTC.</param>
    /// <param name="Inserted">Whether the second is inserted (leap indicator 1) or deleted (2).</param>
    public sealed record LeapSecond(DateTimeOffset  MonthEnd,
                                    Boolean         Inserted)
    {

        #region Properties

        /// <summary>
        /// The leap indicator announcing it: 1 for an inserted second, 2 for a
        /// deleted one.
        /// </summary>
        public Byte            LeapIndicator
            => Inserted ? (Byte) 1 : (Byte) 2;

        /// <summary>
        /// When it is made, as the clock handed out reads before it: the
        /// midnight for an inserted second, the second before it for a
        /// deleted one.
        /// </summary>
        public DateTimeOffset  At
            => Inserted ? MonthEnd : MonthEnd - TimeSpan.FromSeconds(1);

        /// <summary>
        /// By how much the clock handed out moves when it is made: back by a
        /// second for an inserted one, forward by a second for a deleted one.
        /// </summary>
        public TimeSpan        Jump
            => TimeSpan.FromSeconds(Inserted ? -1 : 1);

        #endregion


        #region (static) Announced(LeapIndicator, Now)

        /// <summary>
        /// The leap second a leap indicator announces when it is seen at the
        /// given time - or null where it announces none, or where the day is
        /// not the last of its month.
        /// </summary>
        /// <param name="LeapIndicator">The leap indicator: 1 or 2 announce one, 0 and 3 do not.</param>
        /// <param name="Now">When it is seen, on the clock handed out.</param>
        public static LeapSecond? Announced(Byte            LeapIndicator,
                                            DateTimeOffset  Now)
        {

            if (LeapIndicator is not (1 or 2))
                return null;

            var utc       = Now.ToUniversalTime();
            var today     = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
            var tomorrow  = today.AddDays(1);

            return tomorrow.Month != today.Month
                       ? new LeapSecond(tomorrow, LeapIndicator == 1)
                       : null;

        }

        #endregion

        #region ToJSON()

        public JObject ToJSON()

            => new (
                   new JProperty("day",            MonthEnd.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                   new JProperty("inserted",       Inserted),
                   new JProperty("leapIndicator",  LeapIndicator)
               );

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"a leap second {(Inserted ? "inserted" : "deleted")} at the end of " +
               $"{MonthEnd.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} UTC";

        #endregion

    }

}
