using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Modbus.Core.Channels;

/// <summary>Modbus/TCP Security: MBAP frames over TLS 1.2+ with optional client certificate (default port 802).</summary>
public sealed class TlsChannel : IByteChannel
{
    private readonly SslStream _ssl;

    private TlsChannel(SslStream ssl, string description)
    {
        _ssl = ssl;
        Description = description;
    }

    public string Description { get; }
    public bool FaultsOnTimeout => true;

    public static TlsChannel Connect(ConnectionSettings settings)
    {
        var timeout = TimeSpan.FromMilliseconds(settings.ConnectTimeoutMs);
        var socket = TcpChannel.ConnectSocket(settings.Host, settings.Port, timeout, settings.AddressFamily);
        var network = new NetworkStream(socket, ownsSocket: true);
        var ssl = new SslStream(network, leaveInnerStreamOpen: false);
        try
        {
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = string.IsNullOrWhiteSpace(settings.TlsServerName) ? settings.Host : settings.TlsServerName,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, cert, chain, errors) => ValidateServer(settings, cert, errors),
            };

            if (!string.IsNullOrWhiteSpace(settings.ClientCertificatePath))
                options.ClientCertificates = [LoadClientCertificate(settings)];

            network.ReadTimeout = settings.ConnectTimeoutMs;
            ssl.AuthenticateAsClient(options);
            return new TlsChannel(ssl, $"tls://{settings.Host}:{settings.Port}");
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or System.Security.Cryptography.CryptographicException)
        {
            ssl.Dispose();
            throw new ModbusConnectionException($"TLS connection failed: {ex.Message}", 13, ex);
        }
    }

    private static X509Certificate2 LoadClientCertificate(ConnectionSettings s) =>
        LoadCertificate(s.ClientCertificatePath!, s.ClientKeyPath, s.ClientCertificatePassword);

    /// <summary>Loads a PFX/P12 file, or a PEM/CRT certificate with its PEM private key.</summary>
    public static X509Certificate2 LoadCertificate(string path, string? keyPath, string? password)
    {
        var s = new ConnectionSettings { ClientCertificatePath = path, ClientKeyPath = keyPath, ClientCertificatePassword = password };
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".pfx" or ".p12")
            return X509CertificateLoader.LoadPkcs12FromFile(path, s.ClientCertificatePassword, X509KeyStorageFlags.DefaultKeySet);

        using var pem = string.IsNullOrWhiteSpace(s.ClientKeyPath)
            ? X509Certificate2.CreateFromPemFile(path)
            : string.IsNullOrEmpty(s.ClientCertificatePassword)
                ? X509Certificate2.CreateFromPemFile(path, s.ClientKeyPath)
                : X509Certificate2.CreateFromEncryptedPemFile(path, s.ClientCertificatePassword, s.ClientKeyPath);
        // Windows TLS (SChannel) cannot use an ephemeral PEM key; round-trip through PKCS#12.
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.DefaultKeySet);
    }

    private static bool ValidateServer(ConnectionSettings settings, X509Certificate? cert, SslPolicyErrors errors)
    {
        if (settings.AllowUntrustedServer) return true;
        if (cert is null) return false;
        if (string.IsNullOrWhiteSpace(settings.TrustedCaPath)) return errors == SslPolicyErrors.None;

        // Trust only the configured CA. The host name must still match.
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
        using var ca = X509CertificateLoader.LoadCertificateFromFile(settings.TrustedCaPath);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using var serverCert = new X509Certificate2(cert);
        return chain.Build(serverCert);
    }

    public void Write(byte[] data)
    {
        try
        {
            _ssl.Write(data);
            _ssl.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new IOException($"Send failed: {ex.Message}", ex);
        }
    }

    public int Read(byte[] buffer, int offset, int count, TimeSpan timeout)
    {
        try
        {
            _ssl.ReadTimeout = ChannelExtensions.ToTimeoutMs(timeout) is 0 ? System.Threading.Timeout.Infinite : ChannelExtensions.ToTimeoutMs(timeout);
            var n = _ssl.Read(buffer, offset, count);
            if (n == 0) throw new IOException("Connection closed by server.");
            return n;
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
        {
            throw new TimeoutException();
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException("The connection is closed.", ex);
        }
    }

    public void DiscardInput()
    {
        // TLS records cannot be skipped safely; stale responses are dropped by transaction ID.
    }

    public void Dispose() => _ssl.Dispose();
}
