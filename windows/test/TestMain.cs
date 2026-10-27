using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace L2TPConnect
{
    // Тестовый стенд: не входит в поставку.
    internal static class TestMain
    {
        [STAThread]
        static int Main(string[] a)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            switch (a[0])
            {
                case "units": return Units();
                case "dial": return DialTest(a[1]);
                case "shot": return Shots(a[1]);
                case "profiles": return Profiles(a[1]);
                case "creds": return Creds();
                case "balance": return Balance();
                case "dnsapply":
                {
                    // Профиль OmniVPN Connect этой учётки: DNS задаём, потом убираем, затем удаляем профиль.
                    Action<string> show = title =>
                    {
                        Console.WriteLine("  == " + title);
                        bool inSec = false;
                        foreach (var l in File.ReadAllLines(Ras.UserPhonebook))
                        {
                            if (l.StartsWith("[")) { inSec = l == "[" + VpnProfile.EntryName + "]"; continue; }
                            if (inSec && (l.StartsWith("IpNameAssign=") || l.StartsWith("IpDnsAddress=") || l.StartsWith("IpDns2Address=") || l.StartsWith("IpDnsSuffix=")))
                                Console.WriteLine("     " + l);
                        }
                    };
                    VpnProfile.Apply("192.0.2.1", "x", new List<string>(), new List<string> { "192.168.10.11", "192.168.10.12" }, "corp.local");
                    show("DNS 192.168.10.11, 192.168.10.12 и домен corp.local");
                    VpnProfile.Apply("192.0.2.1", "x", new List<string>(), new List<string>(), "");
                    show("без DNS (выдаёт VPN-сервер)");
                    VpnProfile.Remove(true);
                    Console.WriteLine("  профиль удалён: " + !(File.Exists(Ras.UserPhonebook) && File.ReadAllText(Ras.UserPhonebook).Contains("[" + VpnProfile.EntryName + "]")));
                    return 0;
                }
                case "net":
                {
                    var inet = NetCheck.InternetAsync().Result;
                    Console.WriteLine("  интернет: " + inet.State + " " + inet.RttMs + " мс, DNS: " + NetCheck.DnsAsync().Result);
                    foreach (var h in a.Skip(1))
                    {
                        var p = NetCheck.PingAsync(h).Result;
                        Console.WriteLine(string.Format("  ping {0,-22} {1} {2} мс{3}", h, p.State, p.RttMs, p.NameNotFound ? " (имя не найдено)" : ""));
                    }
                    return 0;
                }
                case "rdpfile":
                {
                    foreach (var u in new[] { "user", "BRANCH\\user", "user@corp.local", "" })
                        Console.WriteLine("  логин «" + u + "» + домен CORP -> «" + RdpLauncher.FullUser(u, "CORP") + "»");
                    var path = RdpLauncher.WriteFile(new RdpHost { Name = "Бухгалтерия", Host = "broker.corp.local", User = "user", Collection = "Office", Gateway = "gw.example.ru", FullScreen = false, MultiMon = true }, "CORP");
                    Console.WriteLine("  " + path);
                    Console.Write(File.ReadAllText(path));
                    File.Delete(path);
                    // Домен не должен попасть в файл дважды (логин с доменом + domain:s:).
                    foreach (var u in new[] { "user", "CORP\\user", "user@corp.local", "" })
                    {
                        var f = RdpLauncher.WriteFile(new RdpHost { Name = "domain-test", Host = "rds.corp.local", User = u }, "CORP");
                        var lines = File.ReadAllLines(f).Where(l => l.StartsWith("username:") || l.StartsWith("domain:")).ToArray();
                        File.Delete(f);
                        Console.WriteLine("  логин «" + u + "»: " + string.Join(" | ", lines));
                    }
                    return 0;
                }
                case "update":
                {
                    // update [текущая версия] [скачать]: проверка релиза на GitHub и загрузка установщика.
                    string current = a.Length > 1 ? a[1] : Program.Version;
                    var u = Updater.CheckAsync().Result;
                    Console.WriteLine("  релиз " + u.Version + ": " + u.File + ", " + u.Size + " байт, sha256 " + u.Sha256.Substring(0, 16));
                    Console.WriteLine("  заметки: " + u.Notes.Replace("\n", " | "));
                    bool newer = Updater.IsNewer(u.Version, current);
                    Console.WriteLine("  новее " + current + ": " + newer);
                    if (a.Length > 2 && newer)
                    {
                        int last = -1;
                        string f = Updater.DownloadAsync(u, (d, t) => { int p = (int)(d * 100 / t); if (p / 25 != last) { last = p / 25; Console.WriteLine("    " + p + "%"); } }, CancellationToken.None).Result;
                        Console.WriteLine("  скачан и проверен: " + f + " (" + new FileInfo(f).Length + " байт)");
                        File.Delete(f);
                    }
                    return 0;
                }
                case "rdpprobe":
                    foreach (var h in a.Skip(1))
                    {
                        var r = RdpLauncher.CheckAsync(new RdpHost { Name = h, Host = h }).Result;
                        Console.WriteLine(string.Format("  {0,-22} {1} {2} мс{3}", h, r.State, r.RttMs, r.NameNotFound ? " (имя не найдено)" : ""));
                    }
                    return 0;
                case "probe":
                    foreach (var h in a.Skip(1))
                    {
                        var r = IkeProbe.CheckAsync(h).Result;
                        Console.WriteLine(string.Format("  {0,-22} {1} {2} мс", h, r.State, r.RttMs));
                    }
                    return 0;
            }
            return 2;
        }

        static int Units()
        {
            List<string> r; string bad;
            Console.WriteLine(RouteList.TryParse("192.168.88.17/24, 10.10.0.0/16;172.16.5.5", out r, out bad) + " " + string.Join(" ", r));
            Console.WriteLine(RouteList.TryParse("192.168.1.0/33", out r, out bad) + " bad=" + bad);
            Console.WriteLine(RouteList.TryParse("abc", out r, out bad) + " bad=" + bad);
            Console.WriteLine(Fmt.Bytes(0) + " | " + Fmt.Bytes(1536) + " | " + Fmt.Bytes(5.5 * 1024 * 1024 * 1024) + " | " + Fmt.Duration(90061000));

            // Описание релиза и сравнение версий.
            var u = Updater.Parse("# релиз\nversion = v1.10.0\nnotes = Первое\nnotes = Второе\nwindows = OmniVPNConnect-Setup-1.10.0.exe\n" +
                "windows.size = 190587\nwindows.sha256 = " + new string('A', 64) + "\nmac = OmniVPNConnect-1.10.0.dmg\n", "windows");
            Console.WriteLine("update " + u.Version + " " + u.File + " " + u.Size + " " + u.Sha256.Substring(0, 4) + " notes=" + u.Notes.Replace("\n", "|") + " " + u.Url);
            Console.WriteLine("newer: " + Updater.IsNewer("1.10.0", "1.9.0") + " " + Updater.IsNewer("1.9", "1.9.0") + " " + Updater.IsNewer("1.9.1", "1.9.0") + " " + Updater.IsNewer("1.8.9", "1.9.0") + " " + Updater.IsNewer("мусор", "1.9.0"));
            foreach (var bad2 in new[] { "version = 1.9.0\n", "windows = a.exe\nwindows.size=1\nwindows.sha256=" + new string('a', 64), "version=1.9.0\nwindows=../x.exe\nwindows.size=1\nwindows.sha256=" + new string('a', 64) })
            {
                try { Updater.Parse(bad2, "windows"); Console.WriteLine("  принят: " + bad2); }
                catch (InvalidDataException ex) { Console.WriteLine("  отклонён: " + ex.Message); }
            }

            return 0;
        }

        // Выбор канала: «меньше задержка» и «по порядку».
        static int Balance()
        {
            Application.EnableVisualStyles();
            var f = new MainForm(null);
            f.Show();
            Get<System.Windows.Forms.Timer>(f, "probeTimer").Stop();
            Get<System.Windows.Forms.Timer>(f, "netTimer").Stop();
            var settings = Get<AppSettings>(f, "settings");
            var main = new Connection { Name = "Офис — основной", Server = "a.test", Group = "Офис", Psk = "k", Balance = "latency" };
            var res = new Connection { Name = "Офис — резерв", Server = "b.test", Group = "Офис", Psk = "k", Balance = "latency" };
            var emer = new Connection { Name = "Офис — аварийный", Server = "c.test", Group = "Офис", Psk = "k", Balance = "latency" };
            settings.Connections.AddRange(new[] { main, res, emer });
            settings.Selected = main.Name;
            var reach = Get<System.Collections.Generic.Dictionary<string, ProbeResult>>(f, "reach");
            reach["a.test"] = new ProbeResult { State = Reach.Up, RttMs = 120, When = DateTime.Now };
            reach["b.test"] = new ProbeResult { State = Reach.Up, RttMs = 40, When = DateTime.Now };
            reach["c.test"] = new ProbeResult { State = Reach.Down, When = DateTime.Now };
            Call(f, "RefreshConnectionLists");
            var members = new System.Collections.Generic.List<Connection> { main, res, emer };
            var t = typeof(MainForm);
            Func<string, object[], object> M = (n, args) => t.GetMethod(n, Any).Invoke(f, args);
            Console.WriteLine("  задержка: самый быстрый = " + ((Connection)M("FastestUp", new object[] { members, null })).Name);
            Console.WriteLine("  задержка: после ошибки на резерве -> " + ((Connection)M("NextFallback", new object[] { res })).Name);
            Console.WriteLine("  задержка: переспрашивать про основной = " + (M("BetterAvailable", new object[] { res }) != null));
            Set(f, "autoPrimaryPending", true);
            Call(f, "MaybeSelectPrimary");
            Console.WriteLine("  задержка: при запуске выбран = " + settings.Selected);
            foreach (var m in members) m.Balance = "";
            Console.WriteLine("  по порядку: после ошибки на основном -> " + ((Connection)M("NextFallback", new object[] { main })).Name);
            Console.WriteLine("  по порядку: переспрашивать при резерве -> " + (((Connection)M("BetterAvailable", new object[] { res })) ?? new Connection { Name = "нет" }).Name);
            Set(f, "allowClose", true);
            f.Close();
            return 0;
        }

        // Общие логин и пароль в группе каналов.
        static int Creds()
        {
            Application.EnableVisualStyles();
            var f = new MainForm(null);
            f.Show();
            var settings = Get<AppSettings>(f, "settings");
            var main = new Connection { Name = "Офис — основной", Server = "a", Group = "Офис", Psk = "k", User = "user", Password = "p@ss", SavePassword = true };
            var res = new Connection { Name = "Офис — резерв", Server = "b", Group = "Офис", Psk = "k" };
            var other = new Connection { Name = "Офис — чужой логин", Server = "c", Group = "Офис", Psk = "k", User = "petrov" };
            var solo = new Connection { Name = "Склад", Server = "d", Psk = "k" };
            settings.Connections.AddRange(new[] { main, res, other, solo });
            var remote = Get<RemotePage>(f, "remote");
            foreach (var c in new[] { res, other, solo })
            {
                Call(f, "LoadCredentials", c);
                Console.WriteLine(string.Format("  {0}: login='{1}' pass='{2}' save={3}", c.Name, remote.User.Text, remote.Pass.Text, remote.SavePass.Checked));
            }
            res.User = "user"; res.Password = "new"; res.SavePassword = true;
            Call(f, "ShareCredentials", res);
            Console.WriteLine(string.Format("  после подключения через резерв: основной pass='{0}', чужой login='{1}' pass='{2}', склад login='{3}'", main.Password, other.User, other.Password, solo.User));
            Set(f, "allowClose", true);
            f.Close();
            return 0;
        }

        // Разбор и запись .omnivpn: пример, круговой прогон и ошибки.
        static int Profiles(string samplePath)
        {
            var bundle = ProfileFile.Read(samplePath);
            foreach (var c in bundle.Vpn)
                Console.WriteLine(string.Format("  [{0}] server={1} routes={2} group={3} balance={4} domain={5} dns={6} dnsdomain={7} user={8}", c.Name, c.Server, c.Routes, c.Group, c.Balance, c.Domain, c.Dns, c.DnsDomain, c.User));
            foreach (var h in bundle.Rdp)
                Console.WriteLine(string.Format("  ПК [{0}] host={1} user={2} domain={3} vpn={4} collection={5} gateway={6} проверка={7}:{8}",
                    h.Name, h.Host, h.User, h.Domain, h.Vpn, h.Collection, h.Gateway, h.ProbeHost, h.ProbePort));
            var again = ProfileFile.Parse(ProfileFile.Write(bundle.Vpn, bundle.Rdp));
            bool same = again.Vpn.Count == bundle.Vpn.Count && again.Rdp.Count == bundle.Rdp.Count;
            for (int i = 0; same && i < bundle.Vpn.Count; i++)
                same = again.Vpn[i].Name == bundle.Vpn[i].Name && again.Vpn[i].Server == bundle.Vpn[i].Server && again.Vpn[i].Psk == bundle.Vpn[i].Psk &&
                       again.Vpn[i].Routes == bundle.Vpn[i].Routes && again.Vpn[i].User == bundle.Vpn[i].User && again.Vpn[i].Group == bundle.Vpn[i].Group &&
                       again.Vpn[i].Balance == bundle.Vpn[i].Balance && again.Vpn[i].Domain == bundle.Vpn[i].Domain &&
                       again.Vpn[i].Dns == bundle.Vpn[i].Dns && again.Vpn[i].DnsDomain == bundle.Vpn[i].DnsDomain;
            for (int i = 0; same && i < bundle.Rdp.Count; i++)
                same = again.Rdp[i].Name == bundle.Rdp[i].Name && again.Rdp[i].Host == bundle.Rdp[i].Host && again.Rdp[i].User == bundle.Rdp[i].User &&
                       again.Rdp[i].Vpn == bundle.Rdp[i].Vpn && again.Rdp[i].Collection == bundle.Rdp[i].Collection &&
                       again.Rdp[i].Gateway == bundle.Rdp[i].Gateway && again.Rdp[i].Domain == bundle.Rdp[i].Domain && again.Rdp[i].FullScreen == bundle.Rdp[i].FullScreen && again.Rdp[i].MultiMon == bundle.Rdp[i].MultiMon;
            Console.WriteLine("roundtrip: " + same);
            foreach (var bad in new[] { "[A]\nserver = x", "[A]\npsk = k", "[A]\nserver=x\npsk=k\nroutes=10.0.0.0/40", "[A]\nserver=x\npsk=k\n[a]\nserver=y\npsk=k",
                                        "[P]\ntype=rdp", "[P]\ntype=rdp\nhost=pc:99999", "[P]\ntype=rdp\nhost=pc\ncollection=my coll", "[P]\ntype=rdp\nhost=pc\ngateway=gw:0", "[P]\ntype=rdp\nhost=pc\ndomain=CORP\\x", "[A]\nserver=x\npsk=k\ndomain=my domain", "[A]\nserver=x\npsk=k\ndns=10.0.0.1,10.0.0.2,10.0.0.3", "[A]\nserver=x\npsk=k\ndns=dns.local", "[A]\nserver=x\npsk=k\ndnsdomain=corp loc", "просто текст", "" })
            {
                try { ProfileFile.Parse(bad); Console.WriteLine("  NO ERROR for: " + bad.Replace("\n", " | ")); }
                catch (Exception ex) { Console.WriteLine("  error ok: " + ex.Message); }
            }
            return 0;
        }

        static int DialTest(string server)
        {
            VpnProfile.Apply(server, "test-psk'quote", new List<string> { "192.168.250.0/24" }, new List<string>(), "");
            Console.WriteLine("profile created");
            bool inSection = false;
            foreach (string line in File.ReadAllLines(Ras.UserPhonebook))
            {
                if (line.StartsWith("[")) inSection = line == "[" + VpnProfile.EntryName + "]";
                else if (inSection && (line.StartsWith("IpPrioritizeRemote") || line.StartsWith("Ipv6PrioritizeRemote") ||
                         line.StartsWith("PhoneNumber") || line.StartsWith("VpnStrategy") || line.StartsWith("AuthRestrictions") ||
                         line.StartsWith("DataEncryption") || line.StartsWith("NumRoutes") || line.StartsWith("Route")))
                    Console.WriteLine("  pbk: " + line);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var done = new ManualResetEvent(false);
            int fs = 0, fe = 0;
            Ras.DialCallback cb = delegate (uint msg, int st, int err)
            {
                Console.WriteLine(string.Format("  {0:0.0}s callback state={1} err={2} -> {3}", sw.Elapsed.TotalSeconds, st, err, Errors.Stage(st, server)));
                if (err != 0 || st == Ras.RASCS_Connected || st == Ras.RASCS_Disconnected) { fs = st; fe = err; done.Set(); }
            };
            IntPtr h;
            int result = Ras.Dial(Ras.UserPhonebook, VpnProfile.EntryName, "user", "pass", cb, out h);
            Console.WriteLine("dial started: r=" + result + " handle=" + (h != IntPtr.Zero));
            int st2, err2;
            Thread.Sleep(3000);
            Console.WriteLine("status after 3s: r=" + Ras.GetConnectStatus(h, out st2, out err2) + " state=" + st2 + " findConnection=" + (Ras.FindConnection(VpnProfile.EntryName) != IntPtr.Zero));
            bool cancel = Environment.GetEnvironmentVariable("CANCEL") == "1";
            if (cancel)
            {
                var c = System.Diagnostics.Stopwatch.StartNew();
                Ras.HangUp(h);
                Console.WriteLine(string.Format("cancel: hangup took {0:0.0}s, status now r={1}", c.Elapsed.TotalSeconds, Ras.GetConnectStatus(h, out st2, out err2)));
                result = Ras.ERROR_USER_DISCONNECTION;
            }
            else
            {
                done.WaitOne(120000);
                Ras.HangUp(h);
                result = fe;
                Console.WriteLine(string.Format("dial finished after {0:0.0}s: state={1} err={2}", sw.Elapsed.TotalSeconds, fs, fe));
            }
            GC.KeepAlive(cb);
            Console.WriteLine("explain: " + Errors.Explain(result));
            Console.WriteLine("explain 691: " + Errors.Explain(691));
            Console.WriteLine("explain 650: " + Errors.Explain(650));
            VpnProfile.Remove(true);
            Console.WriteLine("profile removed; still in pbk: " + (File.Exists(Ras.UserPhonebook) && File.ReadAllText(Ras.UserPhonebook).Contains("[" + VpnProfile.EntryName + "]")));
            return 0;
        }

        static int Shots(string dir)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var f = new MainForm(null);
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(0, 0);
            f.Show();
            Get<System.Windows.Forms.Timer>(f, "pollTimer").Stop();
            Get<System.Windows.Forms.Timer>(f, "updateTimer").Stop();
            Pump();
            Shot(f, Path.Combine(dir, "0-empty.png"));
            Call(f, "ShowPage", 1);
            Pump();
            Shot(f, Path.Combine(dir, "0-rdp-empty.png"));
            Call(f, "ShowPage", 0);

            var settings = Get<AppSettings>(f, "settings");
            settings.Connections.Add(new Connection { Name = "Офис — основной", Server = "vpn.example.ru", Group = "Офис", Psk = "secret", Routes = "192.168.88.0/24", User = "user", Password = "password", SavePassword = true });
            settings.Connections.Add(new Connection { Name = "Офис — резерв", Server = "vpn2.example.ru", Group = "Офис", Psk = "secret", User = "user" });
            settings.Connections.Add(new Connection { Name = "Офис — аварийный", Server = "vpn3.example.ru", Group = "Офис", Psk = "secret", User = "user" });
            settings.Selected = "Офис — основной";
            var reach = Get<System.Collections.Generic.Dictionary<string, ProbeResult>>(f, "reach");
            reach["vpn.example.ru"] = new ProbeResult { State = Reach.Up, RttMs = 24, When = DateTime.Now };
            reach["vpn2.example.ru"] = new ProbeResult { State = Reach.Up, RttMs = 31, When = DateTime.Now };
            reach["vpn3.example.ru"] = new ProbeResult { State = Reach.Down, When = DateTime.Now };
            Set(f, "autoPrimaryPending", false);
            Call(f, "RefreshConnectionLists");
            Call(f, "EditConnection", 0);
            Call(f, "SetIdle", null, false);
            Pump();
            Shot(f, Path.Combine(dir, "1-idle.png"));

            Set(f, "active", settings.Connections[0]);
            Set(f, "activeUser", "user");
            Call(f, "SetConnecting", "Соединение с сервером vpn.example.ru (IPsec)…");
            var art = Get<RemotePage>(f, "remote").Art;
            for (int i = 0; i < 8; i++) { Thread.Sleep(40); art.Tick(); Application.DoEvents(); }
            Pump();
            Shot(f, Path.Combine(dir, "2-connecting.png"));

            Set(f, "vpnIp", "10.10.10.25");
            Set(f, "lastDuration", "01:24:37");
            Set(f, "totalRx", (ulong)(1.84 * 1024 * 1024 * 1024));
            Set(f, "totalTx", (ulong)(212 * 1024 * 1024));
            Call(f, "SetConnected");
            var info = Get<RemotePage>(f, "remote").Info;
            info.Set("Получено", "1.84 ГБ", "↓ 2.31 МБ/с");
            info.Set("Отправлено", "212 МБ", "↑ 148 КБ/с");
            var g = Get<RemotePage>(f, "remote").Graph;
            var rnd = new Random(3);
            for (int i = 0; i < 60; i++)
                g.Push(1.4e6 + 1.2e6 * Math.Sin(i / 6.0) + rnd.Next(400000), 120e3 + 60e3 * Math.Cos(i / 4.0) + rnd.Next(30000));
            Get<Sidebar>(f, "sidebar").SetNet("работает, 12 мс", Theme.Ok, "24 мс", Theme.Ok);
            Pump();
            Shot(f, Path.Combine(dir, "3-connected.png"));

            Call(f, "SetIdle", Errors.Explain(809), true);
            Set(f, "active", settings.Connections[0]);
            Call(f, "OfferFallback", 809);
            Pump();
            Shot(f, Path.Combine(dir, "4-error.png"));

            Call(f, "ShowPage", 2);
            Pump();
            Shot(f, Path.Combine(dir, "5-connections.png"));

            Call(f, "ShowPage", 3);
            Pump();
            Shot(f, Path.Combine(dir, "6-about.png"));
            Set(f, "update", new UpdateInfo { Version = "1.9.1", Notes = "Исправлен двойной домен в логине RDP\nПроверка обновлений раз в 6 часов", File = "OmniVPNConnect-Setup-1.9.1.exe", Size = 190587 });
            Call(f, "ShowUpdate");
            Pump();
            Shot(f, Path.Combine(dir, "6b-about-update.png"));
            var about = Get<AboutPage>(f, "about");
            about.UpdateProgress.Visible = true;
            about.UpdateProgress.Value = 0.45;
            about.UpdateButton.Text = "Отменить";
            about.SetUpdateStatus("Загрузка версии 1.9.1: 45%  ·  0.1 МБ из 0.2 МБ", Theme.Label);
            Get<Sidebar>(f, "sidebar").SetUpdate("Обновление: 45%");
            Pump();
            Shot(f, Path.Combine(dir, "6c-about-download.png"));

            settings.Rdp.Add(new RdpHost { Name = "Бухгалтерия", Host = "192.168.20.25", User = "CORP\\user", Vpn = "Офис" });
            settings.Rdp.Add(new RdpHost { Name = "Терминальный сервер", Host = "broker.corp.local", User = "CORP\\user", Collection = "Office", Gateway = "gw.example.ru" });
            settings.Rdp.Add(new RdpHost { Name = "Домашний ПК", Host = "192.168.1.10" });
            Get<System.Windows.Forms.Timer>(f, "probeTimer").Stop();
            Call(f, "ShowPage", 1);
            var rdpReach = Get<System.Collections.Generic.Dictionary<string, ProbeResult>>(f, "rdpReach");
            rdpReach["192.168.1.10"] = new ProbeResult { State = Reach.Up, RttMs = 3, When = DateTime.Now };
            Call(f, "RefreshRdp");
            Pump();
            Shot(f, Path.Combine(dir, "7-rdp.png"));
            Call(f, "EditRdp", 1);
            Pump();
            Shot(f, Path.Combine(dir, "8-rdp-edit.png"));

            Set(f, "allowClose", true);
            f.Close();
            return 0;
        }

        static void Pump()
        {
            for (int i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(30); }
        }

        static void Shot(Form f, string path)
        {
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        [System.Runtime.InteropServices.DllImport("rasapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int RasEnumConnections(IntPtr lprasconn, ref int lpcb, out int lpcConnections);

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static T Get<T>(object o, string name) { return (T)o.GetType().GetField(name, Any).GetValue(o); }
        static void Set(object o, string name, object v) { o.GetType().GetField(name, Any).SetValue(o, v); }
        static void Call(object o, string name, params object[] args) { o.GetType().GetMethod(name, Any).Invoke(o, args ?? new object[] { null }); }
    }
}
