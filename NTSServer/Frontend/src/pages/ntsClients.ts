import { api,
         type KeyExchangeOverSSH,
         type NTSClient,
         type NTSClientEntry,
         type SSHChange }  from '../api/client';
import { auth } from '../auth';
import { toURL } from '@node/basePath';
import { must } from '@node/html';
import type { Page } from '@node/router';
import { mayButNot, reloadButton, shell } from '@node/shell';
import { errorMessage, field, formatTimestamp, whileSaving } from '@node/ui';
import { anyFormTypedSinceDrawn, unsaved } from '@node/unsaved';
import { html, nothing, render, repeat, type TemplateResult } from '@node/view';

/**
 * The NTS clients: the machines that may make key exchanges over SSH - the
 * subsystem ntske/1@open.charging.cloud of the node's SSH server - each under
 * its name, with one of its SSH keys, and confined to the key exchange: no
 * terminal, no command line, no SFTP.
 *
 * Every change is sent at once and is in effect at once, the whole list with
 * it: a client switched off or removed can make no key exchange from then on,
 * and a session of it that is open ends. The two forms - a new client, and
 * another key of one - are drafts the page holds until they are sent.
 */
export const ntsClientsPage: Page = {

    title: 'NTS clients',

    render({ root }) {

        const content = shell(root, {
            active:    '/ntsServer/clients',
            title:     'NTS clients',
            subtitle:  'the machines that may make key exchanges over SSH, by their SSH keys.',
            actions:   reloadButton(() => load())
        });

        render(content, html`<div class="loading">Loading ...</div>`);

        const mayChange = auth.can('timeserver', 'edit');

        let cancelled = false;
        let current: KeyExchangeOverSSH | null = null;


        function draw(): void {

            if (current === null)
                return;

            const ssh = current;

            render(content, html`

                ${mayChange ? nothing : html`
                    <div class="notice">
                        ${mayButNot('look at the NTS clients', 'change them')}
                    </div>
                `}

                <div class="cards stacked">
                    ${serviceCard(ssh)}
                    ${clientsCard(ssh)}
                    ${addClientCard(ssh)}
                    ${ssh.clients.length > 0 ? addKeyCard(ssh) : nothing}
                </div>

            `);

        }


        /** Whether NTS-KE is served over SSH, where, and how a client asks for it. */
        function serviceCard(SSH: KeyExchangeOverSSH): TemplateResult {

            const onlyHere = SSH.url !== null && /\/\/(127\.0\.0\.1|\[::1\]|localhost)[:/]/.test(SSH.url);

            return html`
                <section class="card" id="ntske-ssh">

                    <h2><i class="fa-solid fa-terminal"></i> NTS-KE over SSH</h2>

                    <p class="nts-state">
                        ${SSH.served
                              ? html`<span class="chip ok" id="ssh-served">served</span> <code id="ssh-url">${SSH.url ?? ''}</code>`
                              : SSH.enabled
                                    ? html`<span class="chip warn" id="ssh-served">switched on, not served</span>
                                           <span class="muted">The SSH server of this node is off - see the
                                           <a href="${toURL('/configuration/ssh')}">SSH server</a> page.</span>`
                                    : html`<span class="chip" id="ssh-served">off</span>`}
                    </p>

                    ${SSH.served && onlyHere ? html`
                        <div class="notice" id="ssh-only-here">
                            The SSH server listens on this machine only, so no other machine can reach it: start with
                            <code>--any</code> for NTS-KE over SSH in the local network.
                        </div>
                    ` : nothing}

                    <div class="kv-list">
                        <div class="kv"><span class="k">Subsystem</span> <span class="v"><code>${SSH.subsystem}</code></span></div>
                        <div class="kv"><span class="k">Host key</span>  <span class="v"><code class="fingerprint" id="host-key">${SSH.hostKey ?? 'not known yet'}</code></span></div>
                    </div>

                    <p class="hint">
                        A client holds this server to its host key, signs in under its name with one of its keys and asks
                        for the subsystem - OpenSSH does it with <code>ssh -s</code>. The key exchange runs as over TLS,
                        and NTP afterwards is the same.
                    </p>

                    <div class="form-actions">
                        <button type="button" class="btn" id="switch-ssh" ?disabled=${!mayChange}
                                @click=${() => void change({ enabled: !SSH.enabled }, SSH.enabled ? 'Switched off.' : 'Switched on.')}>
                            ${SSH.enabled ? 'Switch NTS-KE over SSH off' : 'Switch NTS-KE over SSH on'}
                        </button>
                    </div>

                </section>
            `;

        }


        /** The clients and their keys, each with what may be done to it. */
        function clientsCard(SSH: KeyExchangeOverSSH): TemplateResult {

            return html`
                <section class="card" id="clients">

                    <h2><i class="fa-solid fa-server"></i> Clients</h2>

                    ${SSH.clients.length === 0
                          ? html`<p class="hint" id="no-clients">None yet: no machine may make a key exchange over SSH but an account that may run "ntske".</p>`
                          : html`
                              <div class="table-scroll">
                                  <table class="records nts-clients">
                                      <thead>
                                          <tr><th>Name</th><th>Keys</th><th></th></tr>
                                      </thead>
                                      <tbody>
                                          ${repeat(SSH.clients, client => client.name, client => clientRow(client))}
                                      </tbody>
                                  </table>
                              </div>
                          `}

                    <div class="form-actions">
                        <span id="clients-note"  class="form-notice" role="status"></span>
                        <span id="clients-error" class="form-error"  role="alert"></span>
                    </div>

                </section>
            `;

        }


        function clientRow(Client: NTSClient): TemplateResult {

            return html`
                <tr data-client="${Client.name}" class="${Client.enabled ? '' : 'switched-off'}">
                    <td class="client-name">
                        <strong>${Client.name}</strong>
                        ${Client.enabled ? nothing : html`<span class="chip">off</span>`}
                    </td>
                    <td class="client-keys">
                        ${Client.keys.length === 0
                              ? html`<span class="muted">no key: it cannot sign in</span>`
                              : repeat(Client.keys, key => key.line, key => html`
                                  <div class="client-key" data-fingerprint="${key.fingerprint ?? ''}">
                                      <code class="fingerprint">${key.fingerprint ?? key.line}</code>
                                      <span class="muted small">
                                          ${key.algorithm ?? ''}${key.comment ? ` - ${key.comment}` : ''}${key.options !== undefined && key.options.length > 0 ? ` - ${key.options.join(', ')}` : ''}${key.notAfter ? ` - until ${formatTimestamp(key.notAfter)}` : ''}
                                      </span>
                                      <button type="button" class="btn small" data-remove-key="${key.line}" ?disabled=${!mayChange}
                                              @click=${() => void removeKey(Client, key.line)}>Remove key</button>
                                  </div>
                              `)}
                    </td>
                    <td class="client-actions">
                        <button type="button" class="btn small" data-switch="${Client.name}" ?disabled=${!mayChange}
                                @click=${() => void switchClient(Client)}>${Client.enabled ? 'Switch off' : 'Switch on'}</button>
                        <button type="button" class="btn small danger" data-remove="${Client.name}" ?disabled=${!mayChange}
                                @click=${() => void removeClient(Client)}>Remove</button>
                    </td>
                </tr>
            `;

        }


        /** The form of a new client: its name, and its first key. */
        function addClientCard(SSH: KeyExchangeOverSSH): TemplateResult {

            return html`
                <section class="card" id="add-client-card">

                    <h2><i class="fa-solid fa-plus"></i> A new client</h2>

                    <form id="add-client" class="form-stack" @submit=${onAddClient}>

                        <label>Name
                            <input type="text" name="name" required autocomplete="off" spellcheck="false"
                                   placeholder="cs001" ?disabled=${!mayChange} />
                            <span class="hint">What it signs in as. Not the name of an account of this node.</span>
                        </label>

                        <label>Its key
                            <textarea name="key" rows="3" required spellcheck="false" ?disabled=${!mayChange}
                                      placeholder="ssh-ed25519 AAAA... cs001"></textarea>
                            <span class="hint">
                                One line of an <code>authorized_keys</code> file: the public key, as
                                <code>ssh-keygen</code> wrote it. <code>from="192.168.1.0/24"</code> or
                                <code>expiry-time="20271231"</code> in front of it hold as OpenSSH holds them.
                            </span>
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ?disabled=${!mayChange}>Add the client</button>
                            <span id="add-client-note"  class="form-notice" role="status"></span>
                            <span id="add-client-error" class="form-error"  role="alert"></span>
                        </div>

                    </form>

                    ${SSH.clients.length > 0 ? nothing : html`<p class="hint">It may make key exchanges as soon as it is added.</p>`}

                </section>
            `;

        }


        /** The form of another key of a client: a machine with a new key, before the old one goes. */
        function addKeyCard(SSH: KeyExchangeOverSSH): TemplateResult {

            return html`
                <section class="card" id="add-key-card">

                    <h2><i class="fa-solid fa-key"></i> Another key of a client</h2>

                    <form id="add-key" class="form-stack" @submit=${onAddKey}>

                        <label>Client
                            <select name="client" ?disabled=${!mayChange}>
                                ${SSH.clients.map(client => html`<option value="${client.name}">${client.name}</option>`)}
                            </select>
                        </label>

                        <label>The key
                            <textarea name="key" rows="3" required spellcheck="false" ?disabled=${!mayChange}
                                      placeholder="ssh-ed25519 AAAA..."></textarea>
                        </label>

                        <div class="form-actions">
                            <button type="submit" class="btn primary" ?disabled=${!mayChange}>Add the key</button>
                            <span id="add-key-note"  class="form-notice" role="status"></span>
                            <span id="add-key-error" class="form-error"  role="alert"></span>
                        </div>

                    </form>

                </section>
            `;

        }


        function onAddClient(event: SubmitEvent): void {

            event.preventDefault();

            const form = event.currentTarget as HTMLFormElement;
            const name = field(form, 'name');
            const key  = oneLine(field(form, 'key'));

            if (current!.clients.some(client => client.name.toLowerCase() === name.toLowerCase()))
            {
                must<HTMLElement>(content, '#add-client-error').textContent = `There is a client "${name}" already: give it another key below.`;
                return;
            }

            void change({ clients: [ ...entriesOf(current!), { name, keys: [ key ], enabled: true } ] },
                        `${name} may make key exchanges now.`, '#add-client', form);

        }


        function onAddKey(event: SubmitEvent): void {

            event.preventDefault();

            const form  = event.currentTarget as HTMLFormElement;
            const name  = field(form, 'client');
            const key   = oneLine(field(form, 'key'));

            void change({ clients: entriesOf(current!).map(entry => entry.name === name
                                                                        ? { ...entry, keys: [ ...entry.keys, key ] }
                                                                        : entry) },
                        `${name} has another key.`, '#add-key', form);

        }


        function switchClient(Client: NTSClient): Promise<void> {

            return change({ clients: entriesOf(current!).map(entry => entry.name === Client.name
                                                                          ? { ...entry, enabled: !Client.enabled }
                                                                          : entry) },
                          Client.enabled ? `${Client.name} is switched off.` : `${Client.name} is switched on.`);

        }


        function removeClient(Client: NTSClient): Promise<void> {

            if (!confirm(`Remove ${Client.name}? It can make no key exchange over SSH from then on, and a session of it that is open ends.`))
                return Promise.resolve();

            return change({ clients: entriesOf(current!).filter(entry => entry.name !== Client.name) },
                          `${Client.name} is removed.`);

        }


        function removeKey(Client: NTSClient, Line: string): Promise<void> {

            if (Client.keys.length === 1 &&
                !confirm(`This is the only key of ${Client.name}: without it, it cannot sign in. Remove it?`))
                return Promise.resolve();

            return change({ clients: entriesOf(current!).map(entry => entry.name === Client.name
                                                                          ? { ...entry, keys: entry.keys.filter(key => key !== Line) }
                                                                          : entry) },
                          `A key of ${Client.name} is removed.`);

        }


        /**
         * Send a change, and draw what the node answers with: the page shows
         * what the node made of it, not what was sent. Said in the line of the
         * form it came from, or of the list.
         */
        async function change(Change:  SSHChange,
                              Done:    string,
                              From:    '#add-client' | '#add-key' | null = null,
                              Form:    HTMLFormElement | null = null): Promise<void> {

            const notePrefix = From ?? '#clients';
            const note       = content.querySelector<HTMLElement>(`${notePrefix}-note`);
            const error      = content.querySelector<HTMLElement>(`${notePrefix}-error`);

            for (const line of content.querySelectorAll<HTMLElement>('.form-notice, .form-error'))
                line.textContent = '';

            try
            {
                current = await whileSaving(content, note, () => api.ntsServer.setSSH(Change));

                draw();
                Form?.reset();

                const said = content.querySelector<HTMLElement>(`${notePrefix}-note`);

                if (said !== null)
                    said.textContent = `${Done} Saved, and in effect.`;
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

                const loaded = await api.ntsServer.ssh();

                if (cancelled)
                    return;

                current = loaded;

                // Loaded anew - Reload - is what the node has, the forms too.
                draw();
                content.querySelector<HTMLFormElement>('#add-client')?.reset();
                content.querySelector<HTMLFormElement>('#add-key')?.reset();

            }
            catch (problem)
            {
                if (!cancelled)
                    render(content, html`
                        <div class="error-box">The NTS clients could not be loaded: ${errorMessage(problem)}</div>
                    `);
            }

        }

        const release = unsaved.heldBy(() => anyFormTypedSinceDrawn(content));

        void load();

        return () => { cancelled = true; release(); };

    }

};


/** The clients as they are sent back: each key as the line it was given as. */
export function entriesOf(SSH: KeyExchangeOverSSH): NTSClientEntry[] {

    return SSH.clients.map(client => ({
               name:     client.name,
               keys:     client.keys.map(key => key.line),
               enabled:  client.enabled
           }));

}

/**
 * A key as one line: pasted from a terminal, a long key often arrives broken
 * over several, and an authorized_keys line is one.
 */
export function oneLine(Text: string): string {
    return Text.split(/\r?\n/).map(part => part.trim()).filter(part => part.length > 0).join('');
}
