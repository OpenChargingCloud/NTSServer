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

using org.GraphDefined.Vanaheimr.CLI;
using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.NTSServer.CommandLine
{

    /// <summary>
    /// The keys the responses are signed with: listed, made and retired.
    /// </summary>
    /// <remarks>
    /// <code>
    /// signingKeys                 every signing key: its id, from when to when it is valid, and what it is now
    /// signingKeys new             a new key, signing from a day from now on - its public key handed out until then
    /// signingKeys new now         a new key, signing from now on: for a key that may have been read
    /// signingKeys retire &lt;id&gt;     the key signs nothing from now on, and its public key is no longer handed out
    /// </code>
    /// A client asks for a signed response, and learns the public keys from
    /// the key exchange. A private key never leaves the server: what is shown
    /// is its id, its validity and its public key. A new key and a retired one
    /// are written to the key file and into the log, with whoever made or
    /// retired it.
    /// </remarks>
    /// <param name="CLI">The command line of the NTS server.</param>
    public class SigningKeysCommand(NTSServerCLI CLI) : ACLICommand<NTSServerCLI>(CLI),
                                                        ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as: "SigningKeysCommand" without its last seven
        /// characters.
        /// </summary>
        public static readonly String CommandName = nameof(SigningKeysCommand)[..^7].ToLowerFirstChar();

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)
        {

            if (Arguments.Length == 1)
                return CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                           ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                           : [];

            if (Arguments.Length == 2)
                return new[] { "new", "retire" }.
                           Where (word => word.StartsWith(Arguments[1], StringComparison.OrdinalIgnoreCase)).
                           Select(word => SuggestionResponse.ParameterCompleted($"{CommandName} {word}"));

            if (Arguments.Length == 3 && Arguments[1].Equals("new", StringComparison.OrdinalIgnoreCase) &&
                "now".StartsWith(Arguments[2], StringComparison.OrdinalIgnoreCase))
                return [ SuggestionResponse.ParameterCompleted($"{CommandName} new now") ];

            return [];

        }

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override async Task<String[]> Execute(String[]           Arguments,
                                                     CancellationToken  CancellationToken)
        {

            var node = cli.NTSServer;
            var host = node.Host;

            if (Arguments.Length == 1)
            {

                if (!cli.MayDo(Permission.Read(NTSServerAccess.TimeServer), CommandName, out var refusedRead))
                    return [ refusedRead ];

                return Listed(host.SigningKeys);

            }

            var verb   = Arguments[1].ToLowerInvariant();

            var isNew  = verb == "new"    && (Arguments.Length == 2 || (Arguments.Length == 3 && Arguments[2].Equals("now", StringComparison.OrdinalIgnoreCase)));
            var retire = verb == "retire" &&  Arguments.Length == 3;

            if (!isNew && !retire)
                return [ Help() ];

            UInt16 id = 0;

            if (retire && (!UInt16.TryParse(Arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out id) || id == 0))
                return [ $"'{Arguments[2]}' is no id of a signing key: they are numbers from 1 to 65535. {Help()}" ];

            if (!cli.MayDo(Permission.Edit(NTSServerAccess.TimeServer), CommandName, out var refused))
                return [ refused ];

            if (isNew)
            {

                if (await host.NewSigningKeyAsync(AtOnce: Arguments.Length == 3) is not KeyPair made)
                    return [ $"The signing keys in '{host.SigningKeys.File}' could not be read, so no key is added to them." ];

                var said = $"Made {node.SigningKeySaid(made)}.";

                node.Log.Notice($"{said} ({cli.Caller})", "ntsserver", "keys");

                return [ said ];

            }

            if (await host.RetireSigningKeyAsync(id) is not KeyPair retired)
                return [ $"There is no signing key {id} that is not retired already." ];

            var retiredSaid = $"The signing key {retired.Id} signs nothing from now on, and its public key is no longer handed out.";

            node.Log.Notice($"{retiredSaid} ({cli.Caller})", "ntsserver", "keys");

            return [ retiredSaid ];

        }

        #endregion


        #region (private static) Listed(Keys)

        private static String[] Listed(SigningKeys Keys)
        {

            var all = Keys.All;

            if (all.Count == 0)
                return [ Keys.File is not null
                             ? $"No signing key: the file '{Keys.File}' could not be read, so no response is signed."
                             : "No signing key, so no response is signed." ];

            return [
                       Keys.File is not null
                           ? $"{all.Count} signing key(s), kept in {Keys.File}:"
                           : $"{all.Count} signing key(s), in memory only:",
                       .. all.Select(key => String.Create(CultureInfo.InvariantCulture,
                                                $"    {key.Id,5}  {When(key.NotBefore, "always")} to {When(key.NotAfter, "never ends")}  {StateText(Keys.StateOf(key))}" +
                                                $"  {key.SignatureAlgorithm} {Convert.ToBase64String(key.PublicKey)}"))
                   ];

        }

        private static String When(DateTimeOffset? Timestamp, String Otherwise)

            => Timestamp is DateTimeOffset at
                   ? at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
                   : Otherwise;

        private static String StateText(SigningKeyState State)

            => State switch {
                   SigningKeyState.Signs  => "signs now",
                   SigningKeyState.Valid  => "signs where asked for by its id",
                   SigningKeyState.Next   => "not yet - its public key is handed out",
                   _                      => "retired"
               };

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} [new [now] | retire <id>] - the keys the responses are signed with; 'new' makes one that signs from a day from now on, " +
                "'now' at once, 'retire' lets one sign nothing any more";

        #endregion

    }

}
