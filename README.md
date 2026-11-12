# NTSServer

The time server of the OpenChargingCloud: a
[WWCP_Node](https://github.com/OpenChargingCloud/WWCP_Node) that hands out
time over NTS - NTS-KE over TLS 1.3 (RFC 8915) and over SSH (the subsystem
`ntske/1@open.charging.cloud`), and NTP and NTP+NTS - with a JSON API below
`/api/v1/ntsServer` and a web interface at `/ntsServer`. The time is either
the clock of the machine it runs on, or the time of a group of upstream NTS
servers it follows, without ever setting the machine's clock. The protocols
themselves - NTS-KE, NTP, the cookies, the AEAD - are
[Norn](https://github.com/Vanaheimr/Norn)'s.

It is not built alone:
[NTSServerCLI](https://github.com/OpenChargingCloud/NTSServerCLI) pins it
beside the libraries it needs, builds it, and runs its tests. Its README says
how the server is configured and run.

| | |
|---|---|
| `NTSServer/NTSServerNode.cs` | the node: its names, its port (2361), its certificate listeners, and what it is made of |
| `NTSServer/NTSServerAccess.cs` | the resources `timeserver` and `ntske`, and the roles `operator` and `ntsclient` |
| `NTSServer/Settings/` | the `ntsServer` section of the configuration, and what a change of it while running came to |
| `NTSServer/Clock/` | the clock handed out - the machine's, or a free-running one slewed to the upstream servers, making the leap seconds they announce at the end of a month - and what the answers say of it |
| `NTSServer/TimeServerHost.cs` | Norn's NTS server, made anew where that description changes, with the master keys of the cookies in a file and rotated on request, the signing keys handed to every server made, and what each login made of NTS-KE over SSH |
| `NTSServer/SigningKeys.cs` | the keys the responses are signed with, in a file of their own: the first made at the first start, a new one beginning a day later or at once, and one retired |
| `NTSServer/NTSKETLSIdentity.cs` | the certificate NTS-KE over TLS shows: a TLS server identity of the store, or a self-signed one |
| `NTSServer/SecureShell/` | the machines let in for NTS-KE over SSH, by their SSH keys or the user certificates of an SSH CA, and when each was last served |
| `NTSServer/CommandLine/` | what can be typed at the running server - `timeServer`, `masterKeys`, `signingKeys` and `ntsClients` beside every node's commands - at its console and by whoever signs in over SSH, whichever program runs it |
| `NTSServer/HTTPAPI/` | the routes below `v1/ntsServer`, a new master key and a new or retired signing key among them |
| `NTSServer/Frontend/` | the web interface - the time server with the master keys of its cookies and its signing keys, its settings, the NTS clients with when each was last served, the configuration - bundled by webpack with what every node shares, and embedded into the assembly |
| `NTSServerTests/` | the settings and the clock, leap seconds, the server asked by Norn's own client over TLS and SSH and as the upstream server of another - a leap second it announces passed on and made among it, and the server not followed where it says it is not synchronized itself, nor described by a server the others outvoted, and servers that disagree said to disagree - the settings changed while running, the commands of the prompt at the console and over SSH, cookies across a restart, signed responses checked by a client across a restart and a server made anew, the signing keys made and retired at the prompt and over the JSON API, and WWCP_Node's conformance suite asked of an NTS server, its web interface among it; the web interface's own tests are beside its pages, run by `npm test` |


## Your participation

This software is Open Source under the **Affero GPL 3.0 license**.
We appreciate your participation in this ongoing project, and your help to
improve it and the e-mobility ICT in general. If you find bugs, want to
request a feature or send us a pull request, feel free to use the normal
GitHub features to do so. For this please read the Contributor License
Agreement carefully and send us a signed copy or use a similar free and
open license.
