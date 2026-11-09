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

#endregion

namespace cloud.charging.open.NTSServer
{

    /// <summary>
    /// What a change of the time server's own settings came to: the settings
    /// in effect afterwards, which of them changed, and what whoever changed
    /// them has to know - or why nothing changed.
    /// </summary>
    /// <param name="Settings">The settings in effect now.</param>
    /// <param name="Changed">What changed, as "ntpPort" or "upstream.group" name it.</param>
    /// <param name="Notes">What whoever changed them has to know: a port clients are to be told, a certificate made anew, a switch that says otherwise at the next start.</param>
    /// <param name="Refused">Why what was asked for is no setting: nothing was changed.</param>
    /// <param name="NotApplied">Why it could not be put into effect - a port somebody else has: nothing was changed.</param>
    /// <param name="NotSaved">Why it could not be written into the configuration file: nothing was changed.</param>
    public sealed record TimeServerSettingsChange(TimeServerSettings     Settings,
                                                  IReadOnlyList<String>  Changed,
                                                  IReadOnlyList<String>  Notes,
                                                  String?                Refused     = null,
                                                  String?                NotApplied  = null,
                                                  String?                NotSaved    = null)
    {

        /// <summary>
        /// Whether it is in effect and saved - or there was nothing to change.
        /// </summary>
        public Boolean IsDone
            => Refused is null && NotApplied is null && NotSaved is null;

        public JObject ToJSON()

            => new (
                   new JProperty("settings",  Settings.ToJSON()),
                   new JProperty("changed",   new JArray(Changed)),
                   new JProperty("notes",     new JArray(Notes))
               );

    }

}
