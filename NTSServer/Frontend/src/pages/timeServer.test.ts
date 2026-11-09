/**
 * The time server page drawn, in a document of happy-dom, against a stand-in
 * NTS server: in local mode what it hands out and its certificate; in
 * upstream mode the last round, and "Ask them now" for whoever may run the
 * time servers - and greyed out, with why, for whoever may not.
 */

import { asked, open, refused, until, type Asked } from '../../test/ntsServer.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

const { timeServerPage, leapWords, upstreamLeapWords, pemFileName } = await import('./timeServer.ts');


const pem = '-----BEGIN CERTIFICATE-----\nMIIBfake\n-----END CERTIFICATE-----\n';

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
                         hostKey: 'SHA256:host', clients: [ { name: 'cs001', enabled: true, keys: 1 }, { name: 'cs002', enabled: false, keys: 1 } ] },
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

function ntsServer({ method, path }: Asked): unknown {

    if (method === 'GET'  && path === '/ntsServer')
        return status(upstream);

    if (method === 'POST' && path === '/ntsServer/sync') {
        roundsAsked++;
        return (status(true).upstream as Record<string, unknown>);
    }

    return undefined;

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
