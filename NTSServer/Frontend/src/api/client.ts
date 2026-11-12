import { nodeAPI,
         request,
         type Certificate        as NodeCertificate,
         type CertificateImport  as NodeCertificateImport,
         type CertificateStore   as NodeCertificateStore,
         type NodeConfiguration,
         type NodeMe,
         type NodeResource,
         type NodeStatus,
         type Operation }  from '@node/api/client';


// What every node answers - the log, name resolution, the time, the store,
// who is signed in - and how it is asked are WWCP_Node's, and every page here
// reads them from this module. What follows is what an NTS server says of its
// own: its resources, its configuration's own section, the kinds its store
// keeps - and the time server itself, below "/ntsServer".
export * from '@node/api/client';


/**
 * What a role may be allowed to touch on this NTS server: what every node
 * has, the time server - read, how it stands; edited, its NTS clients and
 * NTS-KE over SSH - and a key exchange over SSH by an account.
 */
export type Resource = NodeResource | 'timeserver' | 'ntske';

/** What somebody signed in to this NTS server may do: an operation on a resource, written "timeserver:edit". */
export type Permission = `${Resource}:${Operation}`;

/** Who is signed in to the web interface. */
export type Me = NodeMe<Resource>;

/** How the node is doing right now: what every node says. */
export type Status = NodeStatus;

/**
 * What the NTS server is made of: every node's sections, and its own. Only
 * the shape the Configuration page relies on is named; the fields of each
 * section are drawn from whatever the server sends.
 */
export interface Configuration extends NodeConfiguration {
    ntsServer:  Record<string, unknown>;
}

/**
 * What a certificate in the node's own store is for: the four kinds of TLS -
 * the roots its upstream time servers are held to among them - and the
 * identity NTS-KE over TLS and the web interface over HTTPS show.
 */
export type CertificateKind = 'tlsRoot' | 'clientRoot' | 'tlsServer' | 'tlsIdentity' | 'tlsServerIdentity';

/** One certificate in the node's own store. */
export type Certificate = NodeCertificate<CertificateKind>;

/** What an import into the node's own store sends. */
export type CertificateImport = NodeCertificateImport<CertificateKind>;

/** The node's own store, grouped the way it is shown. */
export type CertificateStore = NodeCertificateStore<CertificateKind>;


/** Where the time handed out comes from: this machine's clock, or a group of upstream NTS servers. */
export type TimeSource = 'local' | 'upstream';

/** What the answers say of the clock handed out (RFC 5905 § 7.3). */
export interface ClockDescription {
    stratum:           number;
    /** "LOCL", or the address of the upstream server closest to the median. */
    referenceId:       string;
    rootDelayMs:       number;
    /** Null where it is measured anew for every answer, as in local mode by default. */
    rootDispersionMs:  number | null;
    /** 0 without a warning, 1 and 2 announcing a leap second inserted or deleted at midnight, 3 unsynchronized. */
    leapIndicator:     number;
}

/** A leap second at the end of a UTC month, scheduled to be made by the clock handed out. */
export interface LeapSecond {
    /** The last day of the month, at whose end it is made: "2026-12-31". */
    day:            string;
    /** Inserted - the last minute has 61 seconds - or deleted - it has 59. */
    inserted:       boolean;
    leapIndicator:  number;
}

/** How the upstream servers were asked, and what was made of their answers. */
export interface UpstreamStatus {
    group:                 string;
    synchronized:          boolean;
    synchronizedSince:     string | null;
    lastPoll:              string | null;
    lastGood:              string | null;
    lastGoodAgeSeconds:    number | null;
    lastProblem:           string | null;
    /** How far the clock handed out was from the median of the group, at the last round. */
    offsetMs:              number | null;
    /** How far it was from where it was meant to be: the error the slew is making up. */
    errorMs:               number | null;
    jitterMs:              number | null;
    /** How far apart the servers that answered were. */
    spreadMs:              number | null;
    /** The upstream server closest to the median, which the answers name as the reference. */
    systemPeer:            string | null;
    upstreamStratum:       number | null;
    answered:              number | null;
    asked:                 number | null;
    frequencyPPM:          number;
    correctionMs:          number | null;
    slewRemainingSeconds:  number;
    polls:                 number;
    failedPolls:           number;
    outliers:              number;
    steps:                 number;
    description:           ClockDescription | null;
    /** What more than half of the answers of the last usable round announced: 0 where they did not agree on 1 or 2. */
    leapIndicatorAnnounced: number | null;
    /** The leap second taken up from them, until it is made: only on the last day of a month. */
    leapSecond:            LeapSecond | null;
    /** The leap seconds made since the start, added up: -1 after an inserted one. */
    leapSecondsMade:       number;
}

/** The certificate NTS-KE over TLS shows. */
export interface NTSKEIdentity {
    subject:      string;
    names:        string[];
    /** The SHA-256 fingerprint of the certificate, in hex, as a client pins it. */
    fingerprint:  string;
    notAfter:     string;
    /** Where it came from, as a sentence says it. */
    source:       string;
    selfSigned:   boolean;
    /** The certificate alone, in PEM - never the key. */
    pem:          string;
}

/**
 * Where the keys of a key exchange over SSH came from (section 4 of the white
 * paper "NTS Key Establishment over SSH"): option A, the exporter of the SSH
 * connection, or option B, a secret the server sent within it.
 */
export type SSHKeyBinding = 'exporter' | 'transport';

/**
 * What one login made of NTS-KE over SSH since the program started: when it
 * was last served and how, when it was last turned away and why, and how
 * often. Kept in memory only - what lasts is in the log.
 */
export interface SSHActivity {
    served:       number;
    refused:      number;
    /** How many cookies went out in all. */
    cookies:      number;
    lastServed:   { at: string; from: string | null; method: SSHKeyBinding; aeadAlgorithm: number | null; cookies: number } | null;
    lastRefused:  { at: string; from: string | null; why: string | null } | null;
}

/** A login that made key exchanges over SSH but is no NTS client: an account of the node that may run "ntske". */
export interface SSHAccount {
    name:      string;
    activity:  SSHActivity;
}

/** One master key the cookies are sealed with: its id and when it is valid - never its value. */
export interface MasterKey {
    id:         number;
    notBefore:  string;
    notAfter:   string;
    /** Whether it seals the cookies made now; the others still open the cookies they sealed. */
    current:    boolean;
}

/** What a rotation of the master key answers: POST /ntsServer/masterKeys/rotate. */
export interface MasterKeyRotation {
    rotated:     MasterKey;
    /** Whether the earlier keys were forgotten at once. */
    forgotten:   boolean;
    masterKeys:  MasterKey[];
}

/**
 * One key the responses are signed with: its public key and when it is valid -
 * never its private key. "signs" signs a response that asks for no key in
 * particular; "valid" only one that asks for it by its id; "next" has not
 * begun, and its public key is handed out in the key exchange beforehand;
 * "retired" signs nothing any more.
 */
export interface SigningKey {
    id:                  number;
    publicKey:           string;
    ellipticCurve:       string;
    signatureAlgorithm:  string;
    notBefore:           string | null;
    notAfter:            string | null;
    state:               'signs' | 'valid' | 'next' | 'retired';
}

/** What a new signing key answers: POST /ntsServer/signingKeys. */
export interface SigningKeyMade {
    made:         SigningKey;
    signingKeys:  SigningKey[];
}

/** What a signing key retired answers: POST /ntsServer/signingKeys/retire. */
export interface SigningKeyRetired {
    retired:      SigningKey;
    signingKeys:  SigningKey[];
}

/** How the time server stands: GET /ntsServer. */
export interface TimeServerStatus {
    timeSource:    TimeSource;
    /** Whether a server runs and answers: in upstream mode, not before the first time and not after the holdover. */
    serving:       boolean;
    servingSince:  string | null;
    /** How often the server was made: anew whenever the description of the clock changes. */
    generations:   number;
    listen:        string;
    ntsKE:         { port: number; tls: NTSKEIdentity };
    ntp:           { port: number; advertised: string | null };
    ssh:           { enabled: boolean; served: boolean; subsystem: string; url: string | null; hostKey: string | null;
                     clients: { name: string; enabled: boolean; keys: number; activity: SSHActivity }[];
                     accounts: SSHAccount[] };
    /** The master keys of the cookies, oldest first. None while nothing is answered. */
    masterKeys:    MasterKey[];
    /** The keys the responses are signed with, by their id - retired ones among them. */
    signingKeys:   SigningKey[];
    clock:        { now: string; base: 'free-running' | 'system'; correctionMs: number; frequencyPPM: number;
                     /** The leap seconds made since the start, added up. */
                     leapSeconds: number;
                     /** The leap second scheduled and not yet made. */
                     leapSecond: LeapSecond | null;
                     description: ClockDescription | null };
    upstream:      UpstreamStatus | null;
    /** What it answered since the program started, over every server made since. */
    metrics:       Record<string, number>;
}

/** One key of an NTS client: the authorized_keys line it was given as, and what it is. */
export interface NTSClientKey {
    line:          string;
    algorithm?:    string;
    /** "SHA256:...", as ssh-keygen -l says it. */
    fingerprint?:  string;
    comment?:      string | null;
    /** Until when it may sign in, where its line says so. */
    notAfter?:     string | null;
    options?:      string[];
    /** Whether it is the key of an SSH CA, whose user certificates let the client in. */
    certAuthority?:  boolean;
    /** The names a certificate of the CA has to be made out to - null for the client's own name. */
    principals?:     string[] | null;
}

/** One machine that may make key exchanges over SSH, under its name, with one of its keys. */
export interface NTSClient {
    name:      string;
    enabled:   boolean;
    keys:      NTSClientKey[];
    activity:  SSHActivity;
}

/** NTS-KE over SSH and the NTS clients: GET and PUT /ntsServer/ssh. */
export interface KeyExchangeOverSSH {
    enabled:    boolean;
    /** Whether it is served now: switched on, and the node's SSH server runs. */
    served:     boolean;
    subsystem:  string;
    url:        string | null;
    /** The host key a client holds the server to. */
    hostKey:    string | null;
    clients:    NTSClient[];
    /** The accounts of the node that made key exchanges over SSH. */
    accounts:   SSHAccount[];
}

/** One NTS client as it is sent: its keys as authorized_keys lines. */
export interface NTSClientEntry {
    name:     string;
    keys:     string[];
    enabled:  boolean;
}

/** What local mode says of the clock in every answer. */
export interface LocalClockSettings {
    stratum:           number;
    referenceId:       string;
    /** Null for what is measured with every answer. */
    rootDispersionMs:  number | null;
}

/** How upstream mode follows its NTS group. */
export interface UpstreamSettings {
    group:            string;
    pollSeconds:      number;
    stepThresholdMs:  number;
    maxSlewPPM:       number;
    holdoverMinutes:  number;
}

/** The "ntsServer" section as it is in effect - the switches of this start included. */
export interface TimeServerSettings {
    listen:      string;
    ntsKEPort:   number;
    ntpPort:     number;
    hostname:    string | null;
    ntpServer:   string | null;
    timeSource:  TimeSource;
    local:       LocalClockSettings;
    upstream:    UpstreamSettings;
    rateLimit:   boolean;
    ssh:         { enabled: boolean; clients: NTSClientEntry[] };
}

/**
 * What a change of the settings sends: any of them but "ssh" - what it
 * leaves out stays, a null is the default. Only what differs from what is in
 * effect is changed and saved.
 */
export interface TimeServerSettingsChange {
    listen?:      string | null;
    ntsKEPort?:   number | null;
    ntpPort?:     number | null;
    hostname?:    string | null;
    ntpServer?:   string | null;
    timeSource?:  TimeSource;
    local?:       { [K in keyof LocalClockSettings]?: LocalClockSettings[K] | null };
    upstream?:    { [K in keyof UpstreamSettings]?:   UpstreamSettings[K]   | null };
    rateLimit?:   boolean;
}

/** What a change came to: the settings in effect now, what changed - "ntpPort", "upstream.group" - and what whoever changed it has to know. */
export interface TimeServerSettingsChanged {
    settings:  TimeServerSettings;
    changed:   string[];
    notes:     string[];
}

/** What a change of NTS-KE over SSH sends: what it changes, and nothing else. */
export interface SSHChange {
    enabled?:  boolean;
    clients?:  NTSClientEntry[];
}


/**
 * How long a round of the upstream servers may take before the page stops
 * waiting: a group is asked band by band, each server's key exchange with
 * its own time limit, which the 30 seconds of any other change do not cover.
 */
export const roundWithin = 90_000;

/** How the NTS server is asked: the routes every node has, typed with the server's own of them, and the time server. */
export const api = {

    ...nodeAPI<{ me: Me; status: Status; configuration: Configuration; kind: CertificateKind; store: CertificateStore }>(),

    ntsServer: {
        status:    ()                   => request<TimeServerStatus>   ('GET',  '/ntsServer'),
        settings:     ()                                   => request<TimeServerSettings>       ('GET',  '/ntsServer/settings'),
        /**
         * A change of the settings, in effect at once. A time source changed
         * to upstream waits for nothing: the answer comes before the group has
         * given a time, and says so.
         */
        setSettings:  (change: TimeServerSettingsChange)   => request<TimeServerSettingsChanged>('PUT',  '/ntsServer/settings', change),
        sync:      ()                   => request<UpstreamStatus>     ('POST', '/ntsServer/sync', undefined, roundWithin),
        ssh:       ()                   => request<KeyExchangeOverSSH> ('GET',  '/ntsServer/ssh'),
        setSSH:    (change: SSHChange)  => request<KeyExchangeOverSSH> ('PUT',  '/ntsServer/ssh', change),
        rotateMasterKey: (forget: boolean) => request<MasterKeyRotation>('POST', '/ntsServer/masterKeys/rotate', { forget }),
        newSigningKey:    (atOnce: boolean) => request<SigningKeyMade>   ('POST', '/ntsServer/signingKeys', { atOnce }),
        retireSigningKey: (id: number)      => request<SigningKeyRetired>('POST', '/ntsServer/signingKeys/retire', { id })
    }

};
