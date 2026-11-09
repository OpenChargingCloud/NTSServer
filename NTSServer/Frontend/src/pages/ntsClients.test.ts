/**
 * The NTS clients page drawn, in a document of happy-dom, against a stand-in
 * NTS server that keeps what it is sent: a client added, another key of one,
 * one switched off and removed, NTS-KE over SSH switched off - each sent as
 * the whole list, and drawn as the server answers. And for whoever may only
 * look, nothing that changes anything.
 */

import { asked, field, open, refused, said, submit, type, until, type Asked } from '../../test/ntsServer.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

const { ntsClientsPage, entriesOf, oneLine } = await import('./ntsClients.ts');


interface Entry { name: string; keys: string[]; enabled: boolean }

const keyOf = (Name: string) => `ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI${Name} ${Name}`;

let enabled  = true;
let clients: Entry[] = [];

function ssh(): Record<string, unknown> {

    return {
        enabled,
        served:     enabled,
        subsystem:  'ntske/1@open.charging.cloud',
        url:        'ssh://192.168.1.2:22361',
        hostKey:    'SHA256:host',
        clients:    clients.map(client => ({
                        name:     client.name,
                        enabled:  client.enabled,
                        keys:     client.keys.map(line => ({ line, algorithm: 'ssh-ed25519', fingerprint: `SHA256:${line.split(' ')[2]}`, comment: line.split(' ')[2], options: [] }))
                    }))
    };

}

function ntsServer({ method, path, body }: Asked): unknown {

    if (path !== '/ntsServer/ssh')
        return undefined;

    if (method === 'PUT') {

        const change = body as { enabled?: boolean; clients?: Entry[] };

        if (change.clients?.some(client => client.keys.some(key => !key.startsWith('ssh-'))))
            return refused(400, 'That is no authorized_keys line.');

        enabled = change.enabled ?? enabled;
        clients = change.clients ?? clients;

    }

    return ssh();

}

function puts(): { enabled?: boolean; clients?: Entry[] }[] {
    return asked.filter(one => one.method === 'PUT').map(one => one.body as { enabled?: boolean; clients?: Entry[] });
}


describe('the NTS clients', () => {

    it('adds a client with its first key, and says so', async () => {

        enabled = true;
        clients = [];

        const root = await open(ntsClientsPage, '/ntsServer/clients', [ 'timeserver:read', 'timeserver:edit' ],
                                ntsServer, root => root.querySelector('#no-clients') !== null);

        assert.match(root.querySelector('#ssh-served')!.textContent!, /served/);
        assert.equal(root.querySelector('#add-key'), null, 'another key of a client, with no client');

        type(field(root, '#add-client', 'name'), 'cs001');
        type(field<HTMLTextAreaElement>(root, '#add-client', 'key'), keyOf('cs001'));
        submit(root, '#add-client');

        await until(() => root.querySelector('tr[data-client="cs001"]') !== null, 'the client was not drawn');

        assert.deepEqual(puts(), [ { clients: [ { name: 'cs001', keys: [ keyOf('cs001') ], enabled: true } ] } ]);
        assert.match(root.querySelector('#add-client-note')!.textContent!, /cs001 may make key exchanges now\. Saved, and in effect\./);
        assert.equal(field(root, '#add-client', 'name').value, '', 'the form kept what was sent');
        assert.match(root.querySelector('tr[data-client="cs001"]')!.textContent!, /SHA256:cs001/);

    });

    it('refuses a second client of the same name before it asks the server', async () => {

        clients = [ { name: 'cs001', keys: [ keyOf('cs001') ], enabled: true } ];

        const root = await open(ntsClientsPage, '/ntsServer/clients', [ 'timeserver:read', 'timeserver:edit' ],
                                ntsServer, root => root.querySelector('tr[data-client="cs001"]') !== null);

        type(field(root, '#add-client', 'name'), 'CS001');
        type(field<HTMLTextAreaElement>(root, '#add-client', 'key'), keyOf('other'));
        submit(root, '#add-client');

        await until(() => root.querySelector('#add-client-error')!.textContent !== '', 'nothing was said');

        assert.match(root.querySelector('#add-client-error')!.textContent!, /There is a client "CS001" already/);
        assert.equal(puts().length, 0);

    });

    it('says what the server refused, and keeps what was typed', async () => {

        clients = [];

        const root = await open(ntsClientsPage, '/ntsServer/clients', [ 'timeserver:read', 'timeserver:edit' ],
                                ntsServer, root => root.querySelector('#add-client') !== null);

        type(field(root, '#add-client', 'name'), 'cs009');
        type(field<HTMLTextAreaElement>(root, '#add-client', 'key'), 'not a key');
        submit(root, '#add-client');

        await until(() => root.querySelector('#add-client-error')!.textContent !== '', 'the refusal was not said');

        assert.match(root.querySelector('#add-client-error')!.textContent!, /no authorized_keys line/);
        assert.equal(field(root, '#add-client', 'name').value, 'cs009');

    });

    it('adds another key, switches a client off and removes it - each as the whole list', async () => {

        clients = [ { name: 'cs001', keys: [ keyOf('cs001') ], enabled: true },
                    { name: 'cs002', keys: [ keyOf('cs002') ], enabled: true } ];

        const root = await open(ntsClientsPage, '/ntsServer/clients', [ 'timeserver:read', 'timeserver:edit' ],
                                ntsServer, root => root.querySelector('#add-key') !== null);

        const choice = field<HTMLSelectElement>(root, '#add-key', 'client');
        choice.value = 'cs002';
        type(field<HTMLTextAreaElement>(root, '#add-key', 'key'), keyOf('cs002b'));
        submit(root, '#add-key');

        await until(() => /cs002 has another key/.test(root.querySelector('#add-key-note')!.textContent!), 'the key was not added');

        assert.deepEqual(clients.find(client => client.name === 'cs002')!.keys, [ keyOf('cs002'), keyOf('cs002b') ]);

        root.querySelector<HTMLButtonElement>('button[data-switch="cs001"]')!.click();

        await until(() => clients.find(client => client.name === 'cs001')!.enabled === false, 'cs001 was not switched off');
        await until(() => root.querySelector('tr[data-client="cs001"].switched-off') !== null, 'cs001 is not drawn as switched off');

        root.querySelector<HTMLButtonElement>('button[data-remove="cs002"]')!.click();

        await until(() => root.querySelector('tr[data-client="cs002"]') === null, 'cs002 was not removed');

        assert.ok(said.some(text => /Remove cs002\?/.test(text)), 'nobody was asked before cs002 went');
        assert.deepEqual(clients.map(client => client.name), [ 'cs001' ]);
        assert.equal(puts().length, 3);

    });

    it('switches NTS-KE over SSH off, and says it is off', async () => {

        enabled = true;
        clients = [];

        const root = await open(ntsClientsPage, '/ntsServer/clients', [ 'timeserver:read', 'timeserver:edit' ],
                                ntsServer, root => root.querySelector('#switch-ssh') !== null);

        root.querySelector<HTMLButtonElement>('#switch-ssh')!.click();

        await until(() => /off/.test(root.querySelector('#ssh-served')!.textContent!), 'it was not drawn as off');

        assert.deepEqual(puts(), [ { enabled: false } ]);
        assert.match(root.querySelector('#switch-ssh')!.textContent!, /Switch NTS-KE over SSH on/);

    });

    it('changes nothing for whoever may only look, and says why', async () => {

        enabled = true;
        clients = [ { name: 'cs001', keys: [ keyOf('cs001') ], enabled: true } ];

        const root = await open(ntsClientsPage, '/ntsServer/clients', [ 'timeserver:read' ],
                                ntsServer, root => root.querySelector('tr[data-client="cs001"]') !== null);

        assert.match(root.querySelector('.notice')!.textContent!, /may look at the NTS clients but not change them/);

        for (const button of root.querySelectorAll<HTMLButtonElement>('#switch-ssh, button[data-switch], button[data-remove], button[data-remove-key], #add-client button[type="submit"]'))
            assert.equal(button.disabled, true, `${button.outerHTML} can be pressed`);

    });

    it('says where the SSH server listens on this machine only', async () => {

        enabled = true;
        clients = [];

        const root = await open(ntsClientsPage, '/ntsServer/clients', [ 'timeserver:read' ],
                                () => ({ ...ssh(), url: 'ssh://127.0.0.1:22361' }), root => root.querySelector('#ssh-served') !== null);

        assert.match(root.querySelector('#ssh-only-here')!.textContent!, /--any/);

    });

});


describe('the NTS clients as they are sent', () => {

    it('sends each key back as the line it was given as', () => {

        assert.deepEqual(entriesOf({ enabled: true, served: true, subsystem: 's', url: null, hostKey: null,
                                     clients: [ { name: 'cs001', enabled: false, keys: [ { line: 'from="10.0.0.0/8" ssh-ed25519 AAAA cs001', fingerprint: 'SHA256:x' } ] } ] }),
                         [ { name: 'cs001', keys: [ 'from="10.0.0.0/8" ssh-ed25519 AAAA cs001' ], enabled: false } ]);

    });

    it('makes a key pasted over several lines one line again', () => {

        assert.equal(oneLine('ssh-ed25519 AAAAC3Nza\nC1lZDI1NTE5 cs001\r\n'), 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5 cs001');
        assert.equal(oneLine('  ssh-ed25519 AAAA cs001  '),                  'ssh-ed25519 AAAA cs001');

    });

});
