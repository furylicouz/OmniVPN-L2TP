using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace L2TPConnect
{
    // Профиль VPN в Windows создаётся штатными командлетами (Add-VpnConnection).
    // Секреты передаются через переменные окружения процесса, а не через командную строку.
    internal static class VpnProfile
    {
        public const string EntryName = "OmniVPN Connect";
        // Имя профиля до переименования программы: удаляется при следующей настройке.
        const string LegacyEntryName = "L2TP Connect";

        // -SplitTunneling = снятая галка «Использовать основной шлюз в удалённой сети» (IPv4 и IPv6).
        const string ApplyScript = @"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
try {
  $n = $env:L2TPC_NAME
  if (Get-VpnConnection -Name $n -ErrorAction SilentlyContinue) { Remove-VpnConnection -Name $n -Force }
  if ($env:L2TPC_LEGACY) { Remove-VpnConnection -Name $env:L2TPC_LEGACY -Force -ErrorAction SilentlyContinue }
  Add-VpnConnection -Name $n -ServerAddress $env:L2TPC_SERVER -TunnelType L2tp -L2tpPsk $env:L2TPC_PSK `
    -AuthenticationMethod MSChapv2,Chap -EncryptionLevel Optional -SplitTunneling -Force -WarningAction SilentlyContinue
  if ($env:L2TPC_DNSSUFFIX) { Set-VpnConnection -Name $n -DnsSuffix $env:L2TPC_DNSSUFFIX -WarningAction SilentlyContinue }
  foreach ($r in ($env:L2TPC_ROUTES -split ';')) {
    if ($r) { Add-VpnConnectionRoute -ConnectionName $n -DestinationPrefix $r -WarningAction SilentlyContinue | Out-Null }
  }
  'OK'
} catch { 'ERR:' + $_.Exception.Message; exit 1 }
";

        const string RemoveScript = @"
$ProgressPreference = 'SilentlyContinue'
Remove-VpnConnection -Name $env:L2TPC_NAME -Force -ErrorAction SilentlyContinue
Remove-VpnConnection -Name $env:L2TPC_LEGACY -Force -ErrorAction SilentlyContinue
";

        public static void Apply(string server, string psk, IList<string> routes, IList<string> dnsServers, string dnsSuffix)
        {
            var env = new Dictionary<string, string>();
            env["L2TPC_DNSSUFFIX"] = dnsSuffix ?? "";
            env["L2TPC_NAME"] = EntryName;
            env["L2TPC_LEGACY"] = LegacyEntryName;
            env["L2TPC_SERVER"] = server;
            env["L2TPC_PSK"] = psk;
            env["L2TPC_ROUTES"] = string.Join(";", routes);

            int exitCode;
            string output = RunPowerShell(ApplyScript, env, 60000, out exitCode);
            if (exitCode == 0 && output.Contains("OK"))
            {
                SetDnsServers(dnsServers);
                return;
            }

            string message = output;
            int i = output.IndexOf("ERR:", StringComparison.Ordinal);
            if (i >= 0)
                message = output.Substring(i + 4);
            message = message.Trim();
            if (message.Length == 0)
                message = "PowerShell завершился с кодом " + exitCode;
            throw new Exception(message);
        }

        public static void Remove(bool wait)
        {
            var env = new Dictionary<string, string>();
            env["L2TPC_NAME"] = EntryName;
            env["L2TPC_LEGACY"] = LegacyEntryName;
            try
            {
                if (wait)
                {
                    int exitCode;
                    RunPowerShell(RemoveScript, env, 30000, out exitCode);
                }
                else
                {
                    Process.Start(CreateStartInfo(RemoveScript, env, false)).Dispose();
                }
            }
            catch
            {
            }
        }

        // DNS-серверы VPN-интерфейса: в PowerShell для L2TP нет параметра, поэтому правим строки профиля в rasphone.pbk
        // (IpNameAssign=2 — серверы заданы вручную, 1 — выдаёт VPN-сервер). Windows хранит не больше двух.
        static void SetDnsServers(IList<string> servers)
        {
            string path = Ras.UserPhonebook;
            if (!File.Exists(path))
                return;
            byte[] raw = File.ReadAllBytes(path);
            // Latin-1 сохраняет байты как есть, чтобы не испортить чужие подключения с кириллицей; UTF-16 — по BOM.
            Encoding enc = raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE ? Encoding.Unicode : Encoding.GetEncoding(28591);
            string text = enc.GetString(raw);
            bool bom = enc == Encoding.Unicode;
            if (bom)
                text = text.TrimStart('\uFEFF');

            string first = servers != null && servers.Count > 0 ? servers[0] : "0.0.0.0";
            string second = servers != null && servers.Count > 1 ? servers[1] : "0.0.0.0";
            string assign = servers != null && servers.Count > 0 ? "2" : "1";
            var lines = text.Replace("\r\n", "\n").Split('\n');
            bool inSection = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.StartsWith("["))
                {
                    inSection = l == "[" + EntryName + "]";
                    continue;
                }
                if (!inSection)
                    continue;
                if (l.StartsWith("IpNameAssign=")) lines[i] = "IpNameAssign=" + assign;
                else if (l.StartsWith("IpDnsAddress=")) lines[i] = "IpDnsAddress=" + first;
                else if (l.StartsWith("IpDns2Address=")) lines[i] = "IpDns2Address=" + second;
            }
            string result = string.Join("\r\n", lines);
            byte[] bytes = enc.GetBytes(result);
            if (bom)
            {
                var withBom = new byte[bytes.Length + 2];
                withBom[0] = 0xFF;
                withBom[1] = 0xFE;
                Buffer.BlockCopy(bytes, 0, withBom, 2, bytes.Length);
                bytes = withBom;
            }
            File.WriteAllBytes(path, bytes);
        }

        static ProcessStartInfo CreateStartInfo(string script, IDictionary<string, string> env, bool redirect)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            if (redirect)
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
            }
            foreach (var kv in env)
                psi.EnvironmentVariables[kv.Key] = kv.Value;
            return psi;
        }

        static string RunPowerShell(string script, IDictionary<string, string> env, int timeoutMs, out int exitCode)
        {
            using (var p = Process.Start(CreateStartInfo(script, env, true)))
            {
                var stderr = p.StandardError.ReadToEndAsync();
                var stdout = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException("PowerShell не ответил вовремя.");
                }
                p.WaitForExit();
                exitCode = p.ExitCode;
                return stdout.Result + stderr.Result;
            }
        }
    }

    internal static class RouteList
    {
        // «192.168.1.0/24, 10.0.0.5» -> ["192.168.1.0/24", "10.0.0.5/32"].
        public static bool TryParse(string text, out List<string> result, out string bad)
        {
            result = new List<string>();
            bad = null;
            if (string.IsNullOrWhiteSpace(text))
                return true;

            foreach (string raw in text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string ipPart = raw;
                int prefix = 32;
                int slash = raw.IndexOf('/');
                if (slash >= 0)
                {
                    ipPart = raw.Substring(0, slash);
                    if (!int.TryParse(raw.Substring(slash + 1), out prefix) || prefix < 1 || prefix > 32)
                    {
                        bad = raw;
                        return false;
                    }
                }

                IPAddress ip;
                if (ipPart.Split('.').Length != 4 || !IPAddress.TryParse(ipPart, out ip) ||
                    ip.AddressFamily != AddressFamily.InterNetwork)
                {
                    bad = raw;
                    return false;
                }

                byte[] b = ip.GetAddressBytes();
                uint v = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
                v &= 0xFFFFFFFFu << (32 - prefix);
                string norm = string.Format("{0}.{1}.{2}.{3}/{4}", v >> 24, (v >> 16) & 255, (v >> 8) & 255, v & 255, prefix);
                if (!result.Contains(norm))
                    result.Add(norm);
            }
            return true;
        }
    }
}
