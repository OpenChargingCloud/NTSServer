import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { html } from '@node/view';
import { nodeMenu, startNode } from '@node/start';

import { configurationPage } from './pages/configuration';
import { ntsClientsPage } from './pages/ntsClients';
import { timeServerPage } from './pages/timeServer';

// What an NTS server has pages for of its own is the time server - what it
// hands out, where from, NTS-KE over TLS and what it answered - at
// "/ntsServer", first in the menu, and below it the machines that may make
// key exchanges over SSH. Everything else is what every node has: its
// configuration, name resolution, the time servers it follows, the SSH
// server, the node's own certificate store and the log - see WWCP_Node's
// start.ts.
startNode({

    name:  'NTS Server',
    icon:  'fa-clock',

    menu: [
        { path: '/ntsServer', label: 'Time server', icon: 'fa-clock', permission: [ 'timeserver:read' ], children: [
            { path: '/ntsServer/clients', label: 'NTS clients', icon: 'fa-server', permission: [ 'timeserver:read' ] }
        ] },
        nodeMenu.configuration([
            nodeMenu.dns,
            // The time servers it asks - in upstream mode, the time it hands
            // out comes from one of these groups.
            { ...nodeMenu.nts, label: 'Upstream time servers' },
            nodeMenu.ssh,
            { ...nodeMenu.certificates, label: 'Trusted certificates' },
            nodeMenu.identities,
            // What NTS-KE over TLS shows, and the web interface over HTTPS
            // where the "web" section says "tls": { "certificate": "store" }.
            nodeMenu.serverIdentities
        ]),
        nodeMenu.logs
    ],

    // The node's own store is what this server believes - the roots of its
    // upstream time servers - and what it shows: the TLS identity of NTS-KE
    // among them, where one is there.
    certificates: {
        title:  'Trusted certificates',
        hints: {
            believes:  html`
                What this NTS server believes, beside the roots of this machine: an upstream time server or a name
                server with a CA of its own - or a self-signed certificate - is reached through one of these, as a
                TLS root for "nts".
            `,
            presents:  html`
                What it will show a server that asks who it is, with its private key. Kept for when this NTS server
                connects to one that does; nothing here presents it yet.
            `
        }
    },

    // "/" is every node's: the first page of the menu the person signed in may
    // open - the time server for whoever may read it.
    pages: {

        '/configuration':      configurationPage,
        '/ntsServer':          timeServerPage,
        '/ntsServer/clients':  ntsClientsPage

    }

});
