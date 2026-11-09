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

using System.Net;
using System.Net.Sockets;

#endregion

namespace cloud.charging.open.NTSServer.Tests
{

    /// <summary>
    /// A UDP port for NTP that nobody has.
    /// </summary>
    /// <remarks>
    /// Not TestPorts.Free(), which finds a port by binding it over TCP: Windows
    /// keeps ranges of ports to itself for Hyper-V and WSL, per protocol, and a
    /// port free for TCP may be one of them for UDP - which is then refused
    /// with "access denied". The operating system's own choice of a UDP port
    /// is never one of those.
    /// </remarks>
    public static class UDPPorts
    {

        private static readonly HashSet<Int32> handedOut = [];

        public static Int32 Free()
        {

            lock (handedOut)
            {
                for (var attempt = 0; attempt < 100; attempt++)
                {

                    using var udp  = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                    var port       = ((IPEndPoint) udp.Client.LocalEndPoint!).Port;

                    if (handedOut.Add(port))
                        return port;

                }
            }

            throw new InvalidOperationException("No UDP port was found that this test run had not been handed.");

        }

    }

}
