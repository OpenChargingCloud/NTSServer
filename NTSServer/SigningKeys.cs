/*
 * Copyright (c) 2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of NTSServer <https://github.com/OpenChargingCloud/NTSServer>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Norn.NTS;

using cloud.charging.open.protocols.WWCP.Node;

#endregion

namespace cloud.charging.open.NTSServer
{

    /// <summary>
    /// What a signing key is at a given time.
    /// </summary>
    public enum SigningKeyState
    {

        /// <summary>
        /// It signs the responses of a request that asks for no key in
        /// particular: of the keys valid now, the one that began last.
        /// </summary>
        Signs,

        /// <summary>
        /// Valid, but another began after it: it signs only where a request
        /// asks for it by its id.
        /// </summary>
        Valid,

        /// <summary>
        /// Not valid yet: its public key is handed out in the key exchange
        /// beforehand, so that clients know it when it takes over.
        /// </summary>
        Next,

        /// <summary>
        /// No longer valid: it signs nothing, and its public key is no longer
        /// handed out.
        /// </summary>
        Retired

    }


    /// <summary>
    /// The keys the NTS responses are signed with, kept in a file of their own
    /// beside the master keys of the cookies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Norn signs a response with the key a request asks for by its id, or -
    /// asked for none in particular - with the valid one that began last, and
    /// hands out the public keys of the valid ones and of those that begin
    /// within a day in the key exchange. It keeps them only as long as one
    /// server runs, and a server here is made anew whenever the description of
    /// the clock changes: so they are kept here, and handed to every server
    /// made.
    /// </para>
    /// <para>
    /// The first start makes one key, without a first day: valid at once and
    /// whatever the clock said when it was made, which in upstream mode is not
    /// yet the time of the upstream servers. A new key begins a day later, so
    /// that the clients have learnt its public key from a key exchange before
    /// it signs - or at once, for a key that may have been read. A key is
    /// never deleted, only retired: what a client was told by a key exchange
    /// keeps its meaning.
    /// </para>
    /// <para>
    /// The private keys are written to a file only its owner may read, in a
    /// directory only its owner may enter: whoever reads them can sign a time
    /// in this server's name.
    /// </para>
    /// </remarks>
    public sealed class SigningKeys
    {

        #region Data

        /// <summary>
        /// The file the signing keys are kept in, beside the master keys of the cookies.
        /// </summary>
        public const           String    FileName  = "signingKeys.json";

        /// <summary>
        /// How long before its first day a new key's public key is handed out:
        /// as long as Norn hands it out beforehand.
        /// </summary>
        public static readonly TimeSpan  LeadTime  = TimeSpan.FromDays(1);

        private readonly Object          sync      = new ();
        private readonly TimeProvider    clock;
        private readonly Action<String>  complain;
        private          KeyPair[]       keys;

        /// <summary>
        /// Whether what was in the file could not be read: then nothing is
        /// written to it, so that it is there to be looked at.
        /// </summary>
        private readonly Boolean         unreadable;

        #endregion

        #region Properties

        /// <summary>
        /// Where the keys are kept, or null for in memory only.
        /// </summary>
        public String?                File    { get; }

        /// <summary>
        /// Every key, by its id - retired ones among them.
        /// </summary>
        public IReadOnlyList<KeyPair>  All
        {
            get
            {
                lock (sync)
                    return keys;
            }
        }

        #endregion

        #region Constructor(s)

        private SigningKeys(String?         File,
                            TimeProvider    Clock,
                            Action<String>  Complain,
                            KeyPair[]       Keys,
                            Boolean         Unreadable)
        {

            this.File        = File;
            this.clock       = Clock;
            this.complain    = Complain;
            this.keys        = Keys;
            this.unreadable  = Unreadable;

        }

        #endregion


        #region (static) Load(File, Clock, Complain)

        /// <summary>
        /// The keys kept in the given file - or, where there is none yet, a
        /// first key, written to it.
        /// </summary>
        /// <param name="File">Where the keys are kept, or null for in memory only.</param>
        /// <param name="Clock">The clock handed out, which the keys are valid by.</param>
        /// <param name="Complain">Told what went wrong with the file.</param>
        public static SigningKeys Load(String?          File,
                                       TimeProvider     Clock,
                                       Action<String>?  Complain = null)
        {

            var complain = Complain ?? (_ => { });

            if (File is not null && System.IO.File.Exists(File))
            {

                if (TryRead(File, out var read, out var problem))
                    return new SigningKeys(File, Clock, complain, read, Unreadable: false);

                complain($"The signing keys in '{File}' could not be read, so no response is signed, and the file is left as it is: {problem}");

                return new SigningKeys(File, Clock, complain, [], Unreadable: true);

            }

            var first = new SigningKeys(File, Clock, complain, [ KeyPair.GenerateECKeys(1) ], Unreadable: false);

            first.Save();

            return first;

        }

        #endregion

        #region (static) TryRead(File, out Keys, out Problem)

        private static Boolean TryRead(String                                                   File,
                                       [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]  out KeyPair[]?  Keys,
                                       [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out String?     Problem)
        {

            Keys    = null;
            Problem = null;

            try
            {

                if (JToken.Parse(System.IO.File.ReadAllText(File)) is not JArray array)
                {
                    Problem = "it is no JSON array";
                    return false;
                }

                var read = new List<KeyPair>();

                foreach (var token in array)
                {

                    if (token is not JObject json)
                    {
                        Problem = "a key is no JSON object";
                        return false;
                    }

                    if (!KeyPair.TryParse(json, out var key, out var error))
                    {
                        Problem = $"a key does not read as one: {error}";
                        return false;
                    }

                    if (key.Id == 0 || read.Any(other => other.Id == key.Id))
                    {
                        Problem = $"the key id {key.Id} is {(key.Id == 0 ? "0, which a request asks for no key in particular by" : "there twice")}";
                        return false;
                    }

                    read.Add(key);

                }

                Keys = [.. read.OrderBy(key => key.Id)];
                return true;

            }
            catch (Exception e)
            {
                Problem = e.Message;
                return false;
            }

        }

        #endregion


        #region StateOf(Key)

        /// <summary>
        /// What the given key is now.
        /// </summary>
        public SigningKeyState StateOf(KeyPair Key)
        {

            var now = clock.GetUtcNow();

            if (Key.NotAfter < now)
                return SigningKeyState.Retired;

            if (Key.NotBefore > now)
                return SigningKeyState.Next;

            return Current?.Id == Key.Id
                       ? SigningKeyState.Signs
                       : SigningKeyState.Valid;

        }

        #endregion

        #region Current

        /// <summary>
        /// The key a response is signed with now when the request asks for no
        /// key in particular - as Norn chooses it: of the keys valid now, the
        /// one that began last, a key without a first day counting as the
        /// oldest; between two that began together the higher id.
        /// </summary>
        public KeyPair? Current
        {
            get
            {

                var now = clock.GetUtcNow();

                return All.Where            (key => key.IsValidAt(now)).
                           OrderByDescending(key => key.NotBefore ?? DateTimeOffset.MinValue).
                           ThenByDescending (key => key.Id).
                           FirstOrDefault();

            }
        }

        #endregion


        #region New(AtOnce = false)

        /// <summary>
        /// A new key: beginning a day from now, so that the clients have its
        /// public key before it signs - or, <paramref name="AtOnce"/>, now.
        /// Null where the file could not be read, and is left as it is.
        /// </summary>
        public KeyPair? New(Boolean AtOnce = false)
        {

            lock (sync)
            {

                if (unreadable)
                    return null;

                var id      = (UInt16) (keys.Length == 0 ? 1 : keys.Max(key => key.Id) + 1);

                if (id == 0)
                    return null;

                var made    = KeyPair.GenerateECKeys(id);
                var now     = clock.GetUtcNow();

                var key     = new KeyPair(made.Id,
                                          made.PrivateKey,
                                          made.PublicKey,
                                          EllipticCurve:       made.EllipticCurve,
                                          SignatureAlgorithm:  made.SignatureAlgorithm,
                                          NotBefore:           AtOnce ? now : now + LeadTime);

                keys = [.. keys, key];

                Save();

                return key;

            }

        }

        #endregion

        #region Retire(Id)

        /// <summary>
        /// Let the key of the given id sign nothing from now on, and its public
        /// key no longer be handed out. Null where there is no such key, or it
        /// is retired already, or the file could not be read.
        /// </summary>
        public KeyPair? Retire(UInt16 Id)
        {

            lock (sync)
            {

                if (unreadable)
                    return null;

                var now = clock.GetUtcNow();

                if (keys.FirstOrDefault(key => key.Id == Id) is not KeyPair key || key.NotAfter < now)
                    return null;

                // Ended a moment ago, so that it is past at once - also a key that
                // has not begun yet, whose public key is then no longer handed out
                // beforehand either.
                var retired = new KeyPair(key.Id,
                                          key.PrivateKey,
                                          key.PublicKey,
                                          key.Description,
                                          key.EllipticCurve,
                                          key.SignatureAlgorithm,
                                          key.NotBefore,
                                          now.AddTicks(-1));

                keys = [.. keys.Select(other => other.Id == Id ? retired : other)];

                Save();

                return retired;

            }

        }

        #endregion


        #region (private) Save()

        /// <summary>
        /// Into the file, by way of another one moved over it - made anew, so
        /// that it is made readable by its owner alone before a key is in it.
        /// </summary>
        private void Save()
        {

            if (File is null || unreadable)
                return;

            var next = File + ".next";

            try
            {

                System.IO.File.Delete(next);

                var options = new FileStreamOptions {
                                  Mode    = FileMode.CreateNew,
                                  Access  = FileAccess.Write,
                                  Share   = FileShare.None
                              };

                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

                using (new FileStream(next, options)) { }

                if (OperatingSystem.IsWindows())
                    OwnerOnly.File(next, complain);

                System.IO.File.WriteAllText(next, new JArray(keys.Select(key => key.ToJSON())).ToString(Formatting.Indented));

                System.IO.File.Move(next, File, overwrite: true);

            }
            catch (Exception e)
            {
                complain($"The signing keys could not be written to '{File}', and are lost at the next start: {e.Message}");
            }

        }

        #endregion

    }

}
