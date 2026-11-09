/**
 * The configuration drawn, in a document of happy-dom, against a stand-in
 * NTS server: its cards stand as the markup they are - the time server's
 * status not among them, which the Time server page draws - the frame's
 * Reload asks again and draws what it says then, and a configuration that
 * cannot be had is said so.
 */

import { asked, open, refused, until, type Asked } from '../../test/ntsServer.ts';

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

const { configurationPage } = await import('./configuration.ts');


let uptime     = '1 minute';
let available  = true;

function ntsServer({ path }: Asked): unknown {

    if (path === '/status')
        return { service: 'NTS server', version: '0.1.0', hermod: null, timestamp: '2026-11-09T12:00:00Z',
                 startedAt: '2026-11-09T11:59:00Z', uptime, sessions: 1, log: { entries: 0, capacity: 100, lastId: 0, tags: [] } };

    if (path === '/configuration')
        return available
                   ? { ntsServer: { machine: 'time001', settings: { timeSource: 'upstream' }, status: { serving: true, secretStatusField: 'not here' } },
                       http: { port: 2361 }, web: {}, log: {}, time: {},
                       assemblies: [ { name: 'NTSServer', version: '0.1.0', commit: 'abc1234' } ] }
                   : refused(500, 'the configuration file could not be read');

    return undefined;

}


describe('the configuration', () => {

    it('draws its cards as markup, without the status the Time server page draws, and again on Reload', async () => {

        uptime     = '1 minute';
        available  = true;

        const root = await open(configurationPage, '/configuration', [ 'configuration:read' ],
                                ntsServer, root => root.querySelector('.cards') !== null);

        assert.equal(root.querySelectorAll('.cards > section.card').length, 6);
        assert.match(root.querySelector('.cards')!.textContent!, /time001/);
        assert.match(root.querySelector('.cards')!.textContent!, /upstream/);
        assert.doesNotMatch(root.querySelector('.cards')!.textContent!, /secretStatusField|not here/);
        assert.match(root.querySelector('.cards')!.textContent!, /Uptime\s+1 minute/);

        const configurationsAsked = () => asked.filter(one => one.method === 'GET' && one.path === '/configuration').length;
        assert.equal(configurationsAsked(), 1);

        uptime = '2 minutes';
        root.querySelector<HTMLButtonElement>('.page-actions #reload')!.click();

        await until(() => /Uptime\s+2 minutes/.test(root.textContent!), 'Reload did not draw what the server says then');

        assert.equal(configurationsAsked(), 2, 'Reload did not ask again');

    });

    it('says so where the configuration cannot be had', async () => {

        available = false;

        const root = await open(configurationPage, '/configuration', [ 'configuration:read' ],
                                ntsServer, root => root.querySelector('.error-box') !== null);

        assert.match(root.querySelector('.error-box')!.textContent!, /The configuration could not be loaded: .*could not be read/);

    });

});
