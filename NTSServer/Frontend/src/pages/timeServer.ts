import { api,
         type ClockDescription,
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
 * Read-only but for one button, "Ask them now", for whoever may run the
 * node's time servers. What changes is the configuration file's, the NTS
 * clients page's and the Server identities page's.
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

        const mayAsk = auth.can('nts', 'run');

        let cancelled  = false;
        let current: TimeServerStatus | null = null;
        let asked: string | null = null;
        let askFailed: string | null = null;


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


        /** What asking the upstream servers came to, in its two lines. */
        function say(): void {

            const note  = content.querySelector<HTMLElement>('#ask-note');
            const error = content.querySelector<HTMLElement>('#ask-error');

            if (note !== null)
                note.textContent  = asked ?? '';

            if (error !== null)
                error.textContent = askFailed ?? '';

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

/** The names of what was counted, as a line says them. */
const metricWords: Record<string, string> = {
    ntpRequestsReceived:       'NTP requests',
    ntpResponsesSent:          'NTP answers',
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
