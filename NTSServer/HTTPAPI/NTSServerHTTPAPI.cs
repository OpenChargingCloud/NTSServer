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
    ///   <item>POST /api/v1/ntsServer/sync        - ask the upstream servers now, in upstream mode.</item>
    ///   <item>PUT  /api/v1/ntsServer/ssh         - { "enabled", "clients": [ { "name", "keys", "enabled" } ] }: NTS-KE over SSH, and who may use it.</item>
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
            AddHandler(root + "/sync",      PostSync,     HTTPMethod.POST);
            AddHandler(root + "/ssh",       GetSSH,       HTTPMethod.GET);
            AddHandler(root + "/ssh",       PutSSH,       HTTPMethod.PUT);

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

    }

}
