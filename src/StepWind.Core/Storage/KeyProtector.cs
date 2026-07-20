using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace StepWind.Core.Storage;

/// <summary>
/// Manages the store's AES key for encryption-at-rest. The key is 32 random bytes, sealed on
/// disk with Windows DPAPI at machine scope, so it survives reboots and is usable by the
/// unattended SYSTEM service without anyone typing a passphrase.
///
/// Honest threat model: this protects the version store if the drive is stolen or read
/// offline / on another machine (the sealed key can't be unwrapped there). It does NOT hide
/// data from another administrator on this same machine — machine-scope DPAPI is decryptable
/// by any local caller — which is the right, achievable guarantee for an always-on service.
/// A passphrase mode (protecting against local admins) can't run unattended and is out of scope.
/// </summary>
[SupportedOSPlatform("windows")]
public static class KeyProtector
{
    // Extra entropy so the sealed key isn't unwrappable by unrelated code that happens to call
    // DPAPI with the machine scope — it must know this app-specific salt too.
    private static readonly byte[] Entropy = "StepWind.store.key.v1"u8.ToArray();

    public static byte[] LoadOrCreate(string storeRoot)
    {
        Directory.CreateDirectory(storeRoot);
        string keyPath = Path.Combine(storeRoot, "store.key");

        if (File.Exists(keyPath))
        {
            byte[] sealed_ = File.ReadAllBytes(keyPath);
            return ProtectedData.Unprotect(sealed_, Entropy, DataProtectionScope.LocalMachine);
        }

        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] wrapped = ProtectedData.Protect(key, Entropy, DataProtectionScope.LocalMachine);

        string tmp = keyPath + ".tmp";
        File.WriteAllBytes(tmp, wrapped);
        File.Move(tmp, keyPath, overwrite: false);
        return key;
    }
}
