import { api,
         type ClockDescription,
         type MasterKey,
         type SigningKey,
         type TimeServerStatus,
         type UpstreamStatus }  from '../api/client';
import { auth } from '../auth';
import { toURL } from '@node/basePath';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, formatNumber, formatSince, formatTimestamp, humanizeKey, whileSaving } from '@node/ui';
import { html, nothing, render, type TemplateResult } from '@node/view';

/**
 * The time server: what it hands out and where that comes from, how far the
 * upstream servers were at the last round, what NTS-KE over TLS shows and
 * how a client is told to trust it, and how much it answered.
 *
 * Read-only but for "Ask them now", for whoever may run the node's time
 * servers, and the keys, for whoever may change the time server: a new master
 * key of the cookies - the "rotate the cookie master key" of section 8.2 of
 * the white paper "NTS Key Establishment over SSH" - and a new key to sign the
 * responses with, or one retired. What changes otherwise
 * is the configuration file's, the NTS clients page's and the Server
 * identities page's.
 */
export const timeServerPage: Page = {

    title: 'Time server',

    render({ root }) {

        const content = shell(root, {
            active:    '/ntsServer',
            title:     'Time server',
            subtitle:  'what it hands out, where from, and what it answered.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayAsk     = auth.can('nts', 'run');
        const mayRotate  = auth.can('timeserver', 'edit');

        let cancelled  = false;
        let current: TimeServerStatus | null = null;
        let asked: string | null = null;
        let askFailed: string | null = null;
        let rotated: string | null = null;
        let rotateFailed: string | null = null;
        let signed: string | null = null;
        let signFailed: string | null = null;


        function draw(): void {

            if (current === null)
                return;

            const status = current;

            render(content, html`

                <div class="cards">
                    ${timeCard(status)}
                    ${status.upstream !== null ? upstreamCard(status.upstream) : nothing}
                    ${ntsKECard(status)}
                    ${ntpCard(status)}
                    ${sshCard(status)}
                    ${masterKeysCard(status.masterKeys)}
                    ${signingKeysCard(status.signingKeys)}
                    ${answeredCard(status)}
                </div>

            `);

        }


        /** What is handed out, and whether anything is. */
        function timeCard(Status: TimeServerStatus): TemplateResult {

            const description = Status.clock.description;

            return html`
                <section class="card" id="time-handed-out">

                    <h2><i class="fa-solid fa-clock"></i> Time handed out</h2>

                    <p class="nts-state">
                        ${Status.serving
                              ? html`<span class="chip ok" id="serving">answering</span>
                                     ${Status.servingSince !== null ? html`<span class="muted">since ${formatTimestamp(Status.servingSince)}</span>` : nothing}`
                              : html`<span class="chip bad" id="serving">not answering</span>
                                     <span class="muted">${whyNotServing(Status)}</span>`}
                    </p>

                    <div class="kv-list">
                        ${kv('Source', Status.timeSource === 'local'
                                           ? 'the clock of this machine, as the operating system keeps it'
                                           : html`the NTS group <a href="${toURL('/configuration/nts')}">"${Status.upstream?.group ?? '?'}"</a>, on a clock of its own`)}
                        ${kv('Now', formatTimestamp(Status.clock.now))}
                        ${description !== null ? descriptionLines(description) : nothing}
                        ${Status.clock.base === 'free-running'
                              ? html`
                                  ${kv('Correction', `${formatNumber(Status.clock.correctionMs, 3)} ms over the stopwatch of this machine`)}
                                  ${kv('Frequency',  `${formatNumber(Status.clock.frequencyPPM, 3)} ppm`)}`
                              : nothing}
                        ${kv('Server made', Status.generations === 1
                                                ? 'once'
                                                : `${Status.generations} times - anew whenever what the answers say of the clock changes`)}
                    </div>

                </section>
            `;

        }


        /** Why nothing is answered: in local mode only a port, in upstream mode the time. */
        function whyNotServing(Status: TimeServerStatus): string {

            if (Status.timeSource === 'local')
                return 'its ports could not be had - see the log.';

            if (Status.upstream?.synchronizedSince === null)
                return 'until the upstream servers have given a time.';

            return 'the upstream servers have given no usable time for longer than the holdover.';

        }


        /** Stratum, reference, root delay and dispersion, and the leap indicator. */
        function descriptionLines(Description: ClockDescription): TemplateResult {

            return html`
                ${kv('Stratum',          String(Description.stratum))}
                ${kv('Reference',        Description.referenceId)}
                ${kv('Root delay',       `${formatNumber(Description.rootDelayMs, 3)} ms`)}
                ${kv('Root dispersion',  Description.rootDispersionMs !== null
                                             ? `${formatNumber(Description.rootDispersionMs, 3)} ms`
                                             : 'measured for every answer')}
                ${kv('Leap indicator',   leapWords(Description.leapIndicator))}
            `;

        }


        /** The last round of the upstream servers, and the button that asks them now. */
        function upstreamCard(Upstream: UpstreamStatus): TemplateResult {

            return html`
                <section class="card" id="upstream">

                    <h2><i class="fa-solid fa-tower-broadcast"></i> Upstream servers</h2>

                    <p class="nts-state">
                        ${Upstream.synchronized
                              ? html`<span class="chip ok" id="synchronized">synchronized</span>
                                     ${Upstream.synchronizedSince !== null ? html`<span class="muted">since ${formatTimestamp(Upstream.synchronizedSince)}</span>` : nothing}`
                              : html`<span class="chip warn" id="synchronized">not synchronized</span>`}
                    </p>

                    <div class="kv-list">
                        ${kv('Group',             html`<a href="${toURL('/configuration/nts')}">${Upstream.group}</a>`)}
                        ${kv('Last round',        Upstream.lastPoll !== null ? `${formatTimestamp(Upstream.lastPoll)} (${formatSince(Upstream.lastPoll)})` : 'none yet')}
                        ${kv('Last usable round', Upstream.lastGood !== null ? `${formatTimestamp(Upstream.lastGood)} (${formatSince(Upstream.lastGood)})` : 'none yet')}
                        ${kv('Answered',          Upstream.answered !== null && Upstream.asked !== null ? `${Upstream.answered} of ${Upstream.asked}` : '-')}
                        ${kv('Closest to the median', Upstream.systemPeer !== null
                                                          ? `${Upstream.systemPeer}${Upstream.upstreamStratum !== null ? `, stratum ${Upstream.upstreamStratum}` : ''}`
                                                          : '-')}
                        ${kv('Offset',            ms(Upstream.offsetMs))}
                        ${kv('Error being slewed', `${ms(Upstream.errorMs)}${Upstream.slewRemainingSeconds > 0 ? `, ${formatNumber(Upstream.slewRemainingSeconds, 0)} s to go` : ''}`)}
                        ${kv('Jitter',            ms(Upstream.jitterMs))}
                        ${kv('Spread',            ms(Upstream.spreadMs))}
                        ${kv('Rounds',            `${Upstream.polls}, of them ${Upstream.failedPolls} without a usable time; ${Upstream.outliers} outlier(s), ${Upstream.steps} step(s)`)}
                        ${kv('Leap second',       html`<span id="leap-second">${upstreamLeapWords(Upstream)}</span>`)}
                        ${Upstream.lastProblem !== null ? kv('Last problem', Upstream.lastProblem) : nothing}
                    </div>

                    <div class="form-actions">
                        <button type="button" class="btn" id="ask-now" ?disabled=${!mayAsk} @click=${() => void askNow()}
                                title=${mayAsk ? 'Ask the upstream servers now, instead of at the next poll' : mayButNot('read the time server', 'ask the time servers')}>
                            <i class="fa-solid fa-rotate"></i> Ask them now
                        </button>
                        <span id="ask-note"  class="form-notice" role="status"></span>
                        <span id="ask-error" class="form-error"  role="alert"></span>
                    </div>

                </section>
            `;

        }


        /** What NTS-KE over TLS shows, and how a client is told to trust it. */
        function ntsKECard(Status: TimeServerStatus): TemplateResult {

            const tls = Status.ntsKE.tls;

            return html`
                <section class="card" id="ntske-tls">

                    <h2><i class="fa-solid fa-lock"></i> NTS-KE over TLS</h2>

                    <div class="kv-list">
                        ${kv('Port',         `TCP ${Status.ntsKE.port}, on ${Status.listen}`)}
                        ${kv('Certificate',  tls.subject)}
                        ${kv('Valid for',    tls.names.join(', '))}
                        ${kv('Valid until',  formatTimestamp(tls.notAfter))}
                        ${kv('From',         tls.source)}
                        ${kv('SHA-256',      html`<code class="fingerprint" id="tls-fingerprint">${tls.fingerprint}</code>`)}
                    </div>

                    <p class="hint">
                        ${tls.selfSigned
                              ? html`Self-signed: a client has to be told to trust it - by its fingerprint, or by the
                                     certificate below as a trusted one (chrony: <code>ntstrustedcerts</code>). A TLS server
                                     identity made out to the name clients reach this server by is shown instead, once it is
                                     on the <a href="${toURL('/configuration/server-identities')}">Server identities</a> page.`
                              : html`A client that trusts the CA above it trusts it as it is.`}
                    </p>

                    <pre class="pem" id="tls-pem">${tls.pem}</pre>

                    <div class="form-actions">
                        <button type="button" class="btn small" id="copy-pem" @click=${(event: Event) => void copy(tls.pem, event.currentTarget as HTMLElement)}>Copy</button>
                        <button type="button" class="btn small" id="download-pem" @click=${() => download(pemFileName(tls.names), tls.pem)}>Download</button>
                    </div>

                </section>
            `;

        }


        /** Where NTP answers, and what the key exchange names to its clients. */
        function ntpCard(Status: TimeServerStatus): TemplateResult {

            return html`
                <section class="card" id="ntp">

                    <h2><i class="fa-solid fa-satellite-dish"></i> NTP and NTP+NTS</h2>

                    <div class="kv-list">
                        ${kv('Port',  `UDP ${Status.ntp.port}, on ${Status.listen}`)}
                        ${kv('Named', Status.ntp.advertised ?? 'nothing: a client asks the host it made the key exchange with, on port 123')}
                    </div>

                </section>
            `;

        }


        /** NTS-KE over SSH, in a few lines: the NTS clients page has the rest. */
        function sshCard(Status: TimeServerStatus): TemplateResult {

            const ssh     = Status.ssh;
            const enabled = ssh.clients.filter(client => client.enabled).length;
            const logins  = [ ...ssh.clients, ...ssh.accounts ].map(login => login.activity);
            const served  = logins.reduce((sum, activity) => sum + activity.served,  0);
            const refused = logins.reduce((sum, activity) => sum + activity.refused, 0);
            const last    = logins.map(activity => activity.lastServed?.at).
                                   filter((at): at is string => at !== undefined).
                                   sort().at(-1);

            return html`
                <section class="card" id="ntske-ssh">

                    <h2><i class="fa-solid fa-terminal"></i> NTS-KE over SSH</h2>

                    <p class="nts-state">
                        ${ssh.served
                              ? html`<span class="chip ok">served</span> <code>${ssh.url ?? ''}</code>`
                              : html`<span class="chip">${ssh.enabled ? 'switched on, not served' : 'off'}</span>`}
                    </p>

                    <div class="kv-list">
                        ${kv('Subsystem',    html`<code>${ssh.subsystem}</code>`)}
                        ${kv('Host key',     ssh.hostKey ?? 'not known yet')}
                        ${kv('NTS clients',  html`<a href="${toURL('/ntsServer/clients')}">${ssh.clients.length} - ${enabled} of them switched on</a>`)}
                        ${kv('Key exchanges', html`<span id="ssh-exchanges">${served} served and ${refused} turned away since the start${last !== undefined ? `, the last served ${formatSince(last)}` : ''}</span>`)}
                    </div>

                </section>
            `;

        }


        /**
         * The master keys the cookies are sealed with - ids and validity,
         * never values - and a new one: the earlier keys still open their
         * cookies until their grace period is over, or are forgotten at once.
         */
        function masterKeysCard(Keys: MasterKey[]): TemplateResult {

            const notAllowed = mayButNot('read the time server', 'change it');

            return html`
                <section class="card" id="master-keys">

                    <h2><i class="fa-solid fa-key"></i> Master keys of the cookies</h2>

                    ${Keys.length === 0
                          ? html`<p class="hint" id="no-master-keys">None: nothing is answered at the moment, so no cookie is sealed.</p>`
                          : html`
                              <div class="table-scroll">
                                  <table class="records master-keys">
                                      <thead>
                                          <tr><th>Id</th><th>Valid from</th><th>Until</th><th></th></tr>
                                      </thead>
                                      <tbody>
                                          ${Keys.map(key => html`
                                              <tr data-master-key="${key.id}" class="${key.current ? 'current' : ''}">
                                                  <td><code>${key.id}</code></td>
                                                  <td>${formatTimestamp(key.notBefore, toTheMinute)}</td>
                                                  <td>${formatTimestamp(key.notAfter,  toTheMinute)}</td>
                                                  <td>${key.current
                                                            ? html`<span class="chip on" title="It seals the cookies made now">seals now</span>`
                                                            : html`<span class="muted" title="It no longer seals cookies, but opens those it sealed">opens its cookies</span>`}</td>
                                              </tr>
                                          `)}
                                      </tbody>
                                  </table>
                              </div>
                          `}

                    <p class="hint">
                        A new key seals the cookies from now on, and the clients move to it with the cookies NTP hands
                        them. A key that may have been read is forgotten with the others at once: every cookie they
                        sealed is refused, and every client makes a new key exchange.
                    </p>

                    <div class="form-actions">
                        <button type="button" class="btn" id="rotate" ?disabled=${!mayRotate || Keys.length === 0}
                                title=${mayRotate ? 'Seal the cookies with a new master key from now on' : notAllowed}
                                @click=${() => void rotate(false)}>
                            <i class="fa-solid fa-rotate"></i> New master key
                        </button>
                        <button type="button" class="btn danger" id="rotate-forget" ?disabled=${!mayRotate || Keys.length === 0}
                                title=${mayRotate ? 'A new master key, and every earlier one forgotten at once' : notAllowed}
                                @click=${() => void rotate(true)}>
                            New key, and forget the others
                        </button>
                        <span id="rotate-note"  class="form-notice" role="status"></span>
                        <span id="rotate-error" class="form-error"  role="alert"></span>
                    </div>

                </section>
            `;

        }


        /**
         * The keys the responses are signed with, where a client asks for a
         * signed response - ids, validity and public keys, never a private key
         * - a new one, beginning a day from now or at once, and one retired.
         */
        function signingKeysCard(Keys: SigningKey[]): TemplateResult {

            const notAllowed = mayButNot('read the time server', 'change it');

            return html`
                <section class="card" id="signing-keys">

                    <h2><i class="fa-solid fa-signature"></i> Signing keys of the responses</h2>

                    ${Keys.length === 0
                          ? html`<p class="hint" id="no-signing-keys">None: no response is signed.</p>`
                          : html`
                              <div class="table-scroll">
                                  <table class="records signing-keys">
                                      <thead>
                                          <tr><th>Id</th><th>Valid from</th><th>Until</th><th></th><th>Public key</th><th></th></tr>
                                      </thead>
                                      <tbody>
                                          ${Keys.map(key => html`
                                              <tr data-signing-key="${key.id}" class="${key.state === 'signs' ? 'current' : ''}">
                                                  <td><code>${key.id}</code></td>
                                                  <td>${key.notBefore !== null ? formatTimestamp(key.notBefore, toTheMinute) : 'always'}</td>
                                                  <td>${key.notAfter  !== null ? formatTimestamp(key.notAfter,  toTheMinute) : ''}</td>
                                                  <td>${signingStateChip(key.state)}</td>
                                                  <td><code class="public-key" title="${key.signatureAlgorithm}, ${key.ellipticCurve}">${key.publicKey}</code></td>
                                                  <td>${key.state !== 'retired'
                                                            ? html`<button type="button" class="btn small danger" data-retire="${key.id}" ?disabled=${!mayRotate}
                                                                           title=${mayRotate ? 'Let it sign nothing from now on, and its public key no longer be handed out' : notAllowed}
                                                                           @click=${() => void retireSigningKey(key.id)}>Retire</button>`
                                                            : nothing}</td>
                                              </tr>
                                          `)}
                                      </tbody>
                                  </table>
                              </div>
                          `}

                    <p class="hint">
                        A client that asks for a signed response learns the public keys from its key exchange. A new
                        key signs from a day from now on, and its public key is handed out until then, so that the
                        clients know it when it takes over; a key that may have been read is replaced at once, and
                        retired.
                    </p>

                    <div class="form-actions">
                        <button type="button" class="btn" id="new-signing-key" ?disabled=${!mayRotate}
                                title=${mayRotate ? 'A new key, signing from a day from now on' : notAllowed}
                                @click=${() => void newSigningKey(false)}>
                            <i class="fa-solid fa-plus"></i> New signing key
                        </button>
                        <button type="button" class="btn danger" id="new-signing-key-now" ?disabled=${!mayRotate}
                                title=${mayRotate ? 'A new key, signing from now on' : notAllowed}
                                @click=${() => void newSigningKey(true)}>
                            New key, signing at once
                        </button>
                        <span id="signing-note"  class="form-notice" role="status"></span>
                        <span id="signing-error" class="form-error"  role="alert"></span>
                    </div>

                </section>
            `;

        }


        /** What it answered since the program started. */
        function answeredCard(Status: TimeServerStatus): TemplateResult {

            return html`
                <section class="card" id="answered">

                    <h2><i class="fa-solid fa-chart-simple"></i> Answered</h2>

                    <div class="kv-list">
                        ${Object.entries(Status.metrics).map(([ name, value ]) => kv(metricWords[name] ?? humanizeKey(name), String(value)))}
                    </div>

                </section>
            `;

        }


        /**
         * Ask the upstream servers now, and say what came of it. What it says
         * is kept, and written into its line again after every draw: the
         * line is the page's to write, not the template's.
         */
        async function askNow(): Promise<void> {

            asked     = null;
            askFailed = null;
            say();

            const note = content.querySelector<HTMLElement>('#ask-note');

            if (note !== null)
                note.textContent = 'Asking ...';

            try
            {
                const round = await whileSaving(content, null, () => api.ntsServer.sync());

                asked = round.synchronized
                            ? `Asked: ${round.answered ?? 0} of ${round.asked ?? 0} answered, ${ms(round.offsetMs)} off.`
                            : `Asked, and still not synchronized${round.lastProblem !== null ? `: ${round.lastProblem}` : '.'}`;

                await load();
            }
            catch (problem)
            {
                askFailed = errorMessage(problem);
            }

            say();

        }


        /**
         * Seal the cookies with a new master key, and say what came of it -
         * asked first where the earlier keys are to be forgotten, as that ends
         * every client's cookies.
         */
        async function rotate(Forget: boolean): Promise<void> {

            if (Forget && !confirm('Forget every earlier master key? Every cookie they sealed is refused from now on, and every client has to make a new key exchange.'))
                return;

            rotated      = null;
            rotateFailed = null;
            say();

            try
            {
                const rotation = await whileSaving(content, content.querySelector<HTMLElement>('#rotate-note'), () => api.ntsServer.rotateMasterKey(Forget));

                rotated = rotation.forgotten
                              ? `The cookies are sealed with the new master key ${rotation.rotated.id} from now on, and every earlier key is forgotten.`
                              : `The cookies are sealed with the new master key ${rotation.rotated.id} from now on; the earlier keys still open their cookies.`;

                await load();
            }
            catch (problem)
            {
                rotateFailed = errorMessage(problem);
            }

            say();

        }


        /**
         * A new signing key, and what came of it - asked first where it is to
         * sign at once, as a client that has not made a key exchange since
         * cannot check what it signs.
         */
        async function newSigningKey(AtOnce: boolean): Promise<void> {

            if (AtOnce && !confirm('A new signing key, signing at once? A client that has not made a key exchange since cannot check the responses it signs.'))
                return;

            signed     = null;
            signFailed = null;
            say();

            try
            {
                const answer = await whileSaving(content, content.querySelector<HTMLElement>('#signing-note'), () => api.ntsServer.newSigningKey(AtOnce));

                signed = answer.made.state === 'next' && answer.made.notBefore !== null
                             ? `The new signing key ${answer.made.id} signs from ${formatTimestamp(answer.made.notBefore, toTheMinute)} on; its public key is handed out until then.`
                             : `The new signing key ${answer.made.id} signs from now on.`;

                await load();
            }
            catch (problem)
            {
                signFailed = errorMessage(problem);
            }

            say();

        }


        /** Retire a signing key - asked first, as it cannot be taken back. */
        async function retireSigningKey(Id: number): Promise<void> {

            if (!confirm(`Retire the signing key ${Id}? It signs nothing from now on, and that cannot be taken back.`))
                return;

            signed     = null;
            signFailed = null;
            say();

            try
            {
                const answer = await whileSaving(content, content.querySelector<HTMLElement>('#signing-note'), () => api.ntsServer.retireSigningKey(Id));

                signed = `The signing key ${answer.retired.id} signs nothing from now on.`;

                await load();
            }
            catch (problem)
            {
                signFailed = errorMessage(problem);
            }

            say();

        }


        /** What asking the upstream servers, a new master key and a signing key came to, each in its two lines. */
        function say(): void {

            const lines: [ string, string | null ][] = [ [ '#ask-note',    asked        ],
                                                         [ '#ask-error',   askFailed    ],
                                                         [ '#rotate-note', rotated      ],
                                                         [ '#rotate-error', rotateFailed ],
                                                         [ '#signing-note',  signed       ],
                                                         [ '#signing-error', signFailed   ] ];

            for (const [ selector, text ] of lines)
            {

                const line = content.querySelector<HTMLElement>(selector);

                if (line !== null)
                    line.textContent = text ?? '';

            }

        }


        async function copy(Text: string, Button: HTMLElement): Promise<void> {

            try
            {
                await navigator.clipboard.writeText(Text);
                Button.textContent = 'Copied';
            }
            catch
            {
                Button.textContent = 'Select it and copy';
            }

            setTimeout(() => { Button.textContent = 'Copy'; }, 2000);

        }


        async function load(): Promise<void> {

            try
            {

                const loaded = await api.ntsServer.status();

                if (cancelled)
                    return;

                current = loaded;
                draw();
                say();

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The time server could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        void load();

        return () => { cancelled = true; };

    }

};


/** When a master key is valid: to the minute, as a key is made once a day. */
const toTheMinute = new Intl.DateTimeFormat([], { dateStyle: 'medium', timeStyle: 'short' });

/** One line of a card. */
function kv(Name: string, Value: string | TemplateResult): TemplateResult {

    return html`
        <div class="kv">
            <span class="k">${Name}</span>
            <span class="v">${Value}</span>
        </div>
    `;

}

/** Milliseconds, with the digits they are worth. */
function ms(Value: number | null): string {
    return Value === null ? '-' : `${formatNumber(Value, 3)} ms`;
}

/** What the leap indicator says (RFC 5905 § 7.3). */
export function leapWords(Indicator: number): string {

    switch (Indicator)
    {
        case 0:   return 'no warning';
        case 1:   return 'the last minute of the day has 61 seconds';
        case 2:   return 'the last minute of the day has 59 seconds';
        default:  return 'unsynchronized';
    }

}

/**
 * What the upstream servers announce of a leap second, and what is made of
 * it: taken up only on the last day of a month, at whose end it is made.
 */
export function upstreamLeapWords(Upstream: Pick<UpstreamStatus, 'leapIndicatorAnnounced' | 'leapSecond' | 'leapSecondsMade'>): string {

    const made = Upstream.leapSecondsMade !== 0
                     ? `; ${Upstream.leapSecondsMade > 0 ? '+' : ''}${Upstream.leapSecondsMade} s made since the start`
                     : '';

    if (Upstream.leapSecond !== null)
        return `${Upstream.leapSecond.inserted ? 'inserted' : 'deleted'} at the end of ${Upstream.leapSecond.day} (UTC): ` +
               `announced to the clients, and made at midnight${made}`;

    if (Upstream.leapIndicatorAnnounced === 1 || Upstream.leapIndicatorAnnounced === 2)
        return `announced by the upstream servers, and passed on only on the last day of a month${made}`;

    return `none announced${made}`;

}

/** What a signing key is now, as a chip says it. */
export function signingStateChip(State: SigningKey['state']): TemplateResult {

    switch (State)
    {
        case 'signs':  return html`<span class="chip on" title="It signs the responses that ask for no key in particular">signs now</span>`;
        case 'valid':  return html`<span class="muted" title="Another began after it: it signs where a request asks for it by its id">by its id</span>`;
        case 'next':   return html`<span class="chip" title="Its public key is handed out in the key exchange until it begins">next</span>`;
        default:       return html`<span class="muted" title="It signs nothing any more">retired</span>`;
    }

}

/** The names of what was counted, as a line says them. */
const metricWords: Record<string, string> = {
    ntpRequestsReceived:       'NTP requests',
    ntpResponsesSent:          'NTP answers',
    ntpSignedResponsesSent:    'NTP answers signed',
    ntpRequestsInvalid:        'NTP requests that were none',
    ntpRequestsRejected:       'NTP requests refused',
    ntpRequestsRateLimited:    'NTP requests over the rate',
    ntpKissesOfDeathSent:      'Kisses of death',
    ntpRequestFailures:        'NTP requests that failed',
    ntsKEConnectionsAccepted:  'Key exchanges begun',
    ntsKEConnectionsRejected:  'Key exchanges refused',
    ntsKEHandshakeFailures:    'TLS handshakes that failed',
    ntsKERequestsInvalid:      'Key exchanges that were none',
    ntsKEResponsesSent:        'Key exchanges answered'
};

/** The file the certificate is downloaded as: named after the first name it is made out to. */
export function pemFileName(Names: readonly string[]): string {
    return `${(Names[0] ?? 'ntske').replace(/[^A-Za-z0-9._-]+/g, '_')}.pem`;
}

/** Hand the browser a text as a file to keep. */
function download(Name: string, Text: string): void {

    const url  = URL.createObjectURL(new Blob([ Text ], { type: 'application/x-pem-file' }));
    const link = document.createElement('a');

    link.href     = url;
    link.download = Name;

    document.body.appendChild(link);
    link.click();
    link.remove();

    setTimeout(() => URL.revokeObjectURL(url), 1000);

}
