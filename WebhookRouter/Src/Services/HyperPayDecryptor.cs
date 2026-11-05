using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WebhookRouter.Settings;

namespace WebhookRouter.Services;

public class HyperPayDecryptor(IOptionsMonitor<HyperPaySettings> settings, ILogger<HyperPayDecryptor> logger)
{
    public const string IvHeader = "X-Initialization-Vector";
    public const string TagHeader = "X-Authentication-Tag";

    public bool IsEnabled => settings.CurrentValue.WebhookSecretEnabled;

    public string? Decrypt(string body, string? ivHex, string? tagHex)
    {
        var secret = settings.CurrentValue.WebhookSecret.Trim();
        if (secret.Length != 64)
        {
            logger.LogError("HyperPay webhook secret is not configured as a 64-character hex key");
            return null;
        }
        if (string.IsNullOrWhiteSpace(ivHex) || string.IsNullOrWhiteSpace(tagHex)) return null;

        try
        {
            var cipherHex = body.TrimStart().StartsWith('{')
                ? JsonDocument.Parse(body).RootElement.GetProperty("encryptedBody").GetString() ?? string.Empty
                : body.Trim();
            var key = Convert.FromHexString(secret);
            var iv = Convert.FromHexString(ivHex.Trim());
            var tag = Convert.FromHexString(tagHex.Trim());
            var cipher = Convert.FromHexString(cipherHex);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(iv, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException or KeyNotFoundException or ArgumentException)
        {
            logger.LogWarning("HyperPay webhook could not be decrypted: {Reason}", ex.Message);
            return null;
        }
    }
}
