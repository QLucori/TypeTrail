using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace TypeTrail {
    public sealed class Settings {
        public int CharacterLimit = 50000;
        public uint HotkeyModifiers = 1;
        public uint HotkeyKey = 192;
        public bool Paused;
        public bool RememberHistory = true;
        public string ExcludedApps = "1Password, KeePass, KeePassXC, Bitwarden, CredentialUIBroker, mstsc";
        public bool FirstRun = true;
        public int HistoryFormat;
        public string Language = "auto";
        public bool DarkMode;
        public void Validate() {
            CharacterLimit = Math.Max(5000, Math.Min(500000, CharacterLimit));
            HotkeyModifiers &= 15;
            if (HotkeyModifiers == 0 || HotkeyKey < 8 || HotkeyKey > 254) { HotkeyModifiers = 1; HotkeyKey = 192; }
            if (ExcludedApps == null) ExcludedApps = "";
            if (Language != "en" && Language != "zh") Language = "auto";
        }
    }
}
