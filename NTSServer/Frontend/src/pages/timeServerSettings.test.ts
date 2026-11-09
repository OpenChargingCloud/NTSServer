/**
 * The settings of the time server drawn, in a document of happy-dom, against
 * a stand-in NTS server that keeps what it is sent: the form sent whole, the
 * answer drawn - what changed and what the node said of it - and what the
 * node refused said without losing what was typed. For whoever may only look,
 * nothing that changes anything.
 */

import { asked, change, field, open, refused, submit, type, until, type Asked } from '../../test/ntsServer.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

const { timeServerSettingsPage } = await import('./timeServerSettings.ts');


function settingsOf(): Record<string, unknown> {

    return {
        listen:      '0.0.0.0',
        ntsKEPort:   4460,
        ntpPort:     123,
        hostname:    'time.lan',
        ntpServer:   null,
        timeSource:  'local',
        local:       { stratum: 1, referenceId: 'LOCL', rootDispersionMs: null },
        upstream:    { group: 'legal', pollSeconds: 64, stepThresholdMs: 128, maxSlewPPM: 500, holdoverMinutes: 60 },
        rateLimit:   false,
        ssh:         { enabled: true, clients: [] }
    };

}

let settings = settingsOf();

function ntsServer({ method, path, body }: Asked): unknown {

    if (path === '/configuration/nts/groups')
        return { groups: [ { id: 'legal' }, { id: 'upstream' } ], suggested: [], limits: { maxIdLength: 32, maxDisplayNameLength: 64 }, file: 'config.json' };

    if (path !== '/ntsServer/settings')
        return undefined;

    if (method === 'PUT') {

        const sent = body as Record<string, unknown>;

        if (sent['ntpPort'] === 99)
            return refused(409, 'Nothing was changed: UDP port 99 is taken by somebody else.');

        const changed = Object.keys(sent).filter(key => typeof sent[key] !== 'object' && JSON.stringify(sent[key]) !== JSON.stringify(settings[key]));

        settings = { ...settings, ...Object.fromEntries(changed.map(key => [ key, sent[key] ])) };

        return { settings, changed, notes: changed.includes('ntpPort') ? [ `Clients are to be told the port: NTS-KE on TCP 4460, NTP on UDP ${String(settings['ntpPort'])}.` ] : [] };

    }

    return settings;

}

function puts(): Record<string, unknown>[] {
    return asked.filter(one => one.method === 'PUT').map(one => one.body as Record<string, unknown>);
}


describe('the settings of the time server', () => {

    it('sends the form whole, and draws what changed and what the node said of it', async () => {

        settings = settingsOf();

        const root = await open(timeServerSettingsPage, '/ntsServer/settings', [ 'timeserver:read', 'timeserver:edit', 'nts:read' ],
                                ntsServer, root => root.querySelector('#settings') !== null);

        assert.equal(field(root, '#settings', 'ntpPort').value,   '123');
        assert.equal(field(root, '#settings', 'hostname').value,  'time.lan');
        assert.equal(root.querySelectorAll('#nts-groups option').length, 2, 'the groups to choose from were not listed');

        type(field(root, '#settings', 'ntpPort'), '1123');
        submit(root, '#settings');

        await until(() => /saved, and in effect/.test(root.querySelector('#settings-note')!.textContent!), 'the change was not said');

        const sent = puts()[0]!;

        assert.equal(sent['ntpPort'],     1123);
        assert.equal(sent['timeSource'],  'local');
        assert.equal(sent['ntpServer'],   null, 'an empty field is the default, not ""');
        assert.deepEqual(sent['local'],   { stratum: 1, referenceId: 'LOCL', rootDispersionMs: null },
                         'an emptied number field is not 0');

        assert.match(root.querySelector('#settings-note')!.textContent!, /^ntpPort: saved, and in effect\.$/);
        assert.match(root.querySelector('#settings-notes')!.textContent!, /NTP on UDP 1123/);
        assert.equal(field(root, '#settings', 'ntpPort').value, '1123', 'the form does not show what is in effect');

    });

    it('sends the time source chosen', async () => {

        settings = settingsOf();

        const root = await open(timeServerSettingsPage, '/ntsServer/settings', [ 'timeserver:read', 'timeserver:edit' ],
                                ntsServer, root => root.querySelector('#settings') !== null);

        change(root.querySelector<HTMLInputElement>('#settings input[name="timeSource"][value="upstream"]')!, true);
        submit(root, '#settings');

        await until(() => puts().length === 1, 'nothing was sent');

        assert.equal(puts()[0]!['timeSource'], 'upstream');

    });

    it('says what the node refused, and keeps what was typed', async () => {

        settings = settingsOf();

        const root = await open(timeServerSettingsPage, '/ntsServer/settings', [ 'timeserver:read', 'timeserver:edit' ],
                                ntsServer, root => root.querySelector('#settings') !== null);

        type(field(root, '#settings', 'ntpPort'), '99');
        submit(root, '#settings');

        await until(() => root.querySelector('#settings-error')!.textContent !== '', 'the refusal was not said');

        assert.match(root.querySelector('#settings-error')!.textContent!, /UDP port 99 is taken/);
        assert.equal(field(root, '#settings', 'ntpPort').value, '99');

    });

    it('changes nothing for whoever may only look, and says why', async () => {

        settings = settingsOf();

        const root = await open(timeServerSettingsPage, '/ntsServer/settings', [ 'timeserver:read' ],
                                ntsServer, root => root.querySelector('#settings') !== null);

        assert.match(root.querySelector('.notice')!.textContent!, /may look at the settings of the time server but not change them/);
        assert.equal(root.querySelector<HTMLButtonElement>('#settings button[type="submit"]')!.disabled, true);
        assert.equal(field(root, '#settings', 'ntpPort').disabled, true);
        assert.equal(root.querySelectorAll('#nts-groups option').length, 0, 'the groups were asked for without the right to read them');

    });

});
