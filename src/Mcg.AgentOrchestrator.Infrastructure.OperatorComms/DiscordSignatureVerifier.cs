using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class DiscordSignatureVerifier
{
    public static bool Verify(string publicKeyHex, string timestamp, string body, string signatureHex)
    {
        try
        {
            var publicKeyBytes = Convert.FromHexString(publicKeyHex);
            var signatureBytes = Convert.FromHexString(signatureHex);
            var messageBytes = Encoding.UTF8.GetBytes(timestamp + body);
            return Verify(publicKeyBytes, messageBytes, signatureBytes);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }

    public static bool Verify(byte[] publicKeyBytes, byte[] messageBytes, byte[] signatureBytes)
    {
        try
        {
            var publicKey = new Ed25519PublicKeyParameters(publicKeyBytes);
            var signer = new Ed25519Signer();
            signer.Init(false, publicKey);
            signer.BlockUpdate(messageBytes, 0, messageBytes.Length);
            return signer.VerifySignature(signatureBytes);
        }
        catch
        {
            return false;
        }
    }
}
