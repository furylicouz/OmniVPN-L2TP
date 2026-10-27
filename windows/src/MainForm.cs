using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace L2TPConnect
{
    internal enum UiState { Disconnected, Connecting, Connected, Disconnecting }

    internal sealed class MainForm : Form
    {
        readonly Sidebar sidebar = new Sidebar();
        readonly Card card = new Card();
        readonly RemotePage remote = new RemotePage();
        readonly RdpPage rdp = new RdpPage();
        readonly ConnectionsPage conns = new ConnectionsPage();
        readonly AboutPage about = new AboutPage();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        readonly ToolStripMenuItem trayToggle = new ToolStripMenuItem();
        readonly System.Windows.Forms.Timer pollTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer animTimer = new System.Windows.Forms.Timer();

        AppSettings settings = new AppSettings();
        bool updating;
        int editIndex = -1;

        UiState state = UiState.Disconnected;
        IntPtr hConn = IntPtr.Zero;
        volatile bool cancelRequested;
        bool exitAfterDisconnect, allowClose, trayHintShown;
        Connection active;
        string activeUser = "";

        uint lastRx, lastTx;
        ulong totalRx, totalTx;
        bool statsPrimed;
        string vpnIp;
        string lastDuration = "00:00:00";
        readonly Stopwatch statsClock = new Stopwatch();

        string pendingImport;

        // Доступность серверов (ключ — адрес сервера) и резервный канал, предложенный после ошибки.
        readonly Dictionary<string, ProbeResult> reach = new Dictionary<string, ProbeResult>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> probing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly System.Windows.Forms.Timer probeTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer netTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer();
        UpdateInfo update;                 // найденная новая версия
        string updateNotified;             // о какой версии уже сообщили в трее
        bool updateChecking, balloonIsUpdate;
        CancellationTokenSource updateDownload;
        bool netChecking;
        ProbeResult serverIke;
        string serverIkeHost;
        NetworkAddressChangedEventHandler netAddressChanged;
        NetworkAvailabilityChangedEventHandler netAvailabilityChanged;
        bool autoPrimaryPending = true;
        Connection fallbackTarget;

        public MainForm(string profileFile)
        {
            pendingImport = profileFile;
            BuildUi();
        }

        void BuildUi()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = Program.AppName;
            Font = new Font("Segoe UI", 9f);
            BackColor = Theme.Back;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(820, 540);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            sidebar.Bounds = new Rectangle(0, 0, 230, 540);
            card.Bounds = new Rectangle(240, 10, 570, 520);
            foreach (var page in new Control[] { remote, rdp, conns, about })
            {
                page.Bounds = new Rectangle(1, 1, 568, 518);
                card.Controls.Add(page);
            }
            Controls.Add(card);
            Controls.Add(sidebar);

            sidebar.SelectedChanged += delegate { ShowPage(sidebar.Selected); };

            remote.Profile.SelectedIndexChanged += delegate { OnProfileChosen(); };
            remote.EditLink.Click += delegate { EditConnection(settings.Connections.IndexOf(Current)); sidebar.Selected = 2; };
            remote.AddFirst.Click += delegate { NewConnection(); sidebar.Selected = 2; };
            remote.ImportFirst.Click += delegate { ImportFromDialog(); };
            remote.Profile.DropDownOpening += delegate { ProbeAll(true); };
            remote.Fallback.Click += delegate { ConnectFallback(); };
            probeTimer.Interval = 45000;
            probeTimer.Tick += delegate { ProbeAll(false); ProbeRdp(false); };
            netTimer.Interval = 10000;
            netTimer.Tick += delegate { CheckNet(); };
            // Сеть поменялась (выдернули кабель, сменился Wi-Fi) — проверяем сразу, не дожидаясь таймера.
            netAddressChanged = delegate { Ui(CheckNet); };
            netAvailabilityChanged = delegate { Ui(CheckNet); };
            NetworkChange.NetworkAddressChanged += netAddressChanged;
            NetworkChange.NetworkAvailabilityChanged += netAvailabilityChanged;

            // Первая проверка обновлений через 5 с после запуска, дальше раз в 6 часов.
            updateTimer.Interval = 5000;
            updateTimer.Tick += delegate { updateTimer.Interval = 6 * 3600 * 1000; CheckUpdates(false); };
            about.CheckButton.Click += delegate { CheckUpdates(true); };
            about.UpdateButton.Click += delegate { if (updateDownload != null) updateDownload.Cancel(); else InstallUpdate(); };
            sidebar.UpdateClicked += delegate { sidebar.Selected = 3; };

            rdp.Add.Click += delegate { EditRdp(-1); };
            rdp.AddFirst.Click += delegate { EditRdp(-1); };
            rdp.Computers.RowClicked += i => OpenRdp(settings.Rdp[i]);
            rdp.Computers.EditClicked += i => EditRdp(i);
            rdp.SaveButton.Click += delegate { SaveRdp(); };
            rdp.CancelButton.Click += delegate { rdp.ShowEditor(false); };
            rdp.Delete.Click += delegate { DeleteRdp(); };
            remote.SavePass.CheckedChanged += delegate { OnSavePasswordChanged(); };
            remote.Connect.Click += delegate { StartConnect(); };
            remote.Cancel.Click += delegate { CancelConnect(); };
            remote.Disconnect.Click += delegate { Disconnect(); };
            remote.User.Box.KeyDown += OnLoginKeyDown;
            remote.Pass.Box.KeyDown += OnLoginKeyDown;

            conns.Selector.SelectedIndexChanged += delegate { if (!updating) EditConnection(conns.Selector.SelectedIndex); };
            conns.New.Click += delegate { NewConnection(); };
            conns.Delete.Click += delegate { DeleteConnection(); };
            conns.SaveButton.Click += delegate { SaveConnection(); };
            conns.CancelButton.Click += delegate { CancelEditing(); };
            conns.Import.Click += delegate { ImportFromDialog(); };
            conns.Export.Click += delegate { ShowExportMenu(); };

            pollTimer.Interval = 1000;
            pollTimer.Tick += delegate { Poll(); };
            animTimer.Interval = 40;
            animTimer.Tick += delegate
            {
                remote.Art.Tick();
                remote.Progress.Tick();
            };

            BuildTray();
            ResumeLayout(false);
            PerformLayout();
        }

        void BuildTray()
        {
            var open = new ToolStripMenuItem("Открыть " + Program.AppName);
            open.Font = new Font(trayMenu.Font, FontStyle.Bold);
            open.Click += delegate { RestoreWindow(); };
            trayToggle.Click += delegate { OnTrayToggle(); };
            var exit = new ToolStripMenuItem("Выход");
            exit.Click += delegate { ExitApp(); };
            trayMenu.Items.AddRange(new ToolStripItem[] { open, new ToolStripSeparator(), trayToggle, new ToolStripSeparator(), exit });
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { RestoreWindow(); };
            tray.BalloonTipClicked += delegate
            {
                if (!balloonIsUpdate) return;
                RestoreWindow();
                sidebar.Selected = 3;
            };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            settings = AppSettings.Load();
            RefreshConnectionLists();
            if (settings.Connections.Count > 0)
                EditConnection(Math.Max(0, settings.Connections.IndexOf(Current)));
            else
                NewConnection();
            SetIdle(null, false);
            RefreshRdp();
            ShowPage(0);
            tray.Visible = true;
            Poll();
            pollTimer.Start();
            ProbeAll(true);
            probeTimer.Start();
            CheckNet();
            netTimer.Start();
            updateTimer.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            FocusFirstEmpty();
            if (pendingImport != null)
            {
                string path = pendingImport;
                pendingImport = null;
                BeginInvoke(new Action(() => ImportFile(path)));
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.ShowMessage)
            {
                RestoreWindow();
                return;
            }
            if (m.Msg == Program.WM_COPYDATA)
            {
                string path = Program.ReadProfileFile(m.LParam);
                if (path != null)
                {
                    // Отвечаем сразу, чтобы второй экземпляр не ждал, пока пользователь подтвердит импорт.
                    BeginInvoke(new Action(() => ImportFile(path)));
                    m.Result = (IntPtr)1;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        // ---------- Импорт и экспорт файлов .omnivpn ----------

        void ImportFromDialog()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = ProfileFile.Filter;
                dlg.Title = "Импорт профилей " + Program.AppName;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    ImportFile(dlg.FileName);
            }
        }

        void ImportFile(string path)
        {
            RestoreWindow();
            string fileName = Path.GetFileName(path);
            ProfileBundle incoming;
            try
            {
                incoming = ProfileFile.Read(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось прочитать файл профилей «" + fileName + "».\n\n" + ex.Message,
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var sb = new StringBuilder("Добавить из файла «" + fileName + "»?\n");
            foreach (var c in incoming.Vpn)
            {
                sb.Append("\n•  ").Append(c.Name).Append(" — ").Append(c.Server);
                if (c.Routes.Length > 0)
                    sb.Append(", сети: ").Append(c.Routes);
                if (settings.Find(c.Name) != null)
                    sb.Append("  (заменит существующее)");
            }
            foreach (var h in incoming.Rdp)
            {
                sb.Append("\n•  ПК «").Append(h.Name).Append("» — ").Append(h.Host);
                if (FindRdp(h.Name) != null)
                    sb.Append("  (заменит существующий)");
            }
            if (MessageBox.Show(this, sb.ToString(), Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            foreach (var c in incoming.Vpn)
            {
                var existing = settings.Find(c.Name);
                if (existing == null)
                {
                    settings.Connections.Add(c);
                    continue;
                }
                existing.Server = c.Server;
                existing.Psk = c.Psk;
                existing.Routes = c.Routes;
                if (c.Group.Length > 0)
                    existing.Group = c.Group;
                existing.Balance = c.Balance;
                if (c.Domain.Length > 0)
                    existing.Domain = c.Domain;
                if (c.Dns.Length > 0)
                    existing.Dns = c.Dns;
                if (c.DnsDomain.Length > 0)
                    existing.DnsDomain = c.DnsDomain;
                if (c.User.Length > 0)
                    existing.User = c.User;
                if (c.SavePassword)
                {
                    existing.Password = c.Password;
                    existing.SavePassword = true;
                }
            }
            foreach (var h in incoming.Rdp)
            {
                int i = settings.Rdp.IndexOf(FindRdp(h.Name));
                if (i >= 0)
                    settings.Rdp[i] = h;
                else
                    settings.Rdp.Add(h);
            }
            if (incoming.Vpn.Count > 0)
                settings.Selected = incoming.Vpn[0].Name;
            if (!TrySave())
                return;

            RefreshConnectionLists();
            RefreshRdp();
            if (settings.Connections.Count > 0)
                EditConnection(settings.Connections.IndexOf(Current));
            if (state == UiState.Disconnected)
                SetIdle(null, false);
            var parts = new List<string>();
            if (incoming.Vpn.Count > 0)
                parts.Add("подключений: " + incoming.Vpn.Count);
            if (incoming.Rdp.Count > 0)
                parts.Add("компьютеров: " + incoming.Rdp.Count);
            string text = "Импортировано " + string.Join(", ", parts) + ".";
            if (incoming.Vpn.Count > 0 || incoming.Rdp.Count == 0)
            {
                remote.Msg.ShowOk(text);
                remote.ShowMode(CurrentPageMode());
                ShowPage(0);
            }
            else
            {
                rdp.Msg.ShowOk(text);
                ShowPage(1);
            }
            ProbeAll(true);
        }

        void ShowExportMenu()
        {
            if (settings.Connections.Count == 0)
            {
                conns.Msg.ShowError("Нет сохранённых подключений для экспорта.");
                return;
            }
            Connection one = editIndex >= 0 ? settings.Connections[editIndex] : Current;
            var menu = new ContextMenuStrip();
            menu.Font = Theme.Body;
            menu.ShowImageMargin = false;
            if (one != null)
                menu.Items.Add("Подключение «" + one.Name + "»", null, delegate { Export(new[] { one }, null, one.Name); });
            menu.Items.Add("Все подключения (" + settings.Connections.Count + ")" +
                           (settings.Rdp.Count > 0 ? " и компьютеры (" + settings.Rdp.Count + ")" : ""), null,
                delegate { Export(settings.Connections.ToArray(), settings.Rdp.ToArray(), "OmniVPN-профили"); });
            menu.Closed += delegate { BeginInvoke(new Action(menu.Dispose)); };
            menu.Show(conns.Export, new Point(0, conns.Export.Height));
        }

        void Export(Connection[] list, RdpHost[] computers, string baseName)
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = ProfileFile.Filter;
                dlg.DefaultExt = "omnivpn";
                dlg.AddExtension = true;
                dlg.Title = "Экспорт профилей " + Program.AppName;
                foreach (char ch in Path.GetInvalidFileNameChars())
                    baseName = baseName.Replace(ch, '_');
                dlg.FileName = baseName + ProfileFile.Extension;
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;
                try
                {
                    File.WriteAllText(dlg.FileName, ProfileFile.Write(list, computers), new UTF8Encoding(true));
                    conns.Msg.ShowOk("Сохранено в «" + Path.GetFileName(dlg.FileName) + "». Пароли в файл не записываются.");
                }
                catch (Exception ex)
                {
                    conns.Msg.ShowError("Не удалось сохранить файл: " + ex.Message);
                }
            }
        }

        void ShowPage(int index)
        {
            remote.Visible = index == 0;
            rdp.Visible = index == 1;
            conns.Visible = index == 2;
            about.Visible = index == 3;
            if (index == 1)
            {
                RefreshRdp();
                ProbeRdp(true);
            }
            if (sidebar.Selected != index)
                sidebar.Selected = index;
            if (index == 0)
                FocusFirstEmpty();
        }

        void FocusFirstEmpty()
        {
            if (state != UiState.Disconnected || !remote.Visible || settings.Connections.Count == 0)
                return;
            if (remote.User.Text.Length == 0)
                remote.User.Box.Focus();
            else if (remote.Pass.Text.Length == 0)
                remote.Pass.Box.Focus();
            else
                remote.Connect.Focus();
        }

        void OnLoginKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && state == UiState.Disconnected)
            {
                e.SuppressKeyPress = true;
                StartConnect();
            }
        }

        // ---------- Список подключений ----------

        Connection Current
        {
            get
            {
                var c = settings.Find(settings.Selected);
                return c ?? (settings.Connections.Count > 0 ? settings.Connections[0] : null);
            }
        }

        void RefreshConnectionLists()
        {
            updating = true;
            try
            {
                var names = settings.Connections.ConvertAll(c => c.Name);
                remote.Profile.SetItems(names);
                conns.Selector.SetItems(names);
                var current = Current;
                if (current != null)
                {
                    settings.Selected = current.Name;
                    remote.Profile.SelectedIndex = settings.Connections.IndexOf(current);
                    LoadCredentials(current);
                }
            }
            finally
            {
                updating = false;
            }
            ApplyStatuses();
        }

        void LoadCredentials(Connection c)
        {
            var src = CredentialSource(c);
            bool was = updating;
            updating = true;
            remote.User.Text = c.User.Length > 0 ? c.User : src.User;
            remote.Pass.Text = src.SavePassword ? src.Password : "";
            remote.SavePass.Checked = src.SavePassword;
            updating = was;
        }

        // Каналы одной группы — это одна учётка: если у канала нет своих данных, берём их у другого канала группы.
        Connection CredentialSource(Connection c)
        {
            if (c.SavePassword && c.User.Length > 0)
                return c;
            Connection withUser = null;
            foreach (var m in GroupOf(c))
            {
                if (m == c || m.User.Length == 0)
                    continue;
                if (c.User.Length > 0 && !string.Equals(m.User, c.User, StringComparison.CurrentCultureIgnoreCase))
                    continue;
                if (m.SavePassword)
                    return m;
                if (withUser == null)
                    withUser = m;
            }
            return withUser ?? c;
        }

        // Записать логин и пароль всем каналам группы, кроме тех, у кого явно указан другой логин.
        void ShareCredentials(Connection c)
        {
            foreach (var m in GroupOf(c))
            {
                if (m == c || (m.User.Length > 0 && !string.Equals(m.User, c.User, StringComparison.CurrentCultureIgnoreCase)))
                    continue;
                m.User = c.User;
                m.SavePassword = c.SavePassword;
                m.Password = c.Password;
            }
        }

        void OnProfileChosen()
        {
            if (updating || remote.Profile.SelectedIndex < 0)
                return;
            var c = settings.Connections[remote.Profile.SelectedIndex];
            settings.Selected = c.Name;
            LoadCredentials(c);
            remote.Msg.Clear();
            HideFallback();
            autoPrimaryPending = false;
            TrySave();
            FocusFirstEmpty();
            CheckNet();
        }

        // Выбрать подключение программно; keepCredentials — оставить введённые логин и пароль (общие для каналов офиса).
        void SelectConnection(Connection c, bool keepCredentials)
        {
            updating = true;
            remote.Profile.SelectedIndex = settings.Connections.IndexOf(c);
            updating = false;
            settings.Selected = c.Name;
            if (!keepCredentials)
                LoadCredentials(c);
            TrySave();
        }

        // ---------- Интернет и задержка до VPN-сервера ----------

        async void CheckNet()
        {
            if (netChecking || IsDisposed)
                return;
            netChecking = true;
            try
            {
                Connection target = state == UiState.Connected && active != null ? active : Current;
                var inetTask = NetCheck.InternetAsync();
                var dnsTask = NetCheck.DnsAsync();
                var pingTask = target != null ? NetCheck.PingAsync(target.Server.Trim()) : null;
                var inet = await inetTask;
                bool dns = await dnsTask;
                ProbeResult srv = pingTask != null ? await pingTask : null;

                if (target != null && srv.State != Reach.Up && !srv.NameNotFound)
                {
                    // Сервер не отвечает на ping (FortiGate режет ICMP) — берём время ответа IPsec, не чаще раза в минуту.
                    var r = ReachOf(target);
                    if (state != UiState.Connected && r != null && r.State == Reach.Up && (DateTime.Now - r.When).TotalSeconds < 90)
                        srv = r;
                    else if (serverIke != null && serverIkeHost == target.Server && (DateTime.Now - serverIke.When).TotalSeconds < 60)
                        srv = serverIke;
                    else if (inet.State == Reach.Up)
                    {
                        serverIke = await IkeProbe.CheckAsync(target.Server.Trim());
                        serverIkeHost = target.Server;
                        srv = serverIke;
                    }
                }
                if (IsDisposed)
                    return;

                string netText;
                Color netColor;
                if (inet.State != Reach.Up) { netText = "нет связи"; netColor = Theme.Err; }
                else if (!dns) { netText = "нет DNS"; netColor = Theme.Busy; }
                else { netText = "работает, " + inet.RttMs + " мс"; netColor = Theme.Ok; }

                string srvText;
                Color srvColor;
                if (target == null) { srvText = "—"; srvColor = Theme.Dots; }
                else if (srv.State == Reach.Up)
                {
                    srvText = srv.RttMs + " мс";
                    srvColor = srv.RttMs < 100 ? Theme.Ok : srv.RttMs < 250 ? Theme.Busy : Theme.Err;
                }
                else { srvText = srv.NameNotFound ? "имя не найдено" : "нет ответа"; srvColor = Theme.Err; }
                sidebar.SetNet(netText, netColor, srvText, srvColor);
            }
            finally
            {
                netChecking = false;
            }
        }

        // ---------- Удалённые ПК (RDP) ----------

        readonly Dictionary<string, ProbeResult> rdpReach = new Dictionary<string, ProbeResult>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> rdpProbing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int rdpEditIndex = -1;
        RdpHost pendingRdp;
        DateTime pendingRdpSince;

        RdpHost FindRdp(string name)
        {
            return settings.Rdp.Find(h => string.Equals(h.Name, name, StringComparison.CurrentCultureIgnoreCase));
        }

        // Домен для логина RDP: у компьютера, иначе у его VPN-подключения (группы).
        string RdpDomain(RdpHost h)
        {
            if (h.Domain.Length > 0)
                return h.Domain;
            if (h.Vpn.Length == 0)
                return "";
            var withDomain = VpnMembers(h.Vpn).Find(c => c.Domain.Length > 0);
            return withDomain != null ? withDomain.Domain : "";
        }

        // VPN, который нужен компьютеру, сейчас подключён (по группе или по названию подключения).
        bool VpnReadyFor(string vpn)
        {
            if (vpn.Length == 0)
                return true;
            return state == UiState.Connected && active != null &&
                   (string.Equals(active.Group.Trim(), vpn, StringComparison.CurrentCultureIgnoreCase) ||
                    string.Equals(active.Name, vpn, StringComparison.CurrentCultureIgnoreCase));
        }

        List<Connection> VpnMembers(string vpn)
        {
            return settings.Connections.FindAll(c =>
                string.Equals(c.Group.Trim(), vpn, StringComparison.CurrentCultureIgnoreCase) ||
                string.Equals(c.Name, vpn, StringComparison.CurrentCultureIgnoreCase));
        }

        void RefreshRdp()
        {
            var rows = new List<RdpList.Row>();
            foreach (var h in settings.Rdp)
            {
                var row = new RdpList.Row { Host = h, DisplayUser = RdpLauncher.FullUser(h.User, RdpDomain(h)) };
                ProbeResult r;
                if (!VpnReadyFor(h.Vpn))
                {
                    row.Status = "нужен VPN";
                    row.StatusColor = Theme.Muted;
                }
                else if (rdpReach.TryGetValue(h.ProbeKey, out r))
                {
                    row.Status = r.State == Reach.Checking ? "проверка…"
                               : r.State == Reach.Up ? "доступен, " + r.RttMs + " мс"
                               : r.NameNotFound ? "имя не найдено" : "недоступен";
                    row.StatusColor = r.State == Reach.Up ? Theme.Ok : r.State == Reach.Down ? Theme.Err : Theme.Muted;
                }
                rows.Add(row);
            }
            rdp.SetRows(rows);
        }

        void ProbeRdp(bool force)
        {
            if (!force && (!rdp.Visible || !Visible || WindowState == FormWindowState.Minimized))
                return;
            foreach (var h in settings.Rdp)
                if (VpnReadyFor(h.Vpn))
                    ProbeRdpHost(h);
        }

        async void ProbeRdpHost(RdpHost h)
        {
            string key = h.ProbeKey;
            if (!rdpProbing.Add(key))
                return;
            if (!rdpReach.ContainsKey(key))
            {
                rdpReach[key] = new ProbeResult { State = Reach.Checking, When = DateTime.Now };
                RefreshRdp();
            }
            ProbeResult result;
            try
            {
                result = await RdpLauncher.CheckAsync(h);
            }
            catch (Exception)
            {
                result = new ProbeResult { State = Reach.Down, When = DateTime.Now };
            }
            rdpProbing.Remove(key);
            if (IsDisposed)
                return;
            rdpReach[key] = result;
            RefreshRdp();
        }

        void OpenRdp(RdpHost h)
        {
            rdp.Msg.Clear();
            if (VpnReadyFor(h.Vpn))
            {
                LaunchRdp(h);
                return;
            }
            if (state == UiState.Connected)
            {
                rdp.Msg.ShowError("Для «" + h.Name + "» нужен VPN «" + h.Vpn + "», а сейчас подключён «" + active.Name + "». Отключитесь и подключитесь к нужному VPN.");
                return;
            }
            if (state != UiState.Disconnected)
            {
                rdp.Msg.ShowError("Идёт подключение VPN. Дождитесь его и откройте компьютер ещё раз.");
                return;
            }
            var members = VpnMembers(h.Vpn);
            if (members.Count == 0)
            {
                rdp.Msg.ShowError("Для «" + h.Name + "» указан VPN «" + h.Vpn + "», но такого подключения нет. Исправьте компьютер или добавьте подключение.");
                return;
            }
            if (MessageBox.Show(this, "Для «" + h.Name + "» нужен VPN «" + h.Vpn + "».\n\nПодключиться и открыть компьютер?",
                    Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            // Основной канал группы, если он доступен; иначе первый доступный; иначе первый по порядку.
            Connection target = (IsLatencyGroup(members[0]) ? FastestUp(members, null) : null)
                                ?? members.Find(c => { var r = ReachOf(c); return r != null && r.State == Reach.Up; }) ?? members[0];
            pendingRdp = h;
            pendingRdpSince = DateTime.Now;
            SelectConnection(target, false);
            HideFallback();
            ShowPage(0);
            if (remote.User.Text.Trim().Length > 0 && remote.Pass.Text.Length > 0)
            {
                StartConnect(false);
            }
            else
            {
                remote.Msg.ShowOk("Введите логин и пароль и нажмите «Подключиться» — «" + h.Name + "» откроется автоматически.");
                remote.ShowMode(CurrentPageMode());
                FocusFirstEmpty();
            }
        }

        void LaunchPendingRdp()
        {
            var h = pendingRdp;
            if (h == null || (DateTime.Now - pendingRdpSince).TotalMinutes > 10 || !VpnReadyFor(h.Vpn))
                return;
            pendingRdp = null;
            // Даём Windows пару секунд прописать маршруты VPN.
            var t = new System.Windows.Forms.Timer { Interval = 2000 };
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                LaunchRdp(h);
            };
            t.Start();
        }

        void LaunchRdp(RdpHost h)
        {
            try
            {
                RdpLauncher.Launch(h, RdpDomain(h));
            }
            catch (Exception ex)
            {
                rdp.Msg.ShowError("Не удалось запустить «Подключение к удалённому рабочему столу»: " + ex.Message);
                ShowPage(1);
            }
        }

        List<string> VpnChoices()
        {
            var list = new List<string> { "Не нужен" };
            foreach (var c in settings.Connections)
            {
                string name = c.Group.Trim().Length > 0 ? c.Group.Trim() : c.Name;
                if (!list.Exists(x => string.Equals(x, name, StringComparison.CurrentCultureIgnoreCase)))
                    list.Add(name);
            }
            return list;
        }

        void EditRdp(int index)
        {
            rdpEditIndex = index;
            var h = index >= 0 ? settings.Rdp[index] : new RdpHost();
            var choices = VpnChoices();
            if (h.Vpn.Length > 0 && !choices.Exists(x => string.Equals(x, h.Vpn, StringComparison.CurrentCultureIgnoreCase)))
                choices.Add(h.Vpn);
            rdp.Vpn.SetItems(choices);
            int vi = h.Vpn.Length == 0 ? 0 : choices.FindIndex(x => string.Equals(x, h.Vpn, StringComparison.CurrentCultureIgnoreCase));
            if (index < 0 && h.Vpn.Length == 0 && choices.Count > 1)
                vi = 1; // новый компьютер обычно доступен через VPN офиса
            rdp.Vpn.SelectedIndex = Math.Max(0, vi);
            rdp.EditorTitle.Text = index >= 0 ? "Компьютер" : "Новый компьютер";
            rdp.HostName.Text = h.Name;
            rdp.Address.Text = h.Host;
            rdp.User.Text = h.User;
            rdp.Domain.Text = h.Domain;
            var vpnDomain = h.Vpn.Length > 0 ? VpnMembers(h.Vpn).Find(c => c.Domain.Length > 0) : null;
            rdp.Domain.Cue = vpnDomain != null ? vpnDomain.Domain : "CORP";
            rdp.Collection.Text = h.Collection;
            rdp.Gateway.Text = h.Gateway;
            rdp.FullScreen.Checked = h.FullScreen;
            rdp.MultiMon.Checked = h.MultiMon;
            rdp.Delete.Visible = index >= 0;
            rdp.EditorMsg.Clear();
            rdp.ShowEditor(true);
            rdp.HostName.Box.Focus();
        }

        void SaveRdp()
        {
            string name = rdp.HostName.Text.Trim();
            string host = rdp.Address.Text.Trim();
            if (name.Length == 0) { rdp.EditorMsg.ShowError("Укажите название компьютера."); rdp.HostName.Box.Focus(); return; }
            var same = FindRdp(name);
            if (same != null && settings.Rdp.IndexOf(same) != rdpEditIndex)
            {
                rdp.EditorMsg.ShowError("Компьютер с таким названием уже есть.");
                rdp.HostName.Box.Focus();
                return;
            }
            string error = RdpHost.CheckAddress(host);
            if (error != null) { rdp.EditorMsg.ShowError("Адрес: " + error); rdp.Address.Box.Focus(); return; }
            string domain = rdp.Domain.Text.Trim();
            error = ProfileFile.CheckDomain(domain);
            if (error != null) { rdp.EditorMsg.ShowError("Домен: " + error); rdp.Domain.Box.Focus(); return; }
            string collection = rdp.Collection.Text.Trim();
            string gateway = rdp.Gateway.Text.Trim();
            error = RdpHost.CheckCollection(collection);
            if (error != null) { rdp.EditorMsg.ShowError("Коллекция: " + error); rdp.Collection.Box.Focus(); return; }
            error = gateway.Length > 0 ? RdpHost.CheckAddress(gateway) : null;
            if (error != null) { rdp.EditorMsg.ShowError("Шлюз: " + error); rdp.Gateway.Box.Focus(); return; }

            var h = rdpEditIndex >= 0 ? settings.Rdp[rdpEditIndex] : new RdpHost();
            h.Name = name;
            h.Host = host;
            h.User = rdp.User.Text.Trim();
            h.Domain = domain;
            h.Collection = collection;
            h.Gateway = gateway;
            h.Vpn = rdp.Vpn.SelectedIndex > 0 ? VpnChoicesSelected() : "";
            h.FullScreen = rdp.FullScreen.Checked;
            h.MultiMon = rdp.MultiMon.Checked;
            if (rdpEditIndex < 0)
                settings.Rdp.Add(h);
            if (!TrySave())
            {
                rdp.EditorMsg.ShowError("Не удалось сохранить настройки на диск.");
                return;
            }
            rdp.ShowEditor(false);
            rdp.Msg.ShowOk("Компьютер «" + h.Name + "» сохранён.");
            RefreshRdp();
            ProbeRdp(true);
        }

        string VpnChoicesSelected()
        {
            var choices = VpnChoices();
            var h = rdpEditIndex >= 0 ? settings.Rdp[rdpEditIndex] : null;
            if (h != null && h.Vpn.Length > 0 && !choices.Exists(x => string.Equals(x, h.Vpn, StringComparison.CurrentCultureIgnoreCase)))
                choices.Add(h.Vpn);
            int i = rdp.Vpn.SelectedIndex;
            return i > 0 && i < choices.Count ? choices[i] : "";
        }

        void DeleteRdp()
        {
            if (rdpEditIndex < 0)
                return;
            var h = settings.Rdp[rdpEditIndex];
            if (MessageBox.Show(this, "Удалить компьютер «" + h.Name + "»?", Program.AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            settings.Rdp.RemoveAt(rdpEditIndex);
            TrySave();
            rdp.ShowEditor(false);
            RefreshRdp();
        }

        // ---------- Доступность каналов и группы ----------

        ProbeResult ReachOf(Connection c)
        {
            ProbeResult r;
            return reach.TryGetValue(c.Server.Trim(), out r) ? r : null;
        }

        // Каналы одной группы в порядке списка (это и есть приоритет). Без группы — только само подключение.
        List<Connection> GroupOf(Connection c)
        {
            var list = new List<Connection>();
            if (c == null)
                return list;
            string g = c.Group.Trim();
            if (g.Length == 0)
            {
                list.Add(c);
                return list;
            }
            foreach (var x in settings.Connections)
                if (string.Equals(x.Group.Trim(), g, StringComparison.CurrentCultureIgnoreCase))
                    list.Add(x);
            return list;
        }

        // Группа с режимом «меньше задержка»: подключаемся к самому быстрому доступному каналу.
        bool IsLatencyGroup(Connection c)
        {
            var g = GroupOf(c);
            return g.Count > 1 && g.Exists(m => m.Balance == "latency");
        }

        // Самый быстрый доступный канал (по свежей проверке), кроме except.
        Connection FastestUp(List<Connection> members, Connection except)
        {
            Connection best = null;
            int bestRtt = int.MaxValue;
            foreach (var m in members)
            {
                if (m == except)
                    continue;
                var r = ReachOf(m);
                if (r != null && r.State == Reach.Up && (DateTime.Now - r.When).TotalMinutes < 3 && r.RttMs < bestRtt)
                {
                    best = m;
                    bestRtt = r.RttMs;
                }
            }
            return best;
        }

        // Перемерить каналы группы, у которых нет свежего результата (младше 30 с).
        async Task RefreshGroupAsync(List<Connection> members)
        {
            var tasks = new List<Task>();
            foreach (var m in members)
            {
                var r = ReachOf(m);
                if (r != null && r.State != Reach.Checking && (DateTime.Now - r.When).TotalSeconds < 30)
                    continue;
                tasks.Add(ProbeInto(m.Server.Trim()));
            }
            await Task.WhenAll(tasks);
        }

        async Task ProbeInto(string host)
        {
            ProbeResult result;
            try
            {
                result = await IkeProbe.CheckAsync(host);
            }
            catch (Exception)
            {
                result = new ProbeResult { State = Reach.Down, When = DateTime.Now };
            }
            if (IsDisposed)
                return;
            reach[host] = result;
            ApplyStatuses();
        }

        // Более приоритетный канал той же группы, который доступен прямо сейчас.
        Connection BetterAvailable(Connection c)
        {
            if (IsLatencyGroup(c))
                return null; // в этом режиме канал выбирается сам, не переспрашиваем
            var g = GroupOf(c);
            int idx = g.IndexOf(c);
            for (int i = 0; i < idx; i++)
            {
                var r = ReachOf(g[i]);
                if (r != null && r.State == Reach.Up && (DateTime.Now - r.When).TotalMinutes < 3)
                    return g[i];
            }
            return null;
        }

        // Следующий канал после неудачного: сначала доступный, иначе не помеченный недоступным, иначе просто следующий.
        Connection NextFallback(Connection c)
        {
            var g = GroupOf(c);
            if (IsLatencyGroup(c))
            {
                // Следующий по скорости из доступных, иначе любой не помеченный недоступным.
                var fastest = FastestUp(g, c);
                if (fastest != null)
                    return fastest;
                var other = g.Find(m => m != c && (ReachOf(m) == null || ReachOf(m).State != Reach.Down));
                return other ?? g.Find(m => m != c);
            }
            int idx = g.IndexOf(c);
            if (idx < 0 || idx + 1 >= g.Count)
                return null;
            Connection notDown = null;
            for (int i = idx + 1; i < g.Count; i++)
            {
                var r = ReachOf(g[i]);
                if (r != null && r.State == Reach.Up)
                    return g[i];
                if (notDown == null && (r == null || r.State != Reach.Down))
                    notDown = g[i];
            }
            return notDown ?? g[idx + 1];
        }

        void ProbeAll(bool force)
        {
            if (!force && (state != UiState.Disconnected || !Visible || WindowState == FormWindowState.Minimized))
                return;
            foreach (var c in settings.Connections)
            {
                string host = c.Server.Trim();
                if (host.Length == 0)
                    continue;
                var r = ReachOf(c);
                if (!force && r != null && r.State != Reach.Checking && (DateTime.Now - r.When).TotalSeconds < 20)
                    continue;
                ProbeHost(host);
            }
        }

        async void ProbeHost(string host)
        {
            if (!probing.Add(host))
                return;
            if (!reach.ContainsKey(host))
            {
                reach[host] = new ProbeResult { State = Reach.Checking, When = DateTime.Now };
                ApplyStatuses();
            }
            ProbeResult result;
            try
            {
                result = await IkeProbe.CheckAsync(host);
            }
            catch (Exception)
            {
                result = new ProbeResult { State = Reach.Down, When = DateTime.Now };
            }
            probing.Remove(host);
            if (IsDisposed)
                return;
            reach[host] = result;
            ApplyStatuses();
            MaybeSelectPrimary();
        }

        void ApplyStatuses()
        {
            for (int i = 0; i < settings.Connections.Count; i++)
            {
                var r = ReachOf(settings.Connections[i]);
                if (r == null || r.State == Reach.Unknown)
                    remote.Profile.SetStatus(i, "", Theme.Muted);
                else if (r.State == Reach.Checking)
                    remote.Profile.SetStatus(i, "проверка…", Theme.Muted);
                else if (r.State == Reach.Up)
                    remote.Profile.SetStatus(i, "доступен, " + r.RttMs + " мс", Theme.Ok);
                else
                    remote.Profile.SetStatus(i, r.NameNotFound ? "имя не найдено" : "недоступен", Theme.Err);
            }
        }

        // При запуске: если выбран резервный канал, а основной доступен — выбираем основной.
        // В режиме «меньше задержка» — выбираем самый быстрый канал группы.
        void MaybeSelectPrimary()
        {
            if (!autoPrimaryPending || state != UiState.Disconnected)
                return;
            var c = Current;
            var g = GroupOf(c);
            if (c != null && IsLatencyGroup(c))
            {
                if (g.Exists(m => ReachOf(m) == null || ReachOf(m).State == Reach.Checking))
                    return; // ждём результатов по всем каналам
                autoPrimaryPending = false;
                var fastest = FastestUp(g, null);
                if (fastest == null || fastest == c)
                    return;
                SelectConnection(fastest, false);
                remote.Msg.ShowOk("Выбран канал с наименьшей задержкой: «" + fastest.Name + "» (" + ReachOf(fastest).RttMs + " мс).");
                remote.ShowMode(CurrentPageMode());
                return;
            }
            if (c == null || g.Count < 2 || g[0] == c)
            {
                autoPrimaryPending = false;
                return;
            }
            var r = ReachOf(g[0]);
            if (r == null || r.State == Reach.Checking)
                return;
            autoPrimaryPending = false;
            if (r.State != Reach.Up)
                return;
            SelectConnection(g[0], false);
            remote.Msg.ShowOk("Основной канал «" + g[0].Name + "» доступен — выбран он.");
            remote.ShowMode(CurrentPageMode());
        }

        void OfferFallback(int code)
        {
            // Ошибки учётной записи на другом канале повторятся — резерв не предлагаем.
            if (code == 691 || code == 812 || code == 647 || code == 648 || code == 649)
                return;
            var next = active != null ? NextFallback(active) : null;
            if (next == null)
                return;
            fallbackTarget = next;
            remote.FallbackName = next.Name;
            remote.Fallback.Text = "Подключиться через «" + next.Name + "»";
            remote.ShowMode(CurrentPageMode());
            ProbeAll(true);
        }

        void HideFallback()
        {
            fallbackTarget = null;
            remote.FallbackName = null;
            remote.ShowMode(CurrentPageMode());
        }

        void ConnectFallback()
        {
            var t = fallbackTarget;
            if (t == null || state != UiState.Disconnected)
                return;
            SelectConnection(t, true);
            StartConnect(true);
        }

        void OnSavePasswordChanged()
        {
            if (updating)
                return;
            var c = Current;
            if (c == null)
                return;
            c.SavePassword = remote.SavePass.Checked;
            if (!c.SavePassword)
                c.Password = "";
            if (c.User.Length > 0)
                ShareCredentials(c);
            TrySave();
        }

        bool TrySave()
        {
            try
            {
                settings.Save();
                return true;
            }
            catch (Exception ex)
            {
                remote.Msg.ShowError("Не удалось сохранить настройки: " + ex.Message);
                return false;
            }
        }

        void EditConnection(int index)
        {
            if (index < 0 || index >= settings.Connections.Count)
            {
                NewConnection();
                return;
            }
            editIndex = index;
            var c = settings.Connections[index];
            updating = true;
            conns.Selector.SelectedIndex = index;
            updating = false;
            conns.Title.Text = "Настройка подключения";
            conns.ConnName.Text = c.Name;
            conns.Server.Text = c.Server;
            conns.Psk.Text = c.Psk;
            conns.Routes.Text = c.Routes;
            conns.Group.Text = c.Group;
            conns.Domain.Text = c.Domain;
            conns.Dns.Text = c.Dns;
            conns.DnsDomain.Text = c.DnsDomain;
            conns.Balance.SelectedIndex = c.Balance == "latency" ? 1 : 0;
            conns.User.Text = c.User;
            conns.Delete.Enabled = true;
            conns.Msg.Clear();
        }

        void NewConnection()
        {
            editIndex = -1;
            updating = true;
            conns.Selector.SelectedIndex = -1;
            updating = false;
            conns.Title.Text = "Новое подключение";
            foreach (var f in new[] { conns.ConnName, conns.Server, conns.Psk, conns.Routes, conns.User, conns.Group, conns.Domain, conns.Dns, conns.DnsDomain })
                f.Text = "";
            conns.Balance.SelectedIndex = 0;
            conns.Delete.Enabled = false;
            conns.Msg.Clear();
            if (conns.Visible)
                conns.ConnName.Box.Focus();
        }

        void SaveConnection()
        {
            string name = conns.ConnName.Text.Trim();
            string server = conns.Server.Text.Trim();
            string psk = conns.Psk.Text;

            if (name.Length == 0) { FieldError("Укажите название подключения.", conns.ConnName); return; }
            var same = settings.Find(name);
            if (same != null && settings.Connections.IndexOf(same) != editIndex)
            {
                FieldError("Подключение с таким названием уже есть.", conns.ConnName);
                return;
            }
            if (server.Length == 0) { FieldError("Укажите адрес VPN-сервера.", conns.Server); return; }
            if (psk.Length == 0) { FieldError("Укажите общий ключ IPsec (PSK).", conns.Psk); return; }
            List<string> routes;
            string bad;
            if (!RouteList.TryParse(conns.Routes.Text, out routes, out bad))
            {
                FieldError("Неверная сеть «" + bad + "». Пример: 192.168.88.0/24", conns.Routes);
                return;
            }

            string domainError = ProfileFile.CheckDomain(conns.Domain.Text.Trim());
            if (domainError != null) { FieldError("Домен: " + domainError, conns.Domain); return; }
            List<string> dnsServers;
            string dnsError = ProfileFile.ParseDns(conns.Dns.Text, out dnsServers);
            if (dnsError != null) { FieldError("DNS: " + dnsError, conns.Dns); return; }
            dnsError = ProfileFile.CheckDnsDomain(conns.DnsDomain.Text.Trim());
            if (dnsError != null) { FieldError(dnsError, conns.DnsDomain); return; }

            Connection c = editIndex >= 0 ? settings.Connections[editIndex] : new Connection();
            c.Name = name;
            c.Server = server;
            c.Psk = psk;
            c.Routes = string.Join(", ", routes);
            c.Group = conns.Group.Text.Trim();
            c.User = conns.User.Text.Trim();
            c.Domain = conns.Domain.Text.Trim();
            c.Dns = string.Join(", ", dnsServers);
            c.DnsDomain = conns.DnsDomain.Text.Trim();
            c.Balance = conns.Balance.SelectedIndex == 1 ? "latency" : "";
            // Режим выбора канала и домен — общие для всей группы.
            foreach (var m in GroupOf(c))
            {
                m.Balance = c.Balance;
                if (c.Domain.Length > 0 || m == c)
                    m.Domain = c.Domain;
                // DNS офиса один на все каналы группы.
                if (c.Dns.Length > 0 || m == c)
                    m.Dns = c.Dns;
                if (c.DnsDomain.Length > 0 || m == c)
                    m.DnsDomain = c.DnsDomain;
            }
            if (editIndex < 0)
            {
                settings.Connections.Add(c);
                editIndex = settings.Connections.Count - 1;
            }
            settings.Selected = c.Name;
            if (!TrySave())
            {
                conns.Msg.ShowError("Не удалось сохранить настройки на диск.");
                return;
            }

            RefreshConnectionLists();
            EditConnection(editIndex);
            if (state == UiState.Disconnected)
                SetIdle(null, false);
            remote.Msg.ShowOk("Подключение «" + c.Name + "» сохранено.");
            ProbeAll(true);
            remote.ShowMode(state == UiState.Disconnected ? PageMode.Idle : CurrentPageMode());
            ShowPage(0);
        }

        void FieldError(string message, FlatField field)
        {
            conns.Msg.ShowError(message);
            field.Box.Focus();
        }

        void CancelEditing()
        {
            if (settings.Connections.Count == 0)
            {
                NewConnection();
                return;
            }
            EditConnection(Math.Max(0, settings.Connections.IndexOf(Current)));
            ShowPage(0);
        }

        void DeleteConnection()
        {
            if (editIndex < 0)
                return;
            var c = settings.Connections[editIndex];
            if (state != UiState.Disconnected && active == c)
            {
                conns.Msg.ShowError("Это подключение сейчас используется. Сначала отключитесь.");
                return;
            }
            if (MessageBox.Show(this, "Удалить подключение «" + c.Name + "»?", Program.AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            settings.Connections.RemoveAt(editIndex);
            if (settings.Find(settings.Selected) == null)
                settings.Selected = settings.Connections.Count > 0 ? settings.Connections[0].Name : "";
            TrySave();
            RefreshConnectionLists();
            if (settings.Connections.Count > 0)
                EditConnection(0);
            else
                NewConnection();
            if (state == UiState.Disconnected)
                SetIdle(null, false);
        }

        // ---------- Состояния ----------

        PageMode CurrentPageMode()
        {
            switch (state)
            {
                case UiState.Connected: return PageMode.Connected;
                case UiState.Connecting:
                case UiState.Disconnecting: return PageMode.Connecting;
                default: return settings.Connections.Count == 0 ? PageMode.Empty : PageMode.Idle;
            }
        }

        string IdleUserName()
        {
            var c = Current;
            return c != null && c.User.Length > 0 ? c.User : Environment.UserName;
        }

        void SetIdle(string message, bool isError)
        {
            state = UiState.Disconnected;
            hConn = IntPtr.Zero;
            animTimer.Stop();
            fallbackTarget = null;
            remote.FallbackName = null;
            remote.Art.Mode = message != null && isError ? ArtMode.Error : ArtMode.Idle;
            if (message == null)
                remote.Msg.Clear();
            else if (isError)
                remote.Msg.ShowError(message);
            else
                remote.Msg.ShowOk(message);
            remote.ShowMode(CurrentPageMode());
            remote.Connect.Enabled = true;
            sidebar.SetUser(IdleUserName(), "Не подключено", Theme.Dots);
            UpdateTray();
            RefreshRdp();
        }

        void SetConnecting(string stage)
        {
            state = UiState.Connecting;
            remote.Art.Mode = ArtMode.Connecting;
            remote.BigStatus.Text = "Подключение к «" + active.Name + "»";
            remote.Stage.Text = stage;
            remote.Cancel.Enabled = true;
            remote.Msg.Clear();
            remote.ShowMode(PageMode.Connecting);
            animTimer.Start();
            sidebar.SetUser(activeUser, "Подключение…", Theme.Busy);
            UpdateTray();
        }

        void SetConnected()
        {
            state = UiState.Connected;
            animTimer.Stop();
            remote.Title.Text = "VPN подключён";
            remote.Info.Set("Подключение", active.Name, null);
            remote.Info.Set("VPN-сервер", active.Server, null);
            remote.Info.Set("IP-адрес", vpnIp ?? "получение…", null);
            remote.Info.Set("Логин", activeUser, null);
            remote.Info.Set("Длительность", lastDuration, null);
            remote.Info.Set("Получено", Fmt.Bytes(totalRx), null);
            remote.Info.Set("Отправлено", Fmt.Bytes(totalTx), null);
            remote.ShowMode(PageMode.Connected);
            remote.Disconnect.Enabled = true;
            sidebar.SetUser(activeUser, "Подключено", Theme.Ok);
            UpdateTray();
            RefreshRdp();
            ProbeRdp(true);
            CheckNet();
        }

        void SetBusy(string title)
        {
            state = UiState.Disconnecting;
            remote.Art.Mode = ArtMode.Connecting;
            remote.BigStatus.Text = title;
            remote.Stage.Text = "";
            remote.Cancel.Enabled = false;
            remote.ShowMode(PageMode.Connecting);
            animTimer.Start();
            sidebar.SetUser(activeUser, title, Theme.Busy);
            UpdateTray();
        }

        void Ui(Action action)
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            try { BeginInvoke(action); } catch (InvalidOperationException) { }
        }

        // ---------- Подключение / отключение ----------

        void StartConnect()
        {
            StartConnect(false);
        }

        bool balancing;

        // skipGuard — не спрашивать про основной канал и не выбирать канал заново (переход на резерв после ошибки).
        void StartConnect(bool skipGuard)
        {
            var c = Current;
            if (!skipGuard && state == UiState.Disconnected && c != null && IsLatencyGroup(c) &&
                remote.User.Text.Trim().Length > 0 && remote.Pass.Text.Length > 0)
            {
                BalanceThenConnect(c);
                return;
            }
            StartConnectCore(skipGuard);
        }

        // Перемерить каналы группы и подключиться к самому быстрому.
        async void BalanceThenConnect(Connection c)
        {
            if (balancing)
                return;
            balancing = true;
            remote.Connect.Enabled = false;
            remote.Msg.ShowOk("Выбираю канал с наименьшей задержкой…");
            remote.ShowMode(CurrentPageMode());
            try
            {
                var members = GroupOf(c);
                await RefreshGroupAsync(members);
                if (IsDisposed || state != UiState.Disconnected)
                    return;
                var fastest = FastestUp(members, null);
                if (fastest != null && fastest != c)
                    SelectConnection(fastest, true);
            }
            finally
            {
                balancing = false;
                remote.Connect.Enabled = true;
            }
            remote.Msg.Clear();
            StartConnectCore(true);
        }

        void StartConnectCore(bool skipGuard)
        {
            if (state != UiState.Disconnected)
                return;
            var c = Current;
            if (c == null)
            {
                NewConnection();
                ShowPage(2);
                return;
            }
            string user = remote.User.Text.Trim();
            string pass = remote.Pass.Text;
            if (user.Length == 0) { LoginError("Укажите логин.", remote.User); return; }
            if (pass.Length == 0) { LoginError("Укажите пароль.", remote.Pass); return; }

            var better = skipGuard ? null : BetterAvailable(c);
            if (better != null)
            {
                var answer = MessageBox.Show(this,
                    "Основной канал «" + better.Name + "» сейчас доступен.\n\n" +
                    "Да — подключиться через «" + better.Name + "».\n" +
                    "Нет — всё равно подключиться через «" + c.Name + "».",
                    Program.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel)
                    return;
                if (answer == DialogResult.Yes)
                {
                    SelectConnection(better, true);
                    c = better;
                }
            }

            if (c.Server.Length == 0 || c.Psk.Length == 0)
            {
                remote.Msg.ShowError("В настройках подключения не указан сервер или общий ключ.");
                remote.ShowMode(PageMode.Idle);
                return;
            }
            List<string> routes;
            string bad;
            if (!RouteList.TryParse(c.Routes, out routes, out bad))
            {
                remote.Msg.ShowError("В настройках подключения неверная сеть «" + bad + "».");
                remote.ShowMode(PageMode.Idle);
                return;
            }

            c.User = user;
            c.SavePassword = remote.SavePass.Checked;
            c.Password = c.SavePassword ? pass : "";
            ShareCredentials(c);
            TrySave();

            active = c;
            activeUser = user;
            cancelRequested = false;
            ResetStats();
            SetConnecting("Настройка VPN-подключения Windows…");

            List<string> dns;
            string dnsError = ProfileFile.ParseDns(c.Dns, out dns);
            if (dnsError != null)
            {
                remote.Msg.ShowError("В настройках подключения: " + dnsError);
                remote.ShowMode(PageMode.Idle);
                return;
            }
            string server = c.Server, psk = c.Psk, dnsDomain = c.DnsDomain;
            var t = new Thread(delegate () { ConnectWorker(server, psk, user, pass, routes, dns, dnsDomain); });
            t.IsBackground = true;
            t.Start();
        }

        void LoginError(string message, FlatField field)
        {
            remote.Msg.ShowError(message);
            remote.ShowMode(PageMode.Idle);
            field.Box.Focus();
        }

        void ConnectWorker(string server, string psk, string user, string pass, List<string> routes, List<string> dns, string dnsDomain)
        {
            try
            {
                VpnProfile.Apply(server, psk, routes, dns, dnsDomain);
            }
            catch (Exception ex)
            {
                string msg = "Не удалось создать VPN-подключение в Windows: " + ex.Message;
                Ui(delegate { SetIdle(msg, true); });
                return;
            }

            if (cancelRequested)
            {
                Ui(FinishCancel);
                return;
            }

            Ui(delegate { remote.Stage.Text = "Соединение с сервером " + server + "…"; });

            int finalState = 0, finalError = 0;
            var done = new ManualResetEvent(false);
            Ras.DialCallback callback = delegate (uint msg, int st, int err)
            {
                if (err != 0 || st == Ras.RASCS_Connected || st == Ras.RASCS_Disconnected)
                {
                    finalState = st;
                    finalError = err;
                    done.Set();
                }
                else
                {
                    Ui(delegate
                    {
                        if (state == UiState.Connecting)
                            remote.Stage.Text = Errors.Stage(st, server);
                    });
                }
            };

            string phonebook = File.Exists(Ras.UserPhonebook) ? Ras.UserPhonebook : null;
            IntPtr h;
            int r = Ras.Dial(phonebook, VpnProfile.EntryName, user, pass, callback, out h);
            if (r == 0)
            {
                while (!done.WaitOne(200) && !cancelRequested)
                {
                }
                if (!cancelRequested && finalError == 0 && finalState == Ras.RASCS_Connected)
                {
                    GC.KeepAlive(callback);
                    Ui(delegate { OnConnected(h); });
                    return;
                }
                r = finalError;
            }

            Ras.HangUp(h);
            GC.KeepAlive(callback);
            if (cancelRequested || r == Ras.ERROR_USER_DISCONNECTION)
                Ui(FinishCancel);
            else if (r == 0)
                Ui(delegate { SetIdle("Сервер разорвал соединение.", true); OfferFallback(0); });
            else
                Ui(delegate { SetIdle(Errors.Explain(r), true); OfferFallback(r); });
        }

        void FinishCancel()
        {
            SetIdle(null, false);
            if (exitAfterDisconnect)
            {
                allowClose = true;
                Close();
            }
        }

        void OnConnected(IntPtr h)
        {
            if (state != UiState.Connecting)
            {
                ThreadPool.QueueUserWorkItem(delegate { Ras.HangUp(h); });
                return;
            }
            hConn = h;
            SetConnected();
            Poll();
            LaunchPendingRdp();
            if (!Visible || WindowState == FormWindowState.Minimized)
                Balloon(3000, "VPN подключён", "«" + active.Name + "» — " + active.Server, ToolTipIcon.Info);
        }

        // Поток подключения сам увидит флаг, разорвёт набор и вызовет FinishCancel.
        void CancelConnect()
        {
            cancelRequested = true;
            SetBusy("Отмена…");
        }

        void Disconnect()
        {
            if (state != UiState.Connected)
                return;
            IntPtr h = hConn;
            string duration = lastDuration;
            SetBusy("Отключение…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                Ras.HangUp(h);
                Ui(delegate
                {
                    SetIdle("Соединение завершено. Длительность сессии " + duration + ".", false);
                    if (exitAfterDisconnect)
                    {
                        allowClose = true;
                        Close();
                    }
                });
            });
        }

        // ---------- Опрос состояния и статистики ----------

        void Poll()
        {
            int st, err;
            if (state == UiState.Connected)
            {
                int r = Ras.GetConnectStatus(hConn, out st, out err);
                if (r != 0 || st != Ras.RASCS_Connected)
                {
                    IntPtr h = hConn;
                    ThreadPool.QueueUserWorkItem(delegate { Ras.HangUp(h); });
                    string why = err != 0 ? Errors.Explain(err) : "Связь с сервером потеряна или соединение разорвано в Windows.";
                    SetIdle("Соединение разорвано. " + why, true);
                    OfferFallback(err);
                    Balloon(5000, "VPN отключён", why, ToolTipIcon.Warning);
                    return;
                }
                UpdateStats();
            }
            else if (state == UiState.Disconnected)
            {
                // Подхватываем соединение, поднятое ранее (например, до перезапуска программы).
                IntPtr h = Ras.FindConnection(VpnProfile.EntryName);
                if (h != IntPtr.Zero && Ras.GetConnectStatus(h, out st, out err) == 0 && st == Ras.RASCS_Connected)
                {
                    hConn = h;
                    active = Current ?? new Connection { Name = VpnProfile.EntryName, Server = "—" };
                    activeUser = active.User.Length > 0 ? active.User : "—";
                    ResetStats();
                    SetConnected();
                    UpdateStats();
                }
            }
        }

        void ResetStats()
        {
            statsPrimed = false;
            totalRx = totalTx = 0;
            vpnIp = null;
            lastDuration = "00:00:00";
            remote.Graph.Reset();
        }

        void UpdateStats()
        {
            uint tx, rx, dur;
            if (Ras.GetStatistics(hConn, out tx, out rx, out dur) != 0)
                return;

            double secs = statsClock.Elapsed.TotalSeconds;
            statsClock.Restart();
            double sRx = 0, sTx = 0;
            if (!statsPrimed)
            {
                totalRx = rx;
                totalTx = tx;
                statsPrimed = true;
            }
            else
            {
                uint dRx = unchecked(rx - lastRx);
                uint dTx = unchecked(tx - lastTx);
                totalRx += dRx;
                totalTx += dTx;
                if (secs > 0.2)
                {
                    sRx = dRx / secs;
                    sTx = dTx / secs;
                }
                remote.Graph.Push(sRx, sTx);
            }
            lastRx = rx;
            lastTx = tx;
            lastDuration = Fmt.Duration(dur);

            if (vpnIp == null)
                vpnIp = Ras.GetIpAddress(hConn);
            remote.Info.Set("IP-адрес", vpnIp ?? "получение…", null);
            remote.Info.Set("Длительность", lastDuration, null);
            remote.Info.Set("Получено", Fmt.Bytes(totalRx), "↓ " + Fmt.Speed(sRx));
            remote.Info.Set("Отправлено", Fmt.Bytes(totalTx), "↑ " + Fmt.Speed(sTx));
            UpdateTrayText();
        }

        // ---------- Трей ----------

        Icon trayIcon;

        void UpdateTray()
        {
            Color dot;
            switch (state)
            {
                case UiState.Connected:
                    dot = Theme.Ok;
                    trayToggle.Text = "Отключить VPN";
                    trayToggle.Enabled = true;
                    break;
                case UiState.Disconnected:
                    dot = Color.Empty;
                    trayToggle.Text = "Подключить VPN";
                    trayToggle.Enabled = settings.Connections.Count > 0;
                    break;
                default:
                    dot = Theme.Busy;
                    trayToggle.Text = "Подключение…";
                    trayToggle.Enabled = false;
                    break;
            }
            var old = trayIcon;
            trayIcon = MakeTrayIcon(dot);
            tray.Icon = trayIcon;
            if (old != null)
                old.Dispose();
            UpdateTrayText();
        }

        void UpdateTrayText()
        {
            string text;
            switch (state)
            {
                case UiState.Connected: text = "подключено, " + lastDuration; break;
                case UiState.Disconnected: text = "не подключено"; break;
                default: text = "подключение…"; break;
            }
            text = Program.AppName + " — " + text;
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        static Icon MakeTrayIcon(Color dot)
        {
            int size = SystemInformation.SmallIconSize.Width;
            using (var bmp = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Theme.AppIcon(g, new RectangleF(0, 0, size, size));
                    if (dot != Color.Empty)
                    {
                        float d = size * 0.5f;
                        g.FillEllipse(Brushes.White, size - d - 1, size - d - 1, d + 1, d + 1);
                        using (var b = new SolidBrush(dot))
                            g.FillEllipse(b, size - d, size - d, d - 1, d - 1);
                    }
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone();
                }
                finally
                {
                    DestroyIcon(h);
                }
            }
        }

        void RestoreWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
        }

        void OnTrayToggle()
        {
            if (state == UiState.Connected)
            {
                Disconnect();
            }
            else if (state == UiState.Disconnected)
            {
                RestoreWindow();
                ShowPage(0);
                StartConnect();
            }
        }

        void ExitApp()
        {
            switch (state)
            {
                case UiState.Connected:
                {
                    RestoreWindow();
                    var res = MessageBox.Show(this,
                        "VPN подключён.\n\nДа — отключить VPN и выйти.\nНет — выйти, оставив VPN подключённым.",
                        Program.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (res == DialogResult.Cancel)
                        return;
                    if (res == DialogResult.Yes)
                    {
                        exitAfterDisconnect = true;
                        Disconnect();
                        return;
                    }
                    break;
                }
                case UiState.Connecting:
                    exitAfterDisconnect = true;
                    CancelConnect();
                    return;
                case UiState.Disconnecting:
                    exitAfterDisconnect = true;
                    return;
            }
            allowClose = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Крестик при активном VPN сворачивает в трей, как у FortiClient.
            if (!allowClose && e.CloseReason == CloseReason.UserClosing && state != UiState.Disconnected)
            {
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    Balloon(4000, Program.AppName,
                        "VPN остаётся подключённым. Программа работает в области уведомлений.", ToolTipIcon.Info);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        // ---------- Обновление программы (релизы на GitHub) ----------

        async void CheckUpdates(bool manual)
        {
            if (updateChecking || updateDownload != null) return;
            updateChecking = true;
            about.CheckButton.Enabled = false;
            if (manual) about.SetUpdateStatus("Проверка обновлений…", Theme.Label);
            try
            {
                UpdateInfo u = await Updater.CheckAsync();
                if (Updater.IsNewer(u.Version, Program.Version))
                {
                    update = u;
                    ShowUpdate();
                    if (!manual && updateNotified != u.Version)
                    {
                        updateNotified = u.Version;
                        balloonIsUpdate = true;
                        tray.ShowBalloonTip(6000, "Доступна версия " + u.Version,
                            "Нажмите, чтобы обновить " + Program.AppName + ".", ToolTipIcon.Info);
                    }
                }
                else
                {
                    update = null;
                    ShowUpdate();
                    about.SetUpdateStatus("Установлена последняя версия (" + Program.Version + ")", Theme.Ok);
                }
            }
            catch (Exception ex)
            {
                // Автоматическая проверка без интернета молчит; ошибку показываем, только если нажали «Проверить».
                if (manual) about.SetUpdateStatus("Не удалось проверить: " + Updater.Explain(ex), Theme.Err);
            }
            finally
            {
                updateChecking = false;
                about.CheckButton.Enabled = true;
            }
        }

        // Уведомление в трее (щелчок по нему открывает обновление, только если оно про обновление).
        void Balloon(int ms, string title, string text, ToolTipIcon icon)
        {
            balloonIsUpdate = false;
            tray.ShowBalloonTip(ms, title, text, icon);
        }

        void ShowUpdate()
        {
            about.UpdateButton.Visible = update != null;
            about.UpdateProgress.Visible = false;
            if (update == null)
            {
                about.UpdateNotes.Text = "";
                sidebar.SetUpdate(null);
                return;
            }
            about.UpdateButton.Text = "Обновить до " + update.Version;
            about.SetUpdateStatus("Доступна версия " + update.Version + " (у вас " + Program.Version + ")", Theme.Primary);
            about.UpdateNotes.Text = (update.Notes.Length > 0 ? update.Notes + "\n" : "") +
                                     "Размер " + Updater.Mb(update.Size) + ". VPN во время обновления не отключается.";
            sidebar.SetUpdate("Доступна версия " + update.Version);
        }

        async void InstallUpdate()
        {
            UpdateInfo u = update;
            if (u == null || updateChecking) return;
            if (state == UiState.Connecting || state == UiState.Disconnecting)
            {
                about.SetUpdateStatus("Дождитесь окончания подключения или отключения VPN", Theme.Busy);
                return;
            }
            updateDownload = new CancellationTokenSource();
            about.CheckButton.Enabled = false;
            about.UpdateButton.Text = "Отменить";
            about.UpdateProgress.Value = 0;
            about.UpdateProgress.Visible = true;
            int shown = -1;
            Action<long, long> progress = (done, total) => Ui(() =>
            {
                int pct = (int)(done * 100 / Math.Max(1, total));
                if (pct == shown || updateDownload == null) return;
                shown = pct;
                about.UpdateProgress.Value = (double)done / total;
                about.SetUpdateStatus("Загрузка версии " + u.Version + ": " + pct + "%  ·  " + Updater.Mb(done) + " из " + Updater.Mb(total), Theme.Label);
                sidebar.SetUpdate("Обновление: " + pct + "%");
            });
            about.SetUpdateStatus("Загрузка версии " + u.Version + "…", Theme.Label);
            try
            {
                string file = await Updater.DownloadAsync(u, progress, updateDownload.Token);
                updateDownload = null;
                about.UpdateProgress.Value = 1;
                about.SetUpdateStatus("Установка версии " + u.Version + "…", Theme.Label);
                sidebar.SetUpdate("Установка " + u.Version + "…");
                if (!Updater.Install(file))
                {
                    ShowUpdate();
                    about.SetUpdateStatus("Установка отменена: нужно разрешить установщику изменения (права администратора)", Theme.Busy);
                    return;
                }
                // Установщик закроет программу, обновит её и запустит снова; VPN остаётся как есть.
                allowClose = true;
                Close();
            }
            catch (OperationCanceledException)
            {
                updateDownload = null;
                ShowUpdate();
                about.SetUpdateStatus("Загрузка отменена", Theme.Label);
            }
            catch (Exception ex)
            {
                updateDownload = null;
                ShowUpdate();
                about.SetUpdateStatus("Не удалось обновить: " + Updater.Explain(ex), Theme.Err);
            }
            finally
            {
                about.CheckButton.Enabled = true;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            pollTimer.Stop();
            updateTimer.Stop();
            animTimer.Stop();
            netTimer.Stop();
            NetworkChange.NetworkAddressChanged -= netAddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= netAvailabilityChanged;
            tray.Visible = false;
            tray.Dispose();
            // Профиль Windows создаётся заново при каждом подключении; после отключения его не оставляем.
            if (state == UiState.Disconnected)
                VpnProfile.Remove(false);
            base.OnFormClosed(e);
        }
    }

    internal static class Errors
    {
        public static string Explain(int code)
        {
            string hint = null;
            switch (code)
            {
                case 691: hint = "Неверный логин или пароль."; break;
                case 718: hint = "Сервер не ответил на этапе PPP. Проверьте профиль PPP на сервере."; break;
                case 720: hint = "Не удалось согласовать протоколы PPP с сервером (проверьте профиль и пул адресов на сервере)."; break;
                case 734: hint = "Протокол PPP прерван сервером."; break;
                case 787:
                case 788:
                case 789: hint = "Не удалось установить IPsec: сервер недоступен, неверный общий ключ (PSK) " +
                                 "или на сервере не включён IPsec для L2TP."; break;
                case 800: hint = "Не удалось установить VPN-соединение. Проверьте адрес сервера и его доступность."; break;
                case 809: hint = "Сервер не отвечает по L2TP/IPsec. Проверьте адрес и фаервол; " +
                                 "если программа только что установлена — перезагрузите компьютер."; break;
                case 812: hint = "Подключение запрещено политикой сервера для этого пользователя."; break;
                case 868: hint = "Не удаётся найти сервер с таким именем. Проверьте адрес сервера."; break;
            }
            string text = hint ?? Ras.ErrorString(code);
            return text + " (код " + code + ")";
        }

        public static string Stage(int rasState, string server)
        {
            if (rasState <= 1) return "Открытие порта…";
            if (rasState <= 4) return "Соединение с сервером " + server + " (IPsec)…";
            if (rasState <= 13 || rasState == 19) return "Проверка логина и пароля…";
            return "Настройка сети…";
        }
    }
}
