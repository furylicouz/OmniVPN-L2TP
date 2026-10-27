using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace L2TPConnect
{
    internal enum Reach { Unknown, Checking, Up, Down }

    internal sealed class ProbeResult
    {
        public Reach State;
        public int RttMs;
        public bool NameNotFound;
        public DateTime When;
    }

    // Проверка доступности L2TP/IPsec-сервера: первый пакет IKEv1 Main Mode на UDP 500.
    // Сервер отвечает своим предложением SA, значит, цепочка до него (включая проброс портов) жива.
    // Логин и пароль при этом не проверяются; на сервере остаётся незавершённая попытка IKE.
    internal static class IkeProbe
    {
        const int TimeoutMs = 2000;
        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        public static async Task<ProbeResult> CheckAsync(string host)
        {
            var result = new ProbeResult { State = Reach.Down, When = DateTime.Now };
            IPAddress ip = await Resolve(host);
            if (ip == null)
            {
                result.NameNotFound = true;
                return result;
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                var cookie = new byte[8];
                lock (Rng) Rng.GetBytes(cookie);
                byte[] packet = BuildMainMode(cookie);
                using (var udp = new UdpClient(AddressFamily.InterNetwork))
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        await udp.SendAsync(packet, packet.Length, new IPEndPoint(ip, 500));
                        while (sw.ElapsedMilliseconds < TimeoutMs)
                        {
                            var receive = udp.ReceiveAsync();
                            var done = await Task.WhenAny(receive, Task.Delay(Math.Max(1, TimeoutMs - (int)sw.ElapsedMilliseconds)));
                            if (done != receive)
                            {
                                // Сокет закроется в using; ошибку незавершённого приёма гасим.
                                var observed = receive.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                                break;
                            }
                            var reply = receive.Result;
                            if (reply.RemoteEndPoint.Address.Equals(ip) && reply.Buffer.Length >= 28 && SameCookie(reply.Buffer, cookie))
                            {
                                result.State = Reach.Up;
                                result.RttMs = (int)sw.ElapsedMilliseconds;
                                result.When = DateTime.Now;
                                return result;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // ICMP «порт недоступен» приходит как SocketException — сервер не слушает IKE.
                    }
                }
            }
            result.When = DateTime.Now;
            return result;
        }

        static async Task<IPAddress> Resolve(string host)
        {
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip))
                return ip;
            try
            {
                foreach (var a in await Dns.GetHostAddressesAsync(host))
                    if (a.AddressFamily == AddressFamily.InterNetwork)
                        return a;
            }
            catch (Exception)
            {
            }
            return null;
        }

        static bool SameCookie(byte[] buffer, byte[] cookie)
        {
            for (int i = 0; i < 8; i++)
                if (buffer[i] != cookie[i])
                    return false;
            return true;
        }

        // ISAKMP: заголовок + SA (DOI IPsec) + одно предложение с наборами, которые принимают
        // MikroTik и Windows по умолчанию: AES-128/256 и 3DES, SHA1, MODP2048/1024, PSK.
        static byte[] BuildMainMode(byte[] cookie)
        {
            int[,] specs = { { 7, 128, 14 }, { 7, 256, 14 }, { 7, 128, 2 }, { 5, 0, 2 }, { 5, 0, 14 } };
            int count = specs.GetLength(0);
            var transforms = new List<byte>();
            for (int i = 0; i < count; i++)
                transforms.AddRange(Transform(i + 1, specs[i, 0], specs[i, 1], specs[i, 2], i == count - 1));

            var proposal = new List<byte> { 0, 0, 0, 0, 1, 1, 0, (byte)count };
            proposal.AddRange(transforms);
            SetLength16(proposal, 2);

            var sa = new List<byte> { 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1 };
            sa.AddRange(proposal);
            SetLength16(sa, 2);

            var packet = new List<byte>(cookie);
            packet.AddRange(new byte[8]);
            packet.AddRange(new byte[] { 1, 0x10, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            packet.AddRange(sa);
            int len = packet.Count;
            packet[24] = (byte)(len >> 24);
            packet[25] = (byte)(len >> 16);
            packet[26] = (byte)(len >> 8);
            packet[27] = (byte)len;
            return packet.ToArray();
        }

        static List<byte> Transform(int number, int encryption, int keyLength, int group, bool last)
        {
            var t = new List<byte> { (byte)(last ? 0 : 3), 0, 0, 0, (byte)number, 1, 0, 0 };
            Attr(t, 1, encryption);
            if (keyLength > 0)
                Attr(t, 14, keyLength);
            Attr(t, 2, 2);      // SHA1
            Attr(t, 3, 1);      // общий ключ
            Attr(t, 4, group);  // группа DH
            Attr(t, 11, 1);     // время жизни в секундах
            Attr(t, 12, 28800);
            SetLength16(t, 2);
            return t;
        }

        static void Attr(List<byte> to, int type, int value)
        {
            to.Add((byte)(0x80 | (type >> 8)));
            to.Add((byte)type);
            to.Add((byte)(value >> 8));
            to.Add((byte)value);
        }

        static void SetLength16(List<byte> payload, int offset)
        {
            payload[offset] = (byte)(payload.Count >> 8);
            payload[offset + 1] = (byte)payload.Count;
        }
    }
}
