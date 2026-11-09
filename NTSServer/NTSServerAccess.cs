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

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.NTSServer
{

    /// <summary>
    /// What an NTS server adds to the resources every node has, and the roles
    /// of the people - and machines - around it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two resources, because they are two different things to be trusted
    /// with. The time server itself - its listeners, its time source and the
    /// NTS clients it lets in over SSH - is what an operator reads and an
    /// administrator changes. A key exchange over SSH is what a machine that
    /// fetches its cookies here runs, and nothing else.
    /// </para>
    /// <para>
    /// The node brings the viewer, who may look at everything, and the
    /// administrators, who may do everything. The configuration file may add
    /// roles and say differently what one of them may do - see the node's
    /// "roles" section.
    /// </para>
    /// </remarks>
    public static class NTSServerAccess
    {

        #region Resources

        /// <summary>
        /// The time server: read, its listeners, its time source, how far it
        /// is from its upstream servers, how many it answered and when each NTS
        /// client was last served; edited, its settings, the NTS clients it lets
        /// in over SSH and the master keys of its cookies.
        /// </summary>
        public const String  TimeServer   = "timeserver";

        /// <summary>
        /// NTS-KE over SSH: run, the key exchange of the SSH subsystem
        /// "ntske/1@open.charging.cloud", by an account of this node signed in
        /// with one of its SSH keys.
        /// </summary>
        /// <remarks>
        /// For an account. A machine that is nothing but an NTS client is
        /// better one of the NTS clients of the "ntsServer" section, which the
        /// node confines to the key exchange - no command line, no SFTP.
        /// </remarks>
        public const String  KeyExchange  = "ntske";

        /// <summary>
        /// Both.
        /// </summary>
        public static readonly IReadOnlyList<String>  Resources = [ TimeServer, KeyExchange ];

        #endregion

        #region Roles

        /// <summary>
        /// Whoever runs this time server day to day: may look at everything
        /// and ask whether the network works - but may not change what the
        /// server serves, whom it lets in, or what it believes.
        /// </summary>
        public static readonly Role  Operator   = new ("operator",
                                                       [ Permission.Read(Permission.AnyResource),
                                                         Permission.Run (NodeResources.DNS),
                                                         Permission.Run (NodeResources.NTS) ],
                                                       "runs the NTS server day to day: looks at everything, and asks whether the network works");

        /// <summary>
        /// An account that is an NTS client and nothing else: it may make a
        /// key exchange over SSH, and look at nothing.
        /// </summary>
        public static readonly Role  NTSClient  = new ("ntsclient",
                                                       [ Permission.Run(KeyExchange) ],
                                                       "makes NTS key exchanges over SSH, and nothing else");

        /// <summary>
        /// Both, in the order a sentence naming them reads best.
        /// </summary>
        public static readonly IReadOnlyList<Role>  Roles = [ Operator, NTSClient ];

        #endregion

    }

}
