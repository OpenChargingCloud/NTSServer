/**
 * The time server page drawn, in a document of happy-dom, against a stand-in
 * NTS server: in local mode what it hands out and its certificate; in
 * upstream mode the last round, and "Ask them now" for whoever may run the
 * time servers - and greyed out, with why, for whoever may not. The master
 * keys of the cookies, a new one that keeps the others and one that forgets
 * them, asked first; the keys the responses are signed with, a new one and
 * one retired; and the key exchanges over SSH since the start.
 */

import { asked, open, refused, said, until, type Asked } from '../../test/ntsServer.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

const { timeServerPage, leapWords, upstreamLeapWords, pemFileName } = await import('./timeServer.ts');


const pem = '-----BEGIN CERTIFICATE-----\nMIIBfake\n-----END CERTIFICATE-----\n';

const none = { served: 0, refused: 0, cookies: 0, lastServed: null, lastRefused: null };

const cs001 = { served: 3, refused: 1, cookies: 24,
                lastServed:  { at: '2026-11-09T12:20:00Z', from: '192.168.1.17:50022', method: 'exporter', aeadAlgorithm: 15, cookies: 8 },
                lastRefused: { at: '2026-11-09T11:00:00Z', from: '192.168.1.17:50010', why: 'the client is switched off' } };

const alice = { served: 2, refused: 0, cookies: 16,
                lastServed:  { at: '2026-11-09T12:25:00Z', from: '127.0.0.1:50100', method: 'transport', aeadAlgorithm: 15, cookies: 8 },
                lastRefused: null };

interface Key { id: number; notBefore: string; notAfter: string; current: boolean }

const firstKeys: Key[] = [ { id: 1, notBefore: '2026-11-08T12:00:00Z', notAfter: '2026-11-09T12:00:00Z', current: false },
                           { id: 2, notBefore: '2026-11-09T12:00:00Z', notAfter: '2026-11-10T12:00:00Z', current: true  } ];

let masterKeys: Key[] = firstKeys;

interface SigningKey { id: number; publicKey: string; ellipticCurve: string; signatureAlgorithm: string;
                       notBefore: string | null; notAfter: string | null; state: 'signs' | 'valid' | 'next' | 'retired' }

const firstSigningKeys: SigningKey[] = [ { id: 1, publicKey: 'BPubKeyOne', ellipticCurve: 'secp256r1', signatureAlgorithm: 'SHA-256withECDSA',
                                           notBefore: null, notAfter: null, state: 'signs' } ];

let signingKeys: SigningKey[] = firstSigningKeys;

function status(Upstream: boolean, Synchronized = true): Record<string, unknown> {

    return {
        timeSource:    Upstream ? 'upstream' : 'local',
        serving:       Synchronized,
        servingSince:  Synchronized ? '2026-11-09T12:00:00Z' : null,
        generations:   2,
        listen:        '0.0.0.0',
        ntsKE:         { port: 4460, tls: { subject: 'CN=time.lan', names: [ 'time.lan', 'localhost' ], fingerprint: 'AB12CD34',
                                            notAfter: '2029-11-09T12:00:00Z', source: 'made here', selfSigned: true, pem } },
        ntp:           { port: 123, advertised: null },
        ssh:           { enabled: true, served: true, subsystem: 'ntske/1@open.charging.cloud', url: 'ssh://192.168.1.2:22361',
                         hostKey: 'SHA256:host', clients: [ { name: 'cs001', enabled: true,  keys: 1, activity: cs001 },
                                                            { name: 'cs002', enabled: false, keys: 1, activity: none  } ],
                         accounts: [ { name: 'alice', activity: alice } ] },
        masterKeys,
        signingKeys,
        clock:         { now: '2026-11-09T12:30:00Z', base: Upstream ? 'free-running' : 'system', correctionMs: 121.9, frequencyPPM: 3.25,
                         leapSeconds: 0, leapSecond: null,
                         description: { stratum: Upstream ? 2 : 1, referenceId: Upstream ? '192.53.103.108' : 'LOCL',
                                        rootDelayMs: 4.2, rootDispersionMs: 5.838, leapIndicator: 0 } },
        upstream:      Upstream
                           ? { group: 'legal', synchronized: Synchronized, synchronizedSince: Synchronized ? '2026-11-09T12:00:00Z' : null,
                               lastPoll: '2026-11-09T12:29:00Z', lastGood: '2026-11-09T12:29:00Z', lastGoodAgeSeconds: 60, lastProblem: null,
                               offsetMs: -4.4, errorMs: 0.2, jitterMs: 1.1, spreadMs: 7.1, systemPeer: 'ptbtime1.ptb.de', upstreamStratum: 1,
                               answered: 4, asked: 4, frequencyPPM: 3.25, correctionMs: 121.9, slewRemainingSeconds: 12, polls: 30,
                               failedPolls: 0, outliers: 1, steps: 1, description: null,
                               leapIndicatorAnnounced: 0, leapSecond: null, leapSecondsMade: 0 }
                           : null,
        metrics:       { ntpRequestsReceived: 12, ntpResponsesSent: 11, ntsKEResponsesSent: 3 }
    };

}

let upstream     = false;
let roundsAsked  = 0;

function ntsServer({ method, path, body }: Asked): unknown {

    if (method === 'GET'  && path === '/ntsServer')
        return status(upstream);

    if (method === 'POST' && path === '/ntsServer/sync') {
        roundsAsked++;
        return (status(true).upstream as Record<string, unknown>);
    }

    if (method === 'POST' && path === '/ntsServer/masterKeys/rotate') {

        const forget  = (body as { forget: boolean }).forget;
        const id      = Math.max(...masterKeys.map(key => key.id)) + 1;
        const rotated = { id, notBefore: '2026-11-09T12:30:00Z', notAfter: '2026-11-10T12:30:00Z', current: true };

        masterKeys = [ ...(forget ? [] : masterKeys.map(key => ({ ...key, current: false }))), rotated ];

        return { rotated, forgotten: forget, masterKeys };

    }

    if (method === 'POST' && path === '/ntsServer/signingKeys') {

        const atOnce = (body as { atOnce: boolean }).atOnce;
        const id     = Math.max(...signingKeys.map(key => key.id)) + 1;
        const made: SigningKey = { id, publicKey: `BPubKey${id}`, ellipticCurve: 'secp256r1', signatureAlgorithm: 'SHA-256withECDSA',
                                   notBefore: atOnce ? '2026-11-09T12:30:00Z' : '2026-11-10T12:30:00Z', notAfter: null,
                                   state: atOnce ? 'signs' : 'next' };

        signingKeys = [ ...signingKeys.map(key => atOnce && key.state === 'signs' ? { ...key, state: 'valid' as const } : key), made ];

        return { made, signingKeys };

    }

    if (method === 'POST' && path === '/ntsServer/signingKeys/retire') {

        const id      = (body as { id: number }).id;
        const retired = { ...signingKeys.find(key => key.id === id)!, notAfter: '2026-11-09T12:30:00Z', state: 'retired' as const };

        signingKeys = signingKeys.map(key => key.id === id ? retired : key);

        return { retired, signingKeys };

    }

    return undefined;

}

function signingRequests(): { path: string; body: unknown }[] {
    return asked.filter(one => one.method === 'POST' && one.path.startsWith('/ntsServer/signingKeys')).map(one => ({ path: one.path, body: one.body }));
}

function rotations(): unknown[] {
    return asked.filter(one => one.method === 'POST' && one.path === '/ntsServer/masterKeys/rotate').map(one => one.body);
}


describe('the time server', () => {

    it('in local mode, says what it hands out and shows its certificate, without an upstream card', async () => {

        upstream = false;

        const root = await open(timeServerPage, '/ntsServer', [ 'timeserver:read' ],
                                ntsServer, root => root.querySelector('#time-handed-out') !== null);

        assert.match(root.querySelector('#serving')!.textContent!, /answering/);
        assert.match(root.querySelector('#time-handed-out')!.textContent!, /the clock of this machine/);
        assert.match(root.querySelector('#time-handed-out')!.textContent!, /Stratum\s+1/);
        assert.match(root.querySelector('#time-handed-out')!.textContent!, /Reference\s+LOCL/);
        assert.equal(root.querySelector('#upstream'), null, 'local mode has no upstream servers to show');

        assert.equal(root.querySelector('#tls-fingerprint')!.textContent, 'AB12CD34');
        assert.equal(root.querySelector('#tls-pem')!.textContent, pem);
        assert.match(root.querySelector('#ntske-tls')!.textContent!, /Self-signed/);
        assert.match(root.querySelector('#ntske-ssh')!.textContent!, /2 - 1 of them switched on/);
        assert.match(root.querySelector('#answered')!.textContent!, /NTP answers\s+11/);

    });

    it('in upstream mode, shows the last round and asks the servers again on "Ask them now"', async () => {

        upstream    = true;
        roundsAsked = 0;

        const root = await open(timeServerPage, '/ntsServer', [ 'timeserver:read', 'nts:run' ],
                                ntsServer, root => root.querySelector('#upstream') !== null);

        assert.match(root.querySelector('#synchronized')!.textContent!, /synchronized/);
        assert.match(root.querySelector('#upstream')!.textContent!, /ptbtime1\.ptb\.de, stratum 1/);
        assert.match(root.querySelector('#upstream')!.textContent!, /4 of 4/);
        assert.equal(root.querySelector('#leap-second')!.textContent, 'none announced');
        assert.match(root.querySelector('#time-handed-out')!.textContent!, /Correction\s+121[.,]900 ms/);

        const button = root.querySelector<HTMLButtonElement>('#ask-now')!;

        assert.equal(button.disabled, false);
        button.click();

        await until(() => /Asked: 4 of 4 answered/.test(root.querySelector('#ask-note')!.textContent!), 'what the round came to was not said');

        assert.equal(roundsAsked, 1);
        assert.equal(asked.filter(one => one.method === 'GET' && one.path === '/ntsServer').length, 2, 'the page was not drawn anew after the round');

    });

    it('greys "Ask them now" out, with why, for whoever may not run the time servers', async () => {

        upstream = true;

        const root = await open(timeServerPage, '/ntsServer', [ 'timeserver:read' ],
                                ntsServer, root => root.querySelector('#upstream') !== null);

        const button = root.querySelector<HTMLButtonElement>('#ask-now')!;

        assert.equal(button.disabled, true);
        assert.match(button.title, /may read the time server but not ask the time servers/);

    });

    it('says why nothing is answered before the upstream servers gave a time', async () => {

        const root = await open(timeServerPage, '/ntsServer', [ 'timeserver:read' ],
                                () => status(true, false), root => root.querySelector('#serving') !== null);

        assert.match(root.querySelector('#time-handed-out')!.textContent!, /not answering\s+until the upstream servers have given a time/);
        assert.match(root.querySelector('#synchronized')!.textContent!, /not synchronized/);

    });

    it('says so where the time server cannot be had', async () => {

        const root = await open(timeServerPage, '/ntsServer', [ 'timeserver:read' ],
                                () => refused(403, 'not yours to read'), root => root.querySelector('.error-box') !== null);

        assert.match(root.querySelector('.error-box')!.textContent!, /The time server could not be loaded: .*not yours to read/);

    });

    it('says how many key exchanges over SSH were served and turned away, by clients and accounts', async () => {

        upstream = false;

        const root = await open(timeServerPage, '/ntsServer', [ 'timeserver:read' ],
                                ntsServer, root => root.querySelector('#ssh-exchanges') !== null);

        assert.match(root.querySelector('#ssh-exchanges')!.textContent!, /^5 served and 1 turned away since the start, the last served \d+[smhd] ago$/);

    });

});


describe('the master keys of the cookies', () => {

    it('shows them - ids and validity, which one seals now - and makes a new one that keeps the others', async () => {

        upstream   = false;
        masterKeys = firstKeys;

        const root = await open(timeServerPage, '/ntsServer', [ 'timeserver:read', 'timeserver:edit' ],
                                ntsServer, root => root.querySelector('#master-keys') !== null);

        assert.equal(root.querySelectorAll('tr[data-master-key]').length, 2);
        assert.match(root.querySelector('tr[data-master-key="2"]')!.textContent!, /seals now/);
        assert.match(root.querySelector('tr[data-master-key="1"]')!.textContent!, /opens its cookies/);

        root.querySelector<HTMLButtonElement>('#rotate')!.click();

        await until(() => /new master key 3 from now on; the earlier keys still open their cookies/.test(root.querySelector('#rotate-note')!.textContent!),
                    'the new key was not said');

        assert.deepEqual(rotations(), [ { forget: false } ]);
        assert.equal(said.length, 0, 'asked before a rotation that forgets nothing');
        assert.equal(root.querySelectorAll('tr[data-master-key]').length, 3);
        assert.match(root.querySelector('tr[data-master-key="3"]')!.textContent!, /seals now/);
        assert.match(root.querySelector('tr[data-master-key="2"]')!.textContent!, /opens its cookies/);

    });

    it('asks before it forgets the earlier keys, and then keeps the new one alone', async () => {

        upstream   = false;
        masterKeys = firstKeys;

        const root = await open(timeServerPage, '/ntsServer', [ 'timeserver:read', 'timeserver:edit' ],
                                ntsServer, root => root.querySelector('#master-keys') !== null);

        root.querySelector<HTMLButtonElement>('#rotate-forget')!.click();

        await until(() => /every earlier key is forgotten/.test(root.querySelector('#rotate-note')!.textContent!), 'the forgetting was not said');

        assert.ok(said.some(text => /Forget every earlier master key\?/.test(text)), 'nobody was asked before the keys went');
        assert.deepEqual(rotations(), [ { forget: true } ]);
        assert.deepEqual([ ...root.querySelectorAll<HTMLElement>('tr[data-master-key]') ].map(row => row.dataset['masterKey']), [ '3' ]);

    });

    it('says what the server refused', async () => {

        upstream   = false;
        masterKeys = firstKeys;

        const root = await open(timeServerPage, '/ntsServer', [ 'timeserver:read', 'timeserver:edit' ],
                                asked => asked.path === '/ntsServer/masterKeys/rotate' ? refused(409, 'Nothing is answered at the moment') : ntsServer(asked),
                                root => root.querySelector('#master-keys') !== null);

        root.querySelector<HTMLButtonElement>('#rotate')!.click();

        await until(() => root.querySelector('#rotate-error')!.textContent !== '', 'the refusal was not said');

        assert.match(root.querySelector('#rotate-error')!.textContent!, /Nothing is answered at the moment/);
        assert.equal(root.querySelectorAll('tr[data-master-key]').length, 2);

    });

    it('greys both out, with why, for whoever may only read the time server', async () => {

        upstream   = false;
        masterKeys = firstKeys;

        const root = await open(timeServerPage, '/ntsServer', [ 'timeserver:read' ],
                                ntsServer, root => root.querySelector('#master-keys') !== null);

        for (const button of root.querySelectorAll<HTMLButtonElement>('#rotate, #rotate-forget'))
        {
            assert.equal(button.disabled, true, `${button.id} can be pressed`);
            assert.match(button.title, /may read the time server but not change it/);
        }

    });

    it('says there is none while nothing is answered', async () => {

        upstream   = false;
        masterKeys = [];

        const root = await open(timeServerPage, '/ntsServer', [ 'timeserver:read', 'timeserver:edit' ],
                                ntsServer, root => root.querySelector('#master-keys') !== null);

        assert.match(root.querySelector('#no-master-keys')!.textContent!, /nothing is answered at the moment/);
        assert.equal(root.querySelector<HTMLButtonElement>('#rotate')!.disabled, true);

        masterKeys = firstKeys;

    });

});


describe('the keys the responses are signed with', () => {

    it('shows them - public keys, and which one signs now - and makes a new one that begins a day later', async () => {

        upstream    = false;
        signingKeys = firstSigningKeys;

        const root = await open(timeServerPage, '/ntsServer', [ 'timeserver:read', 'timeserver:edit' ],
                                ntsServer, root => root.querySelector('#signing-keys') !== null);

        assert.equal(root.querySelectorAll('tr[data-signing-key]').length, 1);
        assert.match(root.querySelector('tr[data-signing-key="1"]')!.textContent!, /signs now/);
        assert.match(root.querySelector('tr[data-signing-key="1"]')!.textContent!, /BPubKeyOne/);
        assert.match(root.querySelector('tr[data-signing-key="1"]')!.textContent!, /always/);

        root.querySelector<HTMLButtonElement>('#new-signing-key')!.click();

        await until(() => /new signing key 2 signs from .* on; its public key is handed out until then/.test(root.querySelector('#signing-note')!.textContent!),
                    'the new key was not said');

        assert.deepEqual(signingRequests(), [ { path: '/ntsServer/signingKeys', body: { atOnce: false } } ]);
        assert.equal(said.length, 0, 'asked before a key that signs only tomorrow');
        assert.match(root.querySelector('tr[data-signing-key="2"]')!.textContent!, /next/);
        assert.match(root.querySelector('tr[data-signing-key="1"]')!.textContent!, /signs now/);

    });

    it('asks before a new key signs at once', async () => {

        upstream    = false;
        signingKeys = firstSigningKeys;

        const root = await open(timeServerPage, '/ntsServer', [ 'timeserver:read', 'timeserver:edit' ],
                                ntsServer, root => root.querySelector('#signing-keys') !== null);

        root.querySelector<HTMLButtonElement>('#new-signing-key-now')!.click();

        await until(() => /new signing key 2 signs from now on/.test(root.querySelector('#signing-note')!.textContent!), 'the new key was not said');

        assert.ok(said.some(text => /signing at once\?/.test(text)), 'nobody was asked before the key signed at once');
        assert.deepEqual(signingRequests(), [ { path: '/ntsServer/signingKeys', body: { atOnce: true } } ]);
        assert.match(root.querySelector('tr[data-signing-key="2"]')!.textContent!, /signs now/);
        assert.match(root.querySelector('tr[data-signing-key="1"]')!.textContent!, /by its id/);

    });

    it('retires one, asked first, and offers no button for it any more', async () => {

        upstream    = false;
        signingKeys = firstSigningKeys;

        const root = await open(timeServerPage, '/ntsServer', [ 'timeserver:read', 'timeserver:edit' ],
                                ntsServer, root => root.querySelector('#signing-keys') !== null);

        root.querySelector<HTMLButtonElement>('button[data-retire="1"]')!.click();

        await until(() => /signing key 1 signs nothing from now on/.test(root.querySelector('#signing-note')!.textContent!), 'the retirement was not said');

        assert.ok(said.some(text => /Retire the signing key 1\?/.test(text)), 'nobody was asked before the key was retired');
        assert.deepEqual(signingRequests(), [ { path: '/ntsServer/signingKeys/retire', body: { id: 1 } } ]);
        assert.match(root.querySelector('tr[data-signing-key="1"]')!.textContent!, /retired/);
        assert.equal(root.querySelector('button[data-retire="1"]'), null);

    });

    it('says what the server refused', async () => {

        upstream    = false;
        signingKeys = firstSigningKeys;

        const root = await open(timeServerPage, '/ntsServer', [ 'timeserver:read', 'timeserver:edit' ],
                                asked => asked.path === '/ntsServer/signingKeys' ? refused(409, 'could not be read') : ntsServer(asked),
                                root => root.querySelector('#signing-keys') !== null);

        root.querySelector<HTMLButtonElement>('#new-signing-key')!.click();

        await until(() => root.querySelector('#signing-error')!.textContent !== '', 'the refusal was not said');

        assert.match(root.querySelector('#signing-error')!.textContent!, /could not be read/);
        assert.equal(root.querySelectorAll('tr[data-signing-key]').length, 1);

    });

    it('greys everything out, with why, for whoever may only read the time server', async () => {

        upstream    = false;
        signingKeys = firstSigningKeys;

        const root = await open(timeServerPage, '/ntsServer', [ 'timeserver:read' ],
                                ntsServer, root => root.querySelector('#signing-keys') !== null);

        for (const button of root.querySelectorAll<HTMLButtonElement>('#new-signing-key, #new-signing-key-now, button[data-retire]'))
        {
            assert.equal(button.disabled, true, `${button.id || button.dataset['retire']} can be pressed`);
            assert.match(button.title, /may read the time server but not change it/);
        }

    });

    it('says there is none where none is kept', async () => {

        upstream    = false;
        signingKeys = [];

        const root = await open(timeServerPage, '/ntsServer', [ 'timeserver:read', 'timeserver:edit' ],
                                ntsServer, root => root.querySelector('#signing-keys') !== null);

        assert.match(root.querySelector('#no-signing-keys')!.textContent!, /no response is signed/);

        signingKeys = firstSigningKeys;

    });

});


describe('the words of the time server page', () => {

    it('says each leap indicator', () => {
        assert.equal(leapWords(0), 'no warning');
        assert.match(leapWords(1), /61 seconds/);
        assert.match(leapWords(2), /59 seconds/);
        assert.equal(leapWords(3), 'unsynchronized');
    });

    it('says what is made of a leap second the upstream servers announce', () => {

        assert.equal(upstreamLeapWords({ leapIndicatorAnnounced: 0, leapSecond: null, leapSecondsMade: 0 }), 'none announced');

        assert.equal(upstreamLeapWords({ leapIndicatorAnnounced: 1, leapSecond: { day: '2026-12-31', inserted: true, leapIndicator: 1 }, leapSecondsMade: 0 }),
                     'inserted at the end of 2026-12-31 (UTC): announced to the clients, and made at midnight');

        // Announced on another day than the last of the month: held back.
        assert.match(upstreamLeapWords({ leapIndicatorAnnounced: 1, leapSecond: null, leapSecondsMade: 0 }), /only on the last day of a month/);

        assert.equal(upstreamLeapWords({ leapIndicatorAnnounced: 0, leapSecond: null, leapSecondsMade: -1 }), 'none announced; -1 s made since the start');

    });

    it('names the certificate after the first name it is made out to, as a file may be named', () => {
        assert.equal(pemFileName([ 'time.lan', 'localhost' ]), 'time.lan.pem');
        assert.equal(pemFileName([ 'a name/with*odd' ]),      'a_name_with_odd.pem');
        assert.equal(pemFileName([]),                         'ntske.pem');
    });

});
