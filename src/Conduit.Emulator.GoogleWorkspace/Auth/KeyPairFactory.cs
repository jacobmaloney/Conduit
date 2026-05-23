using System.Security.Cryptography;

namespace Conduit.Emulator.GoogleWorkspace.Auth;

public sealed record KeyPair(string PublicPem, string PrivatePem);

public static class KeyPairFactory
{
    public static KeyPair NewRsa2048()
    {
        using var rsa = RSA.Create(2048);
        var privPem = rsa.ExportPkcs8PrivateKeyPem();
        var pubPem = rsa.ExportSubjectPublicKeyInfoPem();
        return new KeyPair(pubPem, privPem);
    }

    public static RSA LoadPublicKey(string pem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa;
    }

    public static RSA LoadPrivateKey(string pem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa;
    }
}
