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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Norn.NTS;

#endregion

namespace cloud.charging.open.NTSServer.SecureShell
{

    /// <summary>
    /// What one login - an NTS client, or an account allowed to run "ntske" -
    /// made of NTS-KE over SSH since this program started: when it was last
    /// served and how, when it was last turned away and why, and how often.
    /// </summary>
    /// <remarks>
    /// The status of section 8.2 of the white paper "NTS Key Establishment over
    /// SSH": the clients served, the cookies handed out, the last handshakes.
    /// Kept in memory only - what lasts is in the log.
    /// </remarks>
    /// <param name="LastServed">The last key exchange that brought cookies, or null.</param>
    /// <param name="LastRefused">The last one that did not, or null.</param>
    /// <param name="Served">How many brought cookies.</param>
    /// <param name="Refused">How many did not.</param>
    /// <param name="Cookies">How many cookies went out in all.</param>
    public sealed record NTSClientActivity(NTSKEOverSSHExchange?  LastServed,
                                           NTSKEOverSSHExchange?  LastRefused,
                                           Int64                  Served,
                                           Int64                  Refused,
                                           Int64                  Cookies)
    {

        /// <summary>
        /// Nothing yet.
        /// </summary>
        public static readonly NTSClientActivity  None = new (null, null, 0, 0, 0);


        /// <summary>
        /// This and the given key exchange.
        /// </summary>
        public NTSClientActivity With(NTSKEOverSSHExchange Exchange)

            => Exchange.Succeeded
                   ? this with { LastServed  = Exchange, Served  = Served  + 1, Cookies = Cookies + Exchange.Cookies }
                   : this with { LastRefused = Exchange, Refused = Refused + 1 };


        /// <summary>
        /// Where the keys of a key exchange came from, as a sentence says it.
        /// </summary>
        public static String TextOf(SSHKeyBindingMethods? Method)

            => Method switch {
                   SSHKeyBindingMethods.Exporter   => "option A, the exporter of the connection",
                   SSHKeyBindingMethods.Transport  => "option B, a transported secret",
                   _                               => "no keys"
               };


        /// <summary>
        /// As the status of the JSON API says it.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("served",       Served),
                   new JProperty("refused",      Refused),
                   new JProperty("cookies",      Cookies),
                   new JProperty("lastServed",   LastServed  is NTSKEOverSSHExchange served
                                                     ? new JObject(
                                                           new JProperty("at",             served.Timestamp.ToString("o")),
                                                           new JProperty("from",           served.Session.Peer?.ToString()),
                                                           new JProperty("method",         served.Method == SSHKeyBindingMethods.Exporter ? "exporter" : "transport"),
                                                           new JProperty("aeadAlgorithm",  served.AEADAlgorithm.HasValue ? (Int32) served.AEADAlgorithm.Value : null),
                                                           new JProperty("cookies",        served.Cookies)
                                                       )
                                                     : null),
                   new JProperty("lastRefused",  LastRefused is NTSKEOverSSHExchange refused
                                                     ? new JObject(
                                                           new JProperty("at",             refused.Timestamp.ToString("o")),
                                                           new JProperty("from",           refused.Session.Peer?.ToString()),
                                                           new JProperty("why",            refused.Problem)
                                                       )
                                                     : null)
               );

    }

}
