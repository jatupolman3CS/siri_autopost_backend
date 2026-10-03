using System.Security.Cryptography;
using System.Text;
using SIRIAUTOPOST.Application.Interfaces;

namespace SIRIAUTOPOST.Infrastructure.Auth;

public sealed class DeviceSecrets : IDeviceSecrets
{
    // No 0/O, 1/I/L: the code is read off a screen and typed by hand.
    private const string CodeAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public string NewPairingCode()
    {
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++) chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(chars, 0, 4) + "-" + new string(chars, 4, 4);
    }

    public string NewDeviceKey() => "apd_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public string Hash(string deviceKey) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deviceKey)));
}
