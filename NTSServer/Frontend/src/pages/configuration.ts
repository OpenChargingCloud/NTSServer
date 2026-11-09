import { api } from '../api/client';
import { cardView, librariesCardView } from '@node/cardViews';
import type { Page } from '@node/router';
import { reloadButton, shell } from '@node/shell';
import { errorMessage, formatSince } from '@node/ui';
import { html, render } from '@node/view';

/**
 * What this NTS server is - read-only: it answers "what am I running", not
 * "change it". The pages below this one, and the configuration file, are
 * where things change.
 *
 * The fields of each section are drawn from whatever the server sends rather
 * than from a list kept here, so a field added on the server shows up
 * without a change to this page. The sections are not: which of them there
 * are, their order and their headings are decided here.
 */
export const configurationPage: Page = {

    title: 'Configuration',

    render({ root }) {

        const content = shell(root, {
            active:    '/configuration',
            title:     'Configuration',
            subtitle:  'what this NTS server is running.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        let cancelled = false;

        async function load(): Promise<void> {

            try
            {

                const [configuration, status] = await Promise.all([
                    api.configuration(),
                    api.status()
                ]);

                if (cancelled)
                    return;

                // What the time server says of itself, without what the Time
                // server page shows better: its status, drawn there.
                const { status: _timeServerStatus, ...ntsServer } = configuration.ntsServer;

                render(content, html`

                    <div class="cards">

                        ${cardView('NTS server',    'fa-clock',            ntsServer, html`
                            <div class="kv">
                                <span class="k">Uptime</span>
                                <span class="v">${status.uptime} <span class="muted">(started ${formatSince(status.startedAt)})</span></span>
                            </div>
                        `)}

                        ${cardView('HTTP server',   'fa-server',           configuration.http)}
                        ${cardView('Accounts',      'fa-user-lock',        configuration.web)}
                        ${cardView('Event log',     'fa-list-ul',          configuration.log)}
                        ${cardView('Time',          'fa-business-time',    configuration.time)}

                        ${librariesCardView(configuration.assemblies)}

                    </div>

                `);

            }
            catch (problem)
            {

                if (cancelled)
                    return;

                render(content, html`
                    <div class="error-box">The configuration could not be loaded: ${errorMessage(problem)}</div>
                `);

            }

        }

        void load();

        return () => { cancelled = true; };

    }

};
