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

using org.GraphDefined.Vanaheimr.CLI;
using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.NTSServer.CommandLine
{

    /// <summary>
    /// The master keys the cookies are sealed with: listed, and rotated.
    /// </summary>
    /// <remarks>
    /// <code>
    /// masterKeys                  every master key kept: its id, and when it seals and opens cookies
    /// masterKeys rotate           seal with a new key from now on; the earlier ones still open their cookies
    /// masterKeys rotate forget    the same, and forget every earlier key at once: every client makes a new key exchange
    /// </code>
    /// "Rotate the cookie master key" of section 8.2 of the white paper "NTS Key
    /// Establishment over SSH". A key never leaves the server: what is shown is
    /// its id and its validity, never its value. The rotation is written to the
    /// key file and into the log, with whoever rotated.
    /// </remarks>
    /// <param name="CLI">The command line of the NTS server.</param>
    public class MasterKeysCommand(NTSServerCLI CLI) : ACLICommand<NTSServerCLI>(CLI),
                                                       ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as: "MasterKeysCommand" without its last seven
        /// characters.
        /// </summary>
        public static readonly String CommandName = nameof(MasterKeysCommand)[..^7].ToLowerFirstChar();

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)
        {

            if (Arguments.Length == 1)
                return CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                           ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                           : [];

            if (Arguments.Length == 2 && "rotate".StartsWith(Arguments[1], StringComparison.OrdinalIgnoreCase))
                return [ SuggestionResponse.ParameterCompleted($"{CommandName} rotate") ];

            if (Arguments.Length == 3 && Arguments[1].Equals("rotate", StringComparison.OrdinalIgnoreCase) &&
                "forget".StartsWith(Arguments[2], StringComparison.OrdinalIgnoreCase))
                return [ SuggestionResponse.ParameterCompleted($"{CommandName} rotate forget") ];

            return [];

        }

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override Task<String[]> Execute(String[]           Arguments,
                                               CancellationToken  CancellationToken)
        {

            var host = cli.NTSServer.Host;

            if (Arguments.Length == 1)
            {

                if (!cli.MayDo(Permission.Read(NTSServerAccess.TimeServer), CommandName, out var refusedRead))
                    return Task.FromResult<String[]>([ refusedRead ]);

                return Task.FromResult(Listed(host));

            }

            var forget = Arguments.Length == 3 && Arguments[2].Equals("forget", StringComparison.OrdinalIgnoreCase);

            if (!Arguments[1].Equals("rotate", StringComparison.OrdinalIgnoreCase) || (Arguments.Length == 3 && !forget) || Arguments.Length > 3)
                return Task.FromResult<String[]>([ Help() ]);

            if (!cli.MayDo(Permission.Edit(NTSServerAccess.TimeServer), CommandName, out var refused))
                return Task.FromResult<String[]>([ refused ]);

            if (host.RotateMasterKey(ForgetTheOthers: forget) is not MasterKeyInfo rotated)
                return Task.FromResult<String[]>([ "Nothing is answered at the moment, so no cookie is sealed: there is no master key to rotate." ]);

            var said = forget
                           ? $"The cookies are sealed with the new master key {rotated.Id} from now on, and every earlier key is forgotten: " +
                              "their cookies are refused, and every client makes a new key exchange."
                           : $"The cookies are sealed with the new master key {rotated.Id} from now on. The earlier keys still open their cookies " +
                              "until their grace period is over.";

            cli.NTSServer.Log.Notice($"{said} ({cli.Caller})", "ntsserver", "keys");

            return Task.FromResult<String[]>([ said ]);

        }

        #endregion


        #region (private static) Listed(Host)

        private static String[] Listed(TimeServerHost Host)
        {

            var keys = Host.MasterKeys;

            if (keys.Count == 0)
                return [ "Nothing is answered at the moment, so no master key is in use." ];

            return [
                       Host.MasterKeysFile is not null
                           ? $"{keys.Count} master key(s), kept in {Host.MasterKeysFile}:"
                           : $"{keys.Count} master key(s), in memory only:",
                       .. keys.Select(key => $"    {key.Id,6}  {key.NotBefore:yyyy-MM-dd HH:mm} to {key.NotAfter:yyyy-MM-dd HH:mm} UTC" +
                                             (key.IsCurrent ? "  seals the cookies made now" : "  opens the cookies it sealed"))
                   ];

        }

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} [rotate [forget]] - the master keys the cookies are sealed with; 'rotate' seals with a new one from now on, " +
                "'forget' forgets the earlier ones at once";

        #endregion

    }

}
