using System;
using System.Security.Cryptography;

namespace Conduit.DataAccess
{
    /// <summary>
    /// PBKDF2 password hashing (SHA-256, 100k iterations, 32-byte salt, 32-byte derived key).
    /// Salt and hash are stored separately as base64 strings.
    /// </summary>
    public static class PasswordHasher
    {
        private const int SaltBytes = 32;
        private const int HashBytes = 32;

        // OWASP 2023 recommendation for PBKDF2-SHA256. New hashes use this cost.
        // Older hashes created before the bump are still accepted via the legacy
        // iteration count in <see cref="LegacyIterations"/> — Verify tries both.
        public const int Iterations = 600_000;
        private const int LegacyIterations = 100_000;

        public static (string Hash, string Salt) Hash(string password)
        {
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password is required.", nameof(password));

            var saltBytes = RandomNumberGenerator.GetBytes(SaltBytes);
            var hashBytes = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, Iterations, HashAlgorithmName.SHA256, HashBytes);
            return (Convert.ToBase64String(hashBytes), Convert.ToBase64String(saltBytes));
        }

        /// <summary>
        /// Constant-time verification. Tries the current cost factor first; if that
        /// fails, transparently re-tries at the legacy cost so credentials minted
        /// before the OWASP bump still work. Callers wanting to upgrade-on-login
        /// can call <see cref="NeedsRehash"/> after a successful Verify.
        /// </summary>
        public static bool Verify(string password, string storedHash, string storedSalt)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(storedSalt))
            {
                return false;
            }
            byte[] saltBytes;
            byte[] expected;
            try
            {
                saltBytes = Convert.FromBase64String(storedSalt);
                expected = Convert.FromBase64String(storedHash);
            }
            catch (FormatException)
            {
                return false;
            }

            var current = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, Iterations, HashAlgorithmName.SHA256, expected.Length);
            if (CryptographicOperations.FixedTimeEquals(current, expected)) return true;

            var legacy = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, LegacyIterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(legacy, expected);
        }
    }
}
