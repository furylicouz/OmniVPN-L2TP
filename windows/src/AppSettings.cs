using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace L2TPConnect
{
    // Одно VPN-подключение пользователя.
    internal sealed class Connection
    {
        public string Name = "";
        public string Server = "";
        public string Psk = "";
        public string Routes = "";
        public string Group = "";
        public string Balance = "";   // "latency" — в группе подключать к каналу с наименьшей задержкой
        public string Domain = "";    // домен для RDP-компьютеров, которым нужен этот VPN (например CORP)
        public string Dns = "";       // DNS-серверы VPN через запятую (не больше двух), например 192.168.10.11, 192.168.10.12
        public string DnsDomain = ""; // DNS-домен (суффикс), например corp.local
        public string User = "";
        public string Password = "";
        public bool SavePassword;
    }

    // Список подключений хранится зашифрованным DPAPI (расшифровать может только этот пользователь Windows).
    // Пароль пишется на диск только при включённой галке «Сохранить пароль».
    internal sealed class AppSettings
    {
        public readonly List<Connection> Connections = new List<Connection>();
        public readonly List<RdpHost> Rdp = new List<RdpHost>();
        public string Selected = "";

        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("L2TPConnect.settings.v1");

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniVPNConnect"); }
        }

        // Папка настроек до переименования программы.
        static string LegacyFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "L2TPConnect"); }
        }

        static string FilePath
        {
            get { return Path.Combine(Folder, "settings.dat"); }
        }

        public Connection Find(string name)
        {
            return Connections.Find(c => string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase));
        }

        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                if (!Directory.Exists(Folder) && Directory.Exists(LegacyFolder))
                    Directory.Move(LegacyFolder, Folder);
                if (!File.Exists(FilePath))
                    return s;
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
                var values = new Dictionary<string, string>();
                foreach (string line in Encoding.UTF8.GetString(plain).Split('\n'))
                {
                    int i = line.IndexOf('=');
                    if (i > 0)
                        values[line.Substring(0, i)] = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(i + 1).Trim()));
                }

                if (values.ContainsKey("server"))
                {
                    // Формат версии 1.0: одно подключение без имени.
                    var c = new Connection();
                    c.Server = Get(values, "server");
                    c.Name = c.Server;
                    c.Psk = Get(values, "psk");
                    c.User = Get(values, "user");
                    c.Password = Get(values, "password");
                    c.Routes = Get(values, "routes");
                    c.SavePassword = c.Password.Length > 0;
                    if (c.Server.Length > 0)
                    {
                        s.Connections.Add(c);
                        s.Selected = c.Name;
                    }
                    return s;
                }

                s.Selected = Get(values, "selected");
                for (int n = 0; values.ContainsKey("c" + n + ".name"); n++)
                {
                    string p = "c" + n + ".";
                    var c = new Connection();
                    c.Name = Get(values, p + "name");
                    c.Server = Get(values, p + "server");
                    c.Psk = Get(values, p + "psk");
                    c.Routes = Get(values, p + "routes");
                    c.Group = Get(values, p + "group");
                    c.Balance = Get(values, p + "balance");
                    c.Domain = Get(values, p + "domain");
                    c.Dns = Get(values, p + "dns");
                    c.DnsDomain = Get(values, p + "dnsdomain");
                    c.User = Get(values, p + "user");
                    c.Password = Get(values, p + "password");
                    c.SavePassword = Get(values, p + "savepassword") == "1";
                    s.Connections.Add(c);
                }
                for (int n = 0; values.ContainsKey("r" + n + ".name"); n++)
                {
                    string p = "r" + n + ".";
                    var h = new RdpHost();
                    h.Name = Get(values, p + "name");
                    h.Host = Get(values, p + "host");
                    h.User = Get(values, p + "user");
                    h.Vpn = Get(values, p + "vpn");
                    h.Collection = Get(values, p + "collection");
                    h.Gateway = Get(values, p + "gateway");
                    h.Domain = Get(values, p + "domain");
                    h.FullScreen = Get(values, p + "fullscreen") != "0";
                    h.MultiMon = Get(values, p + "multimon") == "1";
                    s.Rdp.Add(h);
                }
            }
            catch
            {
            }
            return s;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            Append(sb, "selected", Selected);
            for (int n = 0; n < Connections.Count; n++)
            {
                var c = Connections[n];
                string p = "c" + n + ".";
                Append(sb, p + "name", c.Name);
                Append(sb, p + "server", c.Server);
                Append(sb, p + "psk", c.Psk);
                Append(sb, p + "routes", c.Routes);
                Append(sb, p + "group", c.Group);
                Append(sb, p + "balance", c.Balance);
                Append(sb, p + "domain", c.Domain);
                Append(sb, p + "dns", c.Dns);
                Append(sb, p + "dnsdomain", c.DnsDomain);
                Append(sb, p + "user", c.User);
                Append(sb, p + "password", c.SavePassword ? c.Password : "");
                Append(sb, p + "savepassword", c.SavePassword ? "1" : "0");
            }

            for (int n = 0; n < Rdp.Count; n++)
            {
                var h = Rdp[n];
                string p = "r" + n + ".";
                Append(sb, p + "name", h.Name);
                Append(sb, p + "host", h.Host);
                Append(sb, p + "user", h.User);
                Append(sb, p + "vpn", h.Vpn);
                Append(sb, p + "collection", h.Collection);
                Append(sb, p + "gateway", h.Gateway);
                Append(sb, p + "domain", h.Domain);
                Append(sb, p + "fullscreen", h.FullScreen ? "1" : "0");
                Append(sb, p + "multimon", h.MultiMon ? "1" : "0");
            }

            Directory.CreateDirectory(Folder);
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(sb.ToString()), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, data);
        }

        static string Get(Dictionary<string, string> values, string key)
        {
            string v;
            return values.TryGetValue(key, out v) ? v : "";
        }

        static void Append(StringBuilder sb, string key, string value)
        {
            sb.Append(key).Append('=').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""))).Append('\n');
        }
    }
}
