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

using System.Net;
using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;

using Org.BouncyCastle.Security;
using Org.BouncyCastle.Crypto.Parameters;

using BCX509Certificate = Org.BouncyCastle.X509.X509Certificate;

using cloud.charging.open.protocols.WWCP.Node;

#endregion

namespace cloud.charging.open.NTSServer
{

    /// <summary>
    /// The certificate NTS-KE over TLS 1.3 shows, with its private key, in the
    /// form Norn's TLS stack takes them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Either a TLS server identity of the node's certificate store - one made
    /// out to the name clients reach this server by, by the PKI next door or
    /// anybody else, and put in on the Server identities page or with
    /// --import-certificate tlsServerIdentity=&lt;file&gt; - or, where the
    /// store has none for "ntske", one made here once and kept: self-signed,
    /// which a client has to be told to trust, by its fingerprint or as a
    /// root of its own.
    /// </para>
    /// <para>
    /// Kept rather than made at every start, and that is the point of having
    /// one at all: a client that pinned it would otherwise lose this server at
    /// the next start. Norn, given none, makes a new one for every connection,
    /// which no client can pin.
    /// </para>
    /// <para>
    /// An elliptic curve key, because that is what Norn's NTS-KE takes.
    /// </para>
    /// </remarks>
    /// <param name="Certificate">The certificate, as Norn takes it.</param>
    /// <param name="PrivateKey">Its private key, as Norn takes it.</param>
    /// <param name="Subject">Whom it is made out to.</param>
    /// <param name="Names">The names and addresses it is valid for.</param>
    /// <param name="Fingerprint">The SHA-256 fingerprint of the certificate, as a client pins it.</param>
    /// <param name="NotAfter">Until when it is valid.</param>
    /// <param name="Source">Where it came from, as a sentence says it.</param>
    /// <param name="IsSelfSigned">Whether it was made here.</param>
    public sealed record NTSKETLSIdentity(BCX509Certificate       Certificate,
                                          ECPrivateKeyParameters  PrivateKey,
                                          String                  Subject,
                                          IReadOnlyList<String>   Names,
                                          String                  Fingerprint,
                                          DateTimeOffset          NotAfter,
                                          String                  Source,
                                          Boolean                 IsSelfSigned)
    {

        #region CertificatePEM

        /// <summary>
        /// The certificate alone, in PEM, as a client is told to trust it -
        /// chrony's ntstrustedcerts - and never the key with it.
        /// </summary>
        public String CertificatePEM
            => PemEncoding.WriteString("CERTIFICATE", Certificate.GetEncoded()) + "\n";

        #endregion

        #region Data

        /// <summary>
        /// The file the self-signed identity is kept in, beside the master keys.
        /// </summary>
        public const           String    SelfSignedFileName  = "ntske-tls.pem";

        /// <summary>
        /// How long a self-signed identity is made for.
        /// </summary>
        public static readonly TimeSpan  SelfSignedLifetime  = TimeSpan.FromDays(3 * 365);

        /// <summary>
        /// How long before it ends one is made anew.
        /// </summary>
        public static readonly TimeSpan  RenewBefore         = TimeSpan.FromDays(30);

        #endregion


        #region (static) TryFrom(Certificate, Source, out Identity, out Error)

        /// <summary>
        /// The identity of a certificate with its private key, as .NET has it.
        /// </summary>
        public static Boolean TryFrom(X509Certificate2                            Certificate,
                                      String                                      Source,
                                      [NotNullWhen(true)]  out NTSKETLSIdentity?  Identity,
                                      [NotNullWhen(false)] out String?            Error)
        {

            Identity = null;

            if (!Certificate.HasPrivateKey)
            {
                Error = "it has no private key";
                return false;
            }

            using var ecdsa = Certificate.GetECDsaPrivateKey();

            if (ecdsa is null)
            {
                Error = $"its key is {Certificate.PublicKey.Oid.FriendlyName ?? Certificate.PublicKey.Oid.Value}, and NTS-KE here takes an elliptic curve key";
                return false;
            }

            Byte[] pkcs8;

            try
            {
                pkcs8 = ecdsa.ExportPkcs8PrivateKey();
            }
            catch (CryptographicException e)
            {
                Error = $"its private key cannot be read out to hand it to the TLS stack: {e.Message}";
                return false;
            }

            return TryFrom(Certificate, pkcs8, Source, false, out Identity, out Error);

        }

        private static Boolean TryFrom(X509Certificate2                            Certificate,
                                       Byte[]                                      PKCS8,
                                       String                                      Source,
                                       Boolean                                     IsSelfSigned,
                                       [NotNullWhen(true)]  out NTSKETLSIdentity?  Identity,
                                       [NotNullWhen(false)] out String?            Error)
        {

            Identity = null;

            try
            {

                var bcCertificate  = new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(Certificate.RawData);

                if (PrivateKeyFactory.CreateKey(PKCS8) is not ECPrivateKeyParameters bcKey)
                {
                    Error = "its private key is no elliptic curve key";
                    return false;
                }

                Identity = new NTSKETLSIdentity(
                               bcCertificate,
                               bcKey,
                               Certificate.Subject,
                               NamesOf(Certificate),
                               Convert.ToHexString(SHA256.HashData(Certificate.RawData)),
                               Certificate.NotAfter.ToUniversalTime(),
                               Source,
                               IsSelfSigned
                           );

                Error = null;
                return true;

            }
            catch (Exception e)
            {
                Error = $"it could not be handed to the TLS stack: {e.Message}";
                return false;
            }

        }

        #endregion

        #region (static) SelfSigned(Directory, Names, TimeProvider, out Made)

        /// <summary>
        /// The self-signed identity kept in the given directory - made anew
        /// where there is none, where it is about to end, or where it is not
        /// made out to the names wanted now.
        /// </summary>
        /// <param name="Directory">Where it is kept.</param>
        /// <param name="Names">The names and addresses it is to be valid for, the first its subject.</param>
        /// <param name="TimeProvider">The clock its validity is measured by.</param>
        /// <param name="Made">Why it was made anew, or null where the one kept was taken.</param>
        public static NTSKETLSIdentity SelfSigned(String                Directory,
                                                  IReadOnlyList<String> Names,
                                                  TimeProvider          TimeProvider,
                                                  out String?           Made)
        {

            var file   = Path.Combine(Directory, SelfSignedFileName);
            var now    = TimeProvider.GetUtcNow();
            var wanted = Names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            Made = "there was none yet";

            if (File.Exists(file))
            {
                try
                {

                    var pem          = File.ReadAllText(file);
                    using var cert   = X509Certificate2.CreateFromPem(pem);
                    using var ecdsa  = ECDsa.Create();
                    ecdsa.ImportFromPem(pem);

                    var names        = NamesOf(cert);

                    if (cert.NotAfter.ToUniversalTime() - now < RenewBefore)
                        Made = $"the one kept ends on {cert.NotAfter.ToUniversalTime():yyyy-MM-dd}";

                    else if (!wanted.All(name => names.Contains(name, StringComparer.OrdinalIgnoreCase)))
                        Made = $"the one kept is made out to {String.Join(", ", names)}, and not to {String.Join(", ", wanted.Except(names, StringComparer.OrdinalIgnoreCase))}";

                    else if (TryFrom(cert, ecdsa.ExportPkcs8PrivateKey(), $"self-signed, kept in '{file}'", true, out var kept, out var error))
                    {
                        Made = null;
                        return kept;
                    }

                    else
                        Made = $"the one kept could not be used: {error}";

                }
                catch (Exception e)
                {
                    Made = $"the one kept could not be read: {e.Message}";
                }
            }

            OwnerOnly.Directory(Directory);

            using var key      = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var request        = new CertificateRequest($"CN={wanted.FirstOrDefault() ?? "localhost"}", key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.1") ], false));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            var san            = new SubjectAlternativeNameBuilder();

            foreach (var name in wanted)
            {
                if (IPAddress.TryParse(name, out var address))
                    san.AddIpAddress(address);
                else
                    san.AddDnsName(name);
            }

            request.CertificateExtensions.Add(san.Build());

            using var made     = request.CreateSelfSigned(now - TimeSpan.FromMinutes(5), now + SelfSignedLifetime);

            File.WriteAllText(file, made.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem() + "\n");
            OwnerOnly.File(file);

            if (!TryFrom(made, key.ExportPkcs8PrivateKey(), $"self-signed, made now and kept in '{file}'", true, out var identity, out var madeError))
                throw new InvalidOperationException($"The self-signed TLS identity of NTS-KE could not be made: {madeError}");

            return identity;

        }

        #endregion

        #region (static) NamesOf(Certificate)

        /// <summary>
        /// The DNS names and addresses of a certificate's subject alternative names.
        /// </summary>
        public static IReadOnlyList<String> NamesOf(X509Certificate2 Certificate)
        {

            var names = new List<String>();

            foreach (var extension in Certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>())
            {
                names.AddRange(extension.EnumerateDnsNames());
                names.AddRange(extension.EnumerateIPAddresses().Select(address => address.ToString()));
            }

            return names;

        }

        #endregion

    }

}
