using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace L2TPConnect
{
    // Содержимое файла .omnivpn: VPN-подключения и компьютеры для удалённого рабочего стола.
    internal sealed class ProfileBundle
    {
        public readonly List<Connection> Vpn = new List<Connection>();
        public readonly List<RdpHost> Rdp = new List<RdpHost>();
    }

    // Файл профилей .omnivpn: текст в духе INI, по секции [Название] на подключение или компьютер.
    //   [Офис]
    //   server = vpn.example.ru
    //   psk = ключ
    //   routes = 192.168.88.0/24, 10.0.0.0/16   (необязательно)
    //   group = Офис                            (необязательно: каналы одного офиса, порядок в файле = приоритет)
    //   balance = задержка                      (необязательно: в группе подключать туда, где меньше задержка)
    //   domain = CORP                           (необязательно: домен для RDP-компьютеров этого VPN)
    //   dns = 192.168.10.11, 192.168.10.12      (необязательно: DNS-серверы VPN, не больше двух)
    //   dnsdomain = corp.local                  (необязательно: DNS-домен, имена в нём ищутся через DNS VPN)
    //   user = user                             (необязательно)
    //   password = ...                          (необязательно, при экспорте не пишется)
    //
    //   [Бухгалтерия]
    //   type = rdp
    //   host = 192.168.20.25                    (можно с портом: 192.168.20.25:3390)
    //   user = CORP\user                        (необязательно)
    //   vpn = Офис                              (необязательно: группа или название VPN, без которого ПК недоступен)
    //   domain = CORP                           (необязательно: домен для логина, иначе — домен VPN)
    //   collection = Office                     (необязательно: alias коллекции RDS, тогда host — брокер)
    //   gateway = gw.example.ru                 (необязательно: шлюз RD Gateway)
    //   fullscreen = да | нет                   (по умолчанию да)
    //   multimon = да | нет                     (по умолчанию нет)
    internal static class ProfileFile
    {
        public const string Extension = ".omnivpn";
        public const string Filter = "Профили OmniVPN Connect (*.omnivpn)|*.omnivpn|Все файлы (*.*)|*.*";
        const int MaxSize = 256 * 1024;

        sealed class Section
        {
            public string Name = "";
            public bool Named;
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        }

        public static ProfileBundle Read(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new Exception("Файл не найден: " + path);
            if (info.Length > MaxSize)
                throw new Exception("Файл слишком большой для файла профилей.");
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        public static ProfileBundle Parse(string text)
        {
            var sections = new List<Section>();
            Section current = null;
            var lines = text.TrimStart('﻿').Replace("\r\n", "\n").Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                    continue;

                if (line[0] == '[')
                {
                    int end = line.LastIndexOf(']');
                    string name = end > 0 ? line.Substring(1, end - 1).Trim() : "";
                    if (name.Length == 0)
                        throw LineError(i, "пустое название в [ ]");
                    current = new Section { Name = name, Named = true };
                    sections.Add(current);
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0)
                    throw LineError(i, "ожидалось «параметр = значение»");
                if (current == null)
                {
                    // Параметры без секции — одно подключение, название из name или адреса.
                    current = new Section();
                    sections.Add(current);
                }
                current.Values[line.Substring(0, eq).Trim().ToLowerInvariant()] = Unquote(line.Substring(eq + 1).Trim());
            }

            var bundle = new ProfileBundle();
            var vpnNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            var rdpNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var s in sections)
            {
                if (!s.Named)
                    s.Name = Get(s, "name");
                if (Get(s, "type").Equals("rdp", StringComparison.OrdinalIgnoreCase))
                {
                    var h = ToRdp(s);
                    if (!rdpNames.Add(h.Name))
                        throw new Exception("Компьютер «" + h.Name + "»: встречается в файле дважды.");
                    bundle.Rdp.Add(h);
                }
                else
                {
                    var c = ToVpn(s);
                    if (!vpnNames.Add(c.Name))
                        throw new Exception("Подключение «" + c.Name + "»: встречается в файле дважды.");
                    bundle.Vpn.Add(c);
                }
            }
            if (bundle.Vpn.Count == 0 && bundle.Rdp.Count == 0)
                throw new Exception("В файле нет ни одного подключения.");
            return bundle;
        }

        static Connection ToVpn(Section s)
        {
            var c = new Connection();
            c.Server = Get(s, "server", "gateway", "host");
            c.Name = s.Name.Length > 0 ? s.Name : c.Server;
            c.Psk = Get(s, "psk", "secret", "sharedsecret");
            c.Routes = Get(s, "routes", "networks");
            c.Group = Get(s, "group");
            c.Balance = Balance(Get(s, "balance", "mode"));
            c.Domain = Get(s, "domain");
            c.Dns = Get(s, "dns", "dnsservers");
            c.DnsDomain = Get(s, "dnsdomain", "dnssuffix", "searchdomain");
            c.User = Get(s, "user", "login", "username");
            c.Password = Get(s, "password");
            c.SavePassword = c.Password.Length > 0;

            string where = "Подключение «" + c.Name + "»: ";
            if (c.Server.Length == 0)
                throw new Exception(where + "не указан server.");
            if (c.Server.IndexOfAny(" \t'\"`$;|&<>".ToCharArray()) >= 0)
                throw new Exception(where + "недопустимые символы в адресе сервера.");
            if (c.Psk.Length == 0)
                throw new Exception(where + "не указан psk (общий ключ).");
            List<string> routes;
            string bad;
            if (!RouteList.TryParse(c.Routes, out routes, out bad))
                throw new Exception(where + "неверная сеть «" + bad + "».");
            c.Routes = string.Join(", ", routes);
            string domainError = CheckDomain(c.Domain);
            if (domainError != null)
                throw new Exception(where + domainError);
            List<string> dns;
            string dnsError = ParseDns(c.Dns, out dns) ?? CheckDnsDomain(c.DnsDomain);
            if (dnsError != null)
                throw new Exception(where + dnsError);
            c.Dns = string.Join(", ", dns);
            return c;
        }

        static RdpHost ToRdp(Section s)
        {
            var h = new RdpHost();
            h.Host = Get(s, "host", "address", "server");
            h.Name = s.Name.Length > 0 ? s.Name : h.Host;
            h.User = Get(s, "user", "login", "username");
            h.Vpn = Get(s, "vpn");
            h.Collection = Get(s, "collection");
            h.Gateway = Get(s, "gateway");
            h.Domain = Get(s, "domain");
            h.FullScreen = Flag(Get(s, "fullscreen"), true);
            h.MultiMon = Flag(Get(s, "multimon"), false);
            string error = RdpHost.CheckAddress(h.Host)
                           ?? (h.Gateway.Length > 0 ? RdpHost.CheckAddress(h.Gateway) : null)
                           ?? RdpHost.CheckCollection(h.Collection)
                           ?? CheckDomain(h.Domain);
            if (error != null)
                throw new Exception("Компьютер «" + h.Name + "»: " + error);
            return h;
        }

        public static string Write(IEnumerable<Connection> connections, IEnumerable<RdpHost> computers)
        {
            var sb = new StringBuilder();
            sb.Append("# OmniVPN Connect — профили VPN-подключений (L2TP/IPsec) и компьютеров (RDP).\r\n");
            sb.Append("# Откройте файл двойным щелчком, чтобы добавить их в программу.\r\n");
            sb.Append("# Внимание: файл содержит общий ключ (PSK), передавайте его только тем, кому он нужен.\r\n");
            foreach (var c in connections)
            {
                sb.Append("\r\n[").Append(c.Name).Append("]\r\n");
                Line(sb, "server", c.Server);
                Line(sb, "psk", c.Psk);
                if (c.Routes.Length > 0) Line(sb, "routes", c.Routes);
                if (c.Group.Length > 0) Line(sb, "group", c.Group);
                if (c.Balance == "latency") Line(sb, "balance", "задержка");
                if (c.Domain.Length > 0) Line(sb, "domain", c.Domain);
                if (c.Dns.Length > 0) Line(sb, "dns", c.Dns);
                if (c.DnsDomain.Length > 0) Line(sb, "dnsdomain", c.DnsDomain);
                if (c.User.Length > 0) Line(sb, "user", c.User);
            }
            if (computers != null)
            {
                foreach (var h in computers)
                {
                    sb.Append("\r\n[").Append(h.Name).Append("]\r\n");
                    Line(sb, "type", "rdp");
                    Line(sb, "host", h.Host);
                    if (h.User.Length > 0) Line(sb, "user", h.User);
                    if (h.Domain.Length > 0) Line(sb, "domain", h.Domain);
                    if (h.Vpn.Length > 0) Line(sb, "vpn", h.Vpn);
                    if (h.Collection.Length > 0) Line(sb, "collection", h.Collection);
                    if (h.Gateway.Length > 0) Line(sb, "gateway", h.Gateway);
                    Line(sb, "fullscreen", h.FullScreen ? "да" : "нет");
                    if (h.MultiMon) Line(sb, "multimon", "да");
                }
            }
            return sb.ToString();
        }

        static string Get(Section s, params string[] keys)
        {
            string v;
            foreach (var k in keys)
                if (s.Values.TryGetValue(k, out v))
                    return v;
            return "";
        }

        // «задержка», «latency», «ping» — балансировка по задержке; всё остальное — по порядку.
        static string Balance(string v)
        {
            switch (v.Trim().ToLowerInvariant())
            {
                case "задержка": case "latency": case "ping": case "fastest": case "быстрый": return "latency";
                default: return "";
            }
        }

        // null — домен в порядке. Домен — короткое имя (CORP) или DNS-имя (corp.local), без «\» и пробелов.
        public static string CheckDomain(string domain)
        {
            if (domain.Length == 0)
                return null;
            foreach (char ch in domain)
                if (!(char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_'))
                    return "домен указывается без «\\» и пробелов, например CORP.";
            return null;
        }

        // DNS-серверы: IPv4 через запятую или пробел, не больше двух (столько хранит Windows). null — без ошибок.
        public static string ParseDns(string text, out List<string> servers)
        {
            servers = new List<string>();
            foreach (string raw in text.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                System.Net.IPAddress ip;
                if (raw.Split('.').Length != 4 || !System.Net.IPAddress.TryParse(raw, out ip) ||
                    ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    return "неверный DNS-сервер «" + raw + "» (нужен IPv4-адрес, например 192.168.10.11).";
                if (!servers.Contains(raw))
                    servers.Add(raw);
            }
            if (servers.Count > 2)
                return "DNS-серверов можно указать не больше двух.";
            return null;
        }

        // DNS-домен: corp.local — буквы, цифры, точки и дефисы.
        public static string CheckDnsDomain(string domain)
        {
            if (domain.Length == 0)
                return null;
            foreach (char ch in domain)
                if (!(char.IsLetterOrDigit(ch) || ch == '.' || ch == '-'))
                    return "DNS-домен указывается как corp.local, без пробелов и «\\».";
            return null;
        }

        static bool Flag(string v, bool byDefault)
        {
            switch (v.Trim().ToLowerInvariant())
            {
                case "да": case "yes": case "true": case "1": case "on": return true;
                case "нет": case "no": case "false": case "0": case "off": return false;
                default: return byDefault;
            }
        }

        static void Line(StringBuilder sb, string key, string value)
        {
            sb.Append(key).Append(" = ").Append(Quote(value)).Append("\r\n");
        }

        // Кавычки нужны, только если значение начинается или кончается пробелом либо само начинается с кавычки.
        static string Quote(string v)
        {
            if (v.Length > 0 && (char.IsWhiteSpace(v[0]) || char.IsWhiteSpace(v[v.Length - 1]) || v[0] == '"'))
                return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            return v;
        }

        static string Unquote(string v)
        {
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
                return v.Substring(1, v.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
            return v;
        }

        static Exception LineError(int index, string message)
        {
            return new Exception("Строка " + (index + 1) + ": " + message + ".");
        }
    }
}
