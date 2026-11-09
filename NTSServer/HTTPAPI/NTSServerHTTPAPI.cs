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

using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.WWCP.Node.Logging;

#endregion

namespace cloud.charging.open.NTSServer
{

    /// <summary>
    /// The JSON API of an NTS server: what every node's has, and the time
    /// server at "/api/v1/ntsServer".
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>GET  /api/v1/ntsServer             - how the time server stands: what it listens on, where its time comes from, how far that was from the upstream servers, and how much it answered.</item>
    ///   <item>GET  /api/v1/ntsServer/settings    - the "ntsServer" section as it is in effect.</item>
    ///   <item>PUT  /api/v1/ntsServer/settings    - the section or any part of it but "ssh": in effect at once, and saved - answered with { "settings", "changed", "notes" }.</item>
    ///   <item>POST /api/v1/ntsServer/sync        - ask the upstream servers now, in upstream mode.</item>
    ///   <item>GET  /api/v1/ntsServer/ssh         - NTS-KE over SSH, the NTS clients with their keys, and what each login made of it since the start.</item>
    ///   <item>PUT  /api/v1/ntsServer/ssh         - { "enabled", "clients": [ { "name", "keys", "enabled" } ] }: NTS-KE over SSH, and who may use it.</item>
    ///   <item>POST /api/v1/ntsServer/masterKeys/rotate - { "forget" }: seal the cookies with a new master key from now on, and forget the earlier ones at once where asked.</item>
    /// </list>
    /// </remarks>
    public class NTSServerHTTPAPI : NodeHTTPAPI
    {

        #region Properties

        /// <summary>
        /// The NTS server this API speaks for.
        /// </summary>
        public NTSServerNode  NTSServer  { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create and register the JSON API within the given HTTP server.
        /// </summary>
        public NTSServerHTTPAPI(HTTPServer     HTTPServer,
                                NTSServerNode  Node,
                                HTTPExtAPI     ExtAPI,
                                EventLog       Log,
                                HTTPPath?      APIPath   = null,
                                String?        Version   = null)

            : base(HTTPServer,
                   Node,
                   ExtAPI,
                   Log,
                   APIPath,
                   Version ?? typeof(NTSServerHTTPAPI).Assembly.GetName().Version?.ToString(3) ?? "0.0.0")

        {

            this.NTSServer = Node;

            var root = HTTPPath.Root + "v1/ntsServer";

            AddHandler(root,                GetStatus,    HTTPMethod.GET);
            AddHandler(root + "/settings",  GetSettings,  HTTPMethod.GET);
            AddHandler(root + "/settings",  PutSettings,  HTTPMethod.PUT);
            AddHandler(root + "/sync",      PostSync,     HTTPMethod.POST);
            AddHandler(root + "/ssh",       GetSSH,       HTTPMethod.GET);
            AddHandler(root + "/ssh",       PutSSH,       HTTPMethod.PUT);
            AddHandler(root + "/masterKeys/rotate", PostRotateMasterKey, HTTPMethod.POST);

        }

        #endregion


        #region (private) GetStatus(Request)

        private Task<HTTPResponse> GetStatus(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(NTSServerAccess.TimeServer), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, NTSServer.StatusJSON()));

        }

        #endregion

        #region (private) GetSettings(Request)

        private Task<HTTPResponse> GetSettings(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(NTSServerAccess.TimeServer), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, NTSServer.Settings.ToJSON()));

        }

        #endregion

        #region (private) PutSettings(Request)

        /// <summary>
        /// The time server's own settings, changed: in effect at once, and kept
        /// in the configuration file - see <see cref="NTSServerNode.ChangeSettingsAsync"/>.
        /// </summary>
        private async Task<HTTPResponse> PutSettings(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(NTSServerAccess.TimeServer), true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var badRequest))
                return badRequest;

            var change = await NTSServer.ChangeSettingsAsync(json, user.Id.ToString());

            if (change.Refused    is String invalid)
                return ErrorJSON(Request, HTTPStatusCode.BadRequest,          invalid);

            if (change.NotApplied is String notApplied)
                return ErrorJSON(Request, HTTPStatusCode.Conflict,            $"Nothing was changed: {notApplied}");

            if (change.NotSaved   is String notSaved)
                return ErrorJSON(Request, HTTPStatusCode.InternalServerError, $"Nothing was changed, since it could not be saved: {notSaved}");

            return JSONResponse(Request, HTTPStatusCode.OK, change.ToJSON());

        }

        #endregion

        #region (private) PostSync(Request)

        private async Task<HTTPResponse> PostSync(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Run(NodeResources.NTS), true, out var user, out var refused))
                return refused;

            if (NTSServer.Upstream is not UpstreamDiscipline upstream)
                return ErrorJSON(Request, HTTPStatusCode.Conflict, "This NTS server hands out the time of this machine: there are no upstream servers to ask.");

            Log.Info($"'{user.Id}' asked the upstream time servers now.", "ntsserver", "upstream");

            var status = await upstream.PollAsync();

            return JSONResponse(Request, HTTPStatusCode.OK, status.ToJSON(NTSServer.Clock.Base.GetUtcNow()));

        }

        #endregion

        #region (private) GetSSH(Request)

        /// <summary>
        /// NTS-KE over SSH and the NTS clients, each key with its fingerprint:
        /// what the NTS clients page shows.
        /// </summary>
        private Task<HTTPResponse> GetSSH(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(NTSServerAccess.TimeServer), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, NTSServer.SSHJSON()));

        }

        #endregion

        #region (private) PutSSH(Request)

        private Task<HTTPResponse> PutSSH(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(NTSServerAccess.TimeServer), true, out var user, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var badRequest))
                return Task.FromResult(badRequest);

            if (json["enabled"] is JToken enabledToken && enabledToken.Type != JTokenType.Null)
            {

                if (enabledToken.Type != JTokenType.Boolean)
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, "\"enabled\" is true or false."));

                if (enabledToken.Value<Boolean>() != NTSServer.Settings.SSHEnabled &&
                    !NTSServer.TrySetSSHEnabled(enabledToken.Value<Boolean>(), out var notSaved))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.InternalServerError, notSaved));

            }

            if (json["clients"] is JToken clientsToken && clientsToken.Type != JTokenType.Null)
            {

                if (clientsToken is not JArray clientsArray)
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, "\"clients\" is a list of { \"name\", \"keys\", \"enabled\" }."));

                var clients = new List<NTSClientEntry>();

                foreach (var clientToken in clientsArray)
                {

                    if (!TimeServerSettings.TryParseClient(clientToken, out var client, out var error))
                        return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

                    if (clients.Any(known => String.Equals(known.Name, client.Name, StringComparison.OrdinalIgnoreCase)))
                        return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, $"The NTS client '{client.Name}' is listed twice."));

                    clients.Add(client);

                }

                if (!NTSServer.TrySetSSHClients(clients, out var notSaved))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.InternalServerError, notSaved));

                Log.Notice($"'{user.Id}' set the NTS clients that may make key exchanges over SSH: {(clients.Count == 0 ? "none" : String.Join(", ", clients.Select(client => client.Name)))}.",
                           "ntsserver", "ssh", "auth");

            }

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, NTSServer.SSHJSON()));

        }

        #endregion

        #region (private) PostRotateMasterKey(Request)

        /// <summary>
        /// Seal the cookies with a new master key from now on - the "rotate the
        /// cookie master key" of section 8.2 of the white paper "NTS Key
        /// Establishment over SSH", as the command line's "masterKeys rotate".
        /// The earlier keys still open their cookies until their grace period
        /// is over; with { "forget": true } they are forgotten at once, for a
        /// key that may have been read, and every client makes a new key
        /// exchange. Answered with the new key and every key kept - never a
        /// value.
        /// </summary>
        private Task<HTTPResponse> PostRotateMasterKey(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(NTSServerAccess.TimeServer), true, out var user, out var refused))
                return Task.FromResult(refused);

            var forget = false;

            if (!String.IsNullOrWhiteSpace(Request.HTTPBodyAsUTF8String))
            {

                if (!TryParseJSONObject(Request, out var json, out var badRequest))
                    return Task.FromResult(badRequest);

                if (json["forget"] is JToken forgetToken && forgetToken.Type != JTokenType.Null)
                {

                    if (forgetToken.Type != JTokenType.Boolean)
                        return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, "\"forget\" is true or false."));

                    forget = forgetToken.Value<Boolean>();

                }

            }

            if (NTSServer.Host.RotateMasterKey(ForgetTheOthers: forget) is not MasterKeyInfo rotated)
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.Conflict, "Nothing is answered at the moment, so no cookie is sealed: there is no master key to rotate."));

            Log.Notice(forget
                           ? $"'{user.Id}' sealed the cookies with the new master key {rotated.Id} and forgot every earlier key: their cookies are refused, and every client makes a new key exchange."
                           : $"'{user.Id}' sealed the cookies with the new master key {rotated.Id}. The earlier keys still open their cookies until their grace period is over.",
                       "ntsserver", "keys");

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, new JObject(
                                                                                new JProperty("rotated",     NTSServerNode.MasterKeyJSON(rotated)),
                                                                                new JProperty("forgotten",   forget),
                                                                                new JProperty("masterKeys",  NTSServer.MasterKeysJSON())
                                                                            )));

        }

        #endregion

    }

}
