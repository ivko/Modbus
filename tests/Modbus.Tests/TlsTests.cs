using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Modbus.Core;
using Modbus.Core.Server;

namespace Modbus.Tests;

public sealed class TlsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mm-tls-{Guid.NewGuid():N}");

    public TlsTests() => Directory.CreateDirectory(_dir);

    private static X509Certificate2 SelfSigned(string cn, bool server)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));
        if (server)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            req.CertificateExtensions.Add(san.Build());
        }
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30));
        // Windows TLS needs a persisted key: round-trip through PKCS#12.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    private ModbusTcpSimulator StartServer(X509Certificate2 cert, bool requireClientCert, out ModbusDataStore store)
    {
        store = new ModbusDataStore();
        var sim = new ModbusTcpSimulator(IPAddress.Loopback, 0, SimulatorFraming.Mbap, new ModbusRequestHandler(store))
        {
            ServerCertificate = cert,
            RequireClientCertificate = requireClientCert,
        };
        sim.Start();
        return sim;
    }

    [Fact]
    public void Tls_with_trusted_ca_and_pem_client_certificate()
    {
        using var serverCert = SelfSigned("localhost", server: true);
        using var sim = StartServer(serverCert, requireClientCert: true, out var store);
        store.HoldingRegisters[5] = 4242;

        // The self-signed server certificate acts as its own CA.
        var caPath = Path.Combine(_dir, "ca.cer");
        File.WriteAllBytes(caPath, serverCert.Export(X509ContentType.Cert));

        using var clientCert = SelfSigned("modbus-client", server: false);
        var certPem = Path.Combine(_dir, "client.pem");
        var keyPem = Path.Combine(_dir, "client.key");
        File.WriteAllText(certPem, clientCert.ExportCertificatePem());
        File.WriteAllText(keyPem, clientCert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());

        using var client = ModbusConnection.Create(new ConnectionSettings
        {
            Kind = ConnectionKind.TcpSecurity, Host = "localhost", Port = sim.Port, ResponseTimeoutMs = 2000, ConnectTimeoutMs = 5000,
            TrustedCaPath = caPath, ClientCertificatePath = certPem, ClientKeyPath = keyPem, DelayBetweenPollsMs = 0,
        });
        Assert.Equal(new ushort[] { 4242 }, client.ReadHoldingRegisters(1, 5, 1));
        client.WriteSingleRegister(1, 6, 7);
        Assert.Equal(7, store.HoldingRegisters[6]);
        Assert.Equal("CN=modbus-client", sim.LastClientCertificateSubject);
    }

    [Fact]
    public void Untrusted_server_is_rejected_unless_allowed()
    {
        using var serverCert = SelfSigned("localhost", server: true);
        using var sim = StartServer(serverCert, requireClientCert: false, out _);

        var strict = ModbusConnection.Create(new ConnectionSettings { Kind = ConnectionKind.TcpSecurity, Host = "localhost", Port = sim.Port, ConnectTimeoutMs = 5000 });
        Assert.Throws<ModbusConnectionException>(strict.Open);

        using var relaxed = ModbusConnection.Create(new ConnectionSettings
        {
            Kind = ConnectionKind.TcpSecurity, Host = "127.0.0.1", Port = sim.Port, ConnectTimeoutMs = 5000, AllowUntrustedServer = true, DelayBetweenPollsMs = 0,
        });
        Assert.Single(relaxed.ReadHoldingRegisters(1, 0, 1));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
