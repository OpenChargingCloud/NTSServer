import { api,
         type TimeServerSettings,
         type TimeServerSettingsChange,
         type TimeSource }  from '../api/client';
import { auth } from '../auth';
import { toURL } from '@node/basePath';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, numberFrom, whileSaving } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, type TemplateResult } from '@node/view';

/**
 * The time server's own settings - where its time comes from and how, what it
 * listens on, the names it goes by - changed while it runs.
 *
 * One form, sent whole: the node changes what differs from what is in effect
 * and nothing else, puts it into effect at once and keeps it in the
 * "ntsServer" section - and says what whoever changed it has to know, a port
 * the clients are to be told, a certificate made anew, a switch of the start
 * that says otherwise at the next one. A port somebody else has changes
 * nothing at all. NTS-KE over SSH is the NTS clients page's.
 */
export const timeServerSettingsPage: Page = {

    title: 'Time server settings',

    render({ root }) {

        const content = shell(root, {
            active:    '/ntsServer/settings',
            title:     'Time server settings',
            subtitle:  'where the time comes from, what is listened on, and the names it goes by - in effect at once.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayChange = auth.can('timeserver', 'edit');

        let cancelled = false;
        let current:  TimeServerSettings | null = null;
        let groups:   string[]                  = [];
        let notes:    string[]                  = [];


        function draw(): void {

            if (current === null)
                return;

            const settings = current;

            render(content, html`

                ${mayChange ? nothing : html`
                    <div class="notice">
                        ${mayButNot('look at the settings of the time server', 'change them')}
                    </div>
                `}

                <form id="settings" class="settings-form" @submit=${onSave}>

                    <div class="cards stacked">
                        ${timeSourceCard(settings)}
                        ${listeningCard(settings)}
                        ${namesCard(settings)}
                    </div>

                    <div class="form-actions">
                        <button type="submit" class="btn primary" ?disabled=${!mayChange}>Save</button>
                        <span id="settings-note"  class="form-notice" role="status"></span>
                        <span id="settings-error" class="form-error"  role="alert"></span>
                    </div>

                    ${notes.length === 0 ? nothing : html`
                        <ul class="notes" id="settings-notes">
                            ${notes.map(note => html`<li>${note}</li>`)}
                        </ul>
                    `}

                </form>

            `);

        }


        /** Local or upstream, and what each of them says and does. */
        function timeSourceCard(Settings: TimeServerSettings): TemplateResult {

            const source = (Value: TimeSource, Label: string, Hint: TemplateResult) => html`
                <label class="checkbox">
                    <input type="radio" name="timeSource" value="${Value}" ?checked=${Settings.timeSource === Value} ?disabled=${!mayChange} />
                    ${Label}
                    <span class="hint">${Hint}</span>
                </label>
            `;

            return html`
                <section class="card" id="time-source">

                    <h2><i class="fa-solid fa-clock"></i> Where the time comes from</h2>

                    <div class="form-stack">

                        ${source('local', 'This machine\'s clock', html`
                            As the operating system keeps it - right where a GPS receiver, chrony or the Windows Time
                            service keeps it well.`)}

                        ${source('upstream', 'An NTS group of this node', html`
                            On a clock of its own, never setting this machine's. Changed to it, nothing is answered until
                            the group has given a time.`)}

                    </div>

                    <h3>This machine's clock</h3>

                    <div class="form-stack">

                        <label>Stratum
                            <input type="number" name="stratum" min="1" max="15" step="1" required
                                   value="${Settings.local.stratum}" ?disabled=${!mayChange} />
                            <span class="hint">1 for a server reading its reference directly - a GPS receiver - one more for each server between.</span>
                        </label>

                        <label>Reference
                            <input type="text" name="referenceId" maxlength="4" required spellcheck="false" autocomplete="off"
                                   value="${Settings.local.referenceId}" ?disabled=${!mayChange} />
                            <span class="hint">Up to four characters: <code>LOCL</code> for a clock nobody calibrated, <code>GPS</code>, <code>PPS</code>.</span>
                        </label>

                        <label>Root dispersion (ms)
                            <input type="number" name="rootDispersionMs" min="0.001" max="16000" step="0.001"
                                   value="${Settings.local.rootDispersionMs ?? ''}" placeholder="measured with every answer" ?disabled=${!mayChange} />
                            <span class="hint">The largest error it admits to. Empty, it is measured with every answer.</span>
                        </label>

                    </div>

                    <h3>The NTS group</h3>

                    <div class="form-stack">

                        <label>Group
                            <input type="text" name="group" list="nts-groups" required spellcheck="false" autocomplete="off"
                                   value="${Settings.upstream.group}" ?disabled=${!mayChange} />
                            <datalist id="nts-groups">
                                ${groups.map(group => html`<option value="${group}"></option>`)}
                            </datalist>
                            <span class="hint">
                                One of the <a href="${toURL('/configuration/nts')}">upstream time servers</a> of this node:
                                its servers, its quorum and the certificates they are held to are written there.
                            </span>
                        </label>

                        <label>Asked every (s)
                            <input type="number" name="pollSeconds" min="16" max="1024" step="1" required
                                   value="${Settings.upstream.pollSeconds}" ?disabled=${!mayChange} />
                        </label>

                        <label>Step threshold (ms)
                            <input type="number" name="stepThresholdMs" min="1" max="3600000" step="1" required
                                   value="${Settings.upstream.stepThresholdMs}" ?disabled=${!mayChange} />
                            <span class="hint">Further off than this is an outlier; three in a row are stepped to.</span>
                        </label>

                        <label>Slewed at most (ppm)
                            <input type="number" name="maxSlewPPM" min="0.001" max="500" step="0.001" required
                                   value="${Settings.upstream.maxSlewPPM}" ?disabled=${!mayChange} />
                        </label>

                        <label>Holdover (minutes)
                            <input type="number" name="holdoverMinutes" min="1" max="10080" step="1" required
                                   value="${Settings.upstream.holdoverMinutes}" ?disabled=${!mayChange} />
                            <span class="hint">How long the time is still handed out without a usable answer.</span>
                        </label>

                    </div>

                    <p class="hint">What the source not chosen says is kept for when it is.</p>

                </section>
            `;

        }


        /** The address and the ports, and whether a client asking too often is told to slow down. */
        function listeningCard(Settings: TimeServerSettings): TemplateResult {

            return html`
                <section class="card" id="listening">

                    <h2><i class="fa-solid fa-plug"></i> What is listened on</h2>

                    <div class="form-stack">

                        <label>Address
                            <input type="text" name="listen" spellcheck="false" autocomplete="off"
                                   value="${Settings.listen}" ?disabled=${!mayChange} />
                            <span class="hint">An IPv4 or IPv6 address, <code>0.0.0.0</code> for every IPv4 one, <code>any</code> for both families.</span>
                        </label>

                        <label>NTS-KE over TLS (TCP)
                            <input type="number" name="ntsKEPort" min="1" max="65535" step="1" required
                                   value="${Settings.ntsKEPort}" ?disabled=${!mayChange} />
                        </label>

                        <label>NTP and NTP+NTS (UDP)
                            <input type="number" name="ntpPort" min="1" max="65535" step="1" required
                                   value="${Settings.ntpPort}" ?disabled=${!mayChange} />
                        </label>

                        <label class="checkbox">
                            <input type="checkbox" name="rateLimit" ?checked=${Settings.rateLimit} ?disabled=${!mayChange} />
                            Tell a client that asks too often to slow down
                            <span class="hint">With a RATE kiss-o'-death rather than the time (RFC 8633).</span>
                        </label>

                    </div>

                    <p class="hint">
                        Other ports are answered on at once, by the server made anew; one somebody else has changes
                        nothing. Clients have to be told them.
                    </p>

                </section>
            `;

        }


        /** The name clients reach it by, and the NTP server the key exchange names. */
        function namesCard(Settings: TimeServerSettings): TemplateResult {

            return html`
                <section class="card" id="names">

                    <h2><i class="fa-solid fa-signature"></i> The names it goes by</h2>

                    <div class="form-stack">

                        <label>Host name
                            <input type="text" name="hostname" spellcheck="false" autocomplete="off" placeholder="time.lan"
                                   value="${Settings.hostname ?? ''}" ?disabled=${!mayChange} />
                            <span class="hint">
                                What clients reach it by: a self-signed certificate of NTS-KE is made anew for it, which a
                                client that holds this server to the fingerprint has to be told.
                            </span>
                        </label>

                        <label>NTP server named
                            <input type="text" name="ntpServer" spellcheck="false" autocomplete="off" placeholder="host or host:port"
                                   value="${Settings.ntpServer ?? ''}" ?disabled=${!mayChange} />
                            <span class="hint">
                                What the key exchange names as the NTP server. Empty, it names the host name with the NTP port
                                where that is not 123, and nothing otherwise - a client then asks where it made the key exchange.
                            </span>
                        </label>

                    </div>

                </section>
            `;

        }


        async function onSave(event: SubmitEvent): Promise<void> {

            event.preventDefault();

            const form  = event.currentTarget as HTMLFormElement;
            const note  = content.querySelector<HTMLElement>('#settings-note');
            const error = content.querySelector<HTMLElement>('#settings-error');

            if (note  !== null) note.textContent  = '';
            if (error !== null) error.textContent = '';

            try
            {

                const answer = await whileSaving(content, note, () => api.ntsServer.setSettings(changeFrom(form)));

                current = answer.settings;
                notes   = answer.notes;

                draw();
                form.reset();

                const said = content.querySelector<HTMLElement>('#settings-note');

                if (said !== null)
                    said.textContent = answer.changed.length === 0
                                           ? 'Nothing changed.'
                                           : `${answer.changed.join(', ')}: saved, and in effect.`;

            }
            catch (problem)
            {
                if (error !== null)
                    error.textContent = errorMessage(problem);
            }

        }


        async function load(): Promise<void> {

            try
            {

                const [loaded, known] = await Promise.all([
                                            api.ntsServer.settings(),
                                            // The groups, for the list a group is chosen from - where
                                            // they may be looked at; typed in otherwise.
                                            auth.can('nts', 'read')
                                                ? api.ntsGroups.list().then(list => list.groups.map(group => group.id), () => [] as string[])
                                                : Promise.resolve([] as string[])
                                        ]);

                if (cancelled)
                    return;

                current = loaded;
                groups  = known;
                notes   = [];

                // Loaded anew - Reload - is what the node has, the form too.
                draw();
                content.querySelector<HTMLFormElement>('#settings')?.reset();

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The settings of the time server could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        const release = unsaved.heldBy(() => anyFormTypedSinceDrawn(content));

        void load();

        return () => { cancelled = true; release(); };

    }

};


/**
 * What the form says, as a change is sent: every setting of it - the node
 * changes what differs from what is in effect, and nothing else. A field left
 * empty is null, the default; a number field emptied is not 0.
 *
 * Read from the fields themselves rather than as FormData: the cards are
 * drawn by templates of their own inside the form, and happy-dom, which the
 * tests draw it in, leaves the fields of those out of the form's data.
 */
export function changeFrom(Form: HTMLFormElement): TimeServerSettingsChange {

    const input  = (Name: string) => Form.querySelector<HTMLInputElement>(`[name="${Name}"]`);
    const text   = (Name: string) => input(Name)?.value.trim() || null;
    const number = (Name: string) => { const value = numberFrom(input(Name)?.value ?? ''); return Number.isNaN(value) ? null : value; };
    const chosen = [ ...Form.querySelectorAll<HTMLInputElement>('input[name="timeSource"]') ].find(radio => radio.checked)?.value;

    return {
        timeSource:  (chosen ?? 'local') as TimeSource,
        local:       {
                         stratum:           number('stratum'),
                         referenceId:       text('referenceId'),
                         rootDispersionMs:  number('rootDispersionMs')
                     },
        upstream:    {
                         group:             text('group'),
                         pollSeconds:       number('pollSeconds'),
                         stepThresholdMs:   number('stepThresholdMs'),
                         maxSlewPPM:        number('maxSlewPPM'),
                         holdoverMinutes:   number('holdoverMinutes')
                     },
        listen:      text('listen'),
        ntsKEPort:   number('ntsKEPort'),
        ntpPort:     number('ntpPort'),
        rateLimit:   input('rateLimit')?.checked ?? false,
        hostname:    text('hostname'),
        ntpServer:   text('ntpServer')
    };

}
