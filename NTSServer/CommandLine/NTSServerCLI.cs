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

using System.Reflection;

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.CommandLine;

#endregion

namespace cloud.charging.open.NTSServer.CommandLine
{

    /// <summary>
    /// The command line of a running NTS server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Commands are not listed anywhere. The commands every node has - syncNTS
    /// and listNTSGroups among them, which ask and show the upstream servers -
    /// are the node's, and NodeCLI, which this derives from, finds them in
    /// WWCP_Node. The constructor registers this type as well, so that this
    /// assembly is walked for anything that implements ICLICommand and can be
    /// built from an NTSServerCLI: a command of the time server's own is a new
    /// file and nothing else - 'timeServer', 'masterKeys' and 'ntsClients'
    /// among them.
    /// </para>
    /// <para>
    /// Here, beside the time server, and not in the program that runs it:
    /// what an NTS server can be told is the NTS server's, and is told over
    /// SSH as well as at the console. The node hands it to whoever signs in
    /// over SSH by itself (see NTSServerNode.cs), so that an NTS server run by
    /// another program - the tests, the test environment - has the same
    /// commands as one run by the program NTSServerCLI. A program with
    /// commands of its own derives from this one and sets
    /// WWCPNode.CommandLines anew.
    /// </para>
    /// </remarks>
    public class NTSServerCLI : NodeCLI
    {

        #region Properties

        /// <summary>
        /// The NTS server these commands are about.
        /// </summary>
        public NTSServerNode  NTSServer  { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create the command line of the given NTS server, at the console.
        /// </summary>
        public NTSServerCLI(NTSServerNode      NTSServer,
                            params Assembly[]  AssembliesWithCLICommands)

            : base(NTSServer, AssembliesWithCLICommands)

        {

            this.NTSServer = NTSServer;

            RegisterCLIType(typeof(NTSServerCLI));

        }

        /// <summary>
        /// Create the command line of the given NTS server on the given terminal,
        /// for the given caller - a session over SSH.
        /// </summary>
        public NTSServerCLI(NTSServerNode      NTSServer,
                            ICLITerminal       Terminal,
                            CLICaller          Caller,
                            params Assembly[]  AssembliesWithCLICommands)

            : base(NTSServer, Terminal, Caller, AssembliesWithCLICommands)

        {

            this.NTSServer = NTSServer;

            RegisterCLIType(typeof(NTSServerCLI));

        }

        #endregion


        #region (protected override) GetPrompt()

        /// <summary>
        /// Which port this NTS server answers its web interface on, because a
        /// machine that is one of several on a bench should say which one it is
        /// before it asks for a command.
        /// </summary>
        protected override String GetPrompt()

            => $"nts:{NTSServer.HTTPPort}> ";

        #endregion

    }

}
