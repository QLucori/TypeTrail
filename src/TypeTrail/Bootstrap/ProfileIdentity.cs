using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TypeTrail {
    internal static class ProfileIdentity {
        public static string Normalize(string folder) {
            string full = Path.GetFullPath(folder).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            int rootLength = Path.GetPathRoot(full).Length;
            while (full.Length > rootLength && full[full.Length - 1] == Path.DirectorySeparatorChar)
                full = full.Substring(0, full.Length - 1);
            return full;
        }
        public static string Key(string folder) {
            using (var sha = SHA256.Create()) {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Normalize(folder).ToLowerInvariant() + Environment.UserName));
                return "Local\\TypeTrail-" + BitConverter.ToString(hash, 0, 12).Replace("-", "");
            }
        }
    }
}
