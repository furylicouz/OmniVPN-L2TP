using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace L2TPConnect
{
    // Состояние интернета на этом компьютере и задержка до VPN-сервера (для индикатора в боковой панели).
    internal static class NetCheck
    {
        // Несколько независимых узлов: интернет есть, если открылось TCP-соединение хотя бы с одним.
        static readonly string[] Hosts = { "77.88.8.8", "1.1.1.1", "8.8.8.8" };
        static readonly int[] Ports = { 53, 443, 443 };

        public static async Task<ProbeResult> InternetAsync()
        {
            var result = new ProbeResult { State = Reach.Down, When = DateTime.Now };
            var sw = Stopwatch.StartNew();
            var clients = new List<TcpClient>();
            var pending = new List<Task>();
            for (int i = 0; i < Hosts.Length; i++)
            {
                var c = new TcpClient(AddressFamily.InterNetwork);
                clients.Add(c);
                try { pending.Add(c.ConnectAsync(IPAddress.Parse(Hosts[i]), Ports[i])); }
                catch (Exception) { }
            }
            var deadline = Task.Delay(2500);
            while (pending.Count > 0)
            {
                var wait = new List<Task>(pending) { deadline };
                var done = await Task.WhenAny(wait);
                if (done == deadline)
                    break;
                pending.Remove(done);
                if (!done.IsFaulted && !done.IsCanceled)
                {
                    result.State = Reach.Up;
                    result.RttMs = (int)sw.ElapsedMilliseconds;
                    break;
                }
            }
            foreach (var t in pending)
            {
                var observed = t.ContinueWith(x => { var ignored = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }
            foreach (var c in clients)
                c.Close();
            result.When = DateTime.Now;
            return result;
        }

        public static async Task<bool> DnsAsync()
        {
            try
            {
                var lookup = Dns.GetHostAddressesAsync("ya.ru");
                var done = await Task.WhenAny(lookup, Task.Delay(2500));
                if (done != lookup)
                {
                    var observed = lookup.ContinueWith(x => { var ignored = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    return false;
                }
                return !lookup.IsFaulted && lookup.Result.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static async Task<ProbeResult> PingAsync(string host)
        {
            var result = new ProbeResult { State = Reach.Down, When = DateTime.Now };
            try
            {
                using (var ping = new Ping())
                {
                    var reply = await ping.SendPingAsync(host, 1500);
                    if (reply.Status == IPStatus.Success)
                    {
                        result.State = Reach.Up;
                        result.RttMs = (int)reply.RoundtripTime;
                    }
                }
            }
            catch (PingException ex)
            {
                // Имя не разрешилось.
                result.NameNotFound = ex.InnerException is SocketException;
            }
            catch (Exception)
            {
            }
            result.When = DateTime.Now;
            return result;
        }
    }
}
