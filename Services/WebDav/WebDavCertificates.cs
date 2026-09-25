using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WinUIMusicPlayer.Services.WebDav;

public sealed record WebDavCertificateTrust(string Origin, string Sha256);

public sealed record WebDavCertificate(string Origin, string Subject, string Issuer, string Sha256,
    DateTimeOffset NotBefore, DateTimeOffset NotAfter, bool NameMismatch, bool ChainErrors)
{
    public bool CanTrust => DateTimeOffset.UtcNow >= NotBefore && DateTimeOffset.UtcNow <= NotAfter;
}

public sealed class WebDavCertificateException(WebDavCertificate certificate, bool changed)
    : WebDavException(changed ? "CertificateChanged" : "CertificateUntrusted")
{
    public WebDavCertificate Certificate { get; } = certificate;
}

/// <summary>Separate pools prevent a confirmed certificate's TLS connection being reused by an unconfirmed source.</summary>
internal sealed class WebDavHttpClients : IDisposable
{
    internal static readonly HttpRequestOptionsKey<WebDavCertificate> RejectedCertificate = new("WebDav.RejectedCertificate");
    private readonly object _gate = new();
    private readonly Dictionary<(string Root, string Fingerprint), Entry> _clients = [];
    private bool _disposed;

    internal sealed class Entry(HttpClient client)
    {
        public readonly HttpClient Client = client;
        public int Users;
    }

    internal sealed class Lease(WebDavHttpClients owner, Entry entry) : IDisposable
    {
        private int _disposed;
        public HttpClient Client => entry.Client;
        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._gate) entry.Users--;
        }
    }

    public Lease Acquire(WebDavConnection source, Uri target)
    {
        var trust = source.CertificateTrust;
        bool pinned = trust is not null && target.Scheme == "https" && WebDavTransport.IsWithin(source.Root, target) &&
            string.Equals(trust.Origin, source.Root.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);
        var key = pinned ? (source.Root.AbsoluteUri, trust!.Sha256) : ("", "");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_clients.TryGetValue(key, out var entry))
            {
                // The transport admits at most eight live responses, so a sixteen-entry pool always has idle capacity.
                if (_clients.Count >= 16)
                {
                    var idle = _clients.First(x => x.Value.Users == 0);
                    _clients.Remove(idle.Key);
                    idle.Value.Client.Dispose();
                }
                // Never capture source: it contains credentials and must not be retained by the pool.
                Uri? certificateRoot = pinned ? source.Root : null;
                WebDavCertificateTrust? certificateTrust = pinned ? trust : null;
                var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
                    UseCookies = false, MaxConnectionsPerServer = 8,
                    ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
                        Validate(request, certificate, errors, certificateRoot, certificateTrust)
                };
                entry = new Entry(new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan });
                _clients.Add(key, entry);
            }
            entry.Users++;
            return new Lease(this, entry);
        }
    }

    private static bool Validate(HttpRequestMessage request, X509Certificate2? certificate, SslPolicyErrors errors,
        Uri? root, WebDavCertificateTrust? trust)
    {
        if (certificate is null || request.RequestUri is not { } target) return false;
        if (trust is null && errors == SslPolicyErrors.None) return true;
        var info = new WebDavCertificate(target.GetLeftPart(UriPartial.Authority), certificate.Subject, certificate.Issuer,
            certificate.GetCertHashString(HashAlgorithmName.SHA256), certificate.NotBefore, certificate.NotAfter,
            errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch), errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors));
        if (trust is not null && root is not null && WebDavTransport.IsWithin(root, target) && info.CanTrust &&
            string.Equals(info.Sha256, trust.Sha256, StringComparison.OrdinalIgnoreCase)) return true;
        request.Options.Set(RejectedCertificate, info);
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _clients.Values) entry.Client.Dispose();
            _clients.Clear();
        }
    }
}
