using System;
using System.Linq;
namespace TypeTrail {
    internal static class SensitiveFieldRules {
        public static bool Name(string name) {
            if(String.IsNullOrEmpty(name) || name.Length>100)return false;
            string lower=name.ToLowerInvariant();
            return new[]{"password","passphrase","密码","口令","验证码","verification code","one-time","api key","secret key","密钥"}.Any(lower.Contains);
        }
    }
}
