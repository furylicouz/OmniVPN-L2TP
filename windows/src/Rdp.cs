using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace L2TPConnect
{
    // Компьютер для подключения к удалённому рабочему столу.
    internal sealed class RdpHost
    {
        public string Name = "";
        public string Host = "";      // имя или IP, можно с портом: 192.168.20.25:3390
        public string User = "";      // необязательно, например CORP\user
        public string Vpn = "";       // группа или название VPN, без которого компьютер недоступен
        public string Collection = "";// alias коллекции RDS (тогда Host — брокер подключений)
        public string Gateway = "";   // шлюз RD Gateway, например rdg.example.ru (можно с портом)
        public string Domain = "";    // домен для логина (CORP); пусто — берётся домен VPN
        public bool FullScreen = true;
        public bool MultiMon;

        // Что проверять на доступность: шлюз (HTTPS, 443), если он задан, иначе сам компьютер или брокер.
        public string ProbeKey { get { return Gateway.Length > 0 ? "gw:" + Gateway : Host; } }
        public string ProbeHost { get { return Gateway.Length > 0 ? SplitHost(Gateway) : HostName; } }
        public int ProbePort { get { return Gateway.Length > 0 ? SplitPort(Gateway, 443) : Port; } }

        static string SplitHost(string s)
        {
            int i = s.LastIndexOf(':');
            return i > 0 ? s.Substring(0, i) : s;
        }

        static int SplitPort(string s, int byDefault)
        {
            int i = s.LastIndexOf(':');
            int p;
            return i > 0 && int.TryParse(s.Substring(i + 1), out p) ? p : byDefault;
        }

        // null — имя коллекции в порядке, иначе текст ошибки. Alias коллекции — без пробелов.
        public static string CheckCollection(string alias)
        {
            if (alias.Length == 0)
                return null;
            if (alias.IndexOfAny(" \t'\"`$;|&<>\r\n:/\\".ToCharArray()) >= 0)
                return "имя коллекции — это её alias без пробелов (см. Get-RDSessionCollection).";
            return null;
        }

        public string HostName
        {
            get { int i = Host.LastIndexOf(':'); return i > 0 ? Host.Substring(0, i) : Host; }
        }

        public int Port
        {
            get
            {
                int i = Host.LastIndexOf(':');
                int p;
                return i > 0 && int.TryParse(Host.Substring(i + 1), out p) ? p : 3389;
            }
        }

        // null — адрес в порядке, иначе текст ошибки.
        public static string CheckAddress(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return "не указан адрес компьютера.";
            if (host.IndexOfAny(" \t'\"`$;|&<>\r\n".ToCharArray()) >= 0)
                return "недопустимые символы в адресе.";
            int i = host.LastIndexOf(':');
            int port;
            if (i == 0 || (i > 0 && (!int.TryParse(host.Substring(i + 1), out port) || port < 1 || port > 65535)))
                return "неверный порт в адресе (пример: 192.168.20.25:3390).";
            return null;
        }
    }

    internal static class RdpLauncher
    {
        // Пишет .rdp во временную папку и открывает его штатным клиентом Windows (mstsc).
        // Пароль не передаём: mstsc сам спросит и предложит «Запомнить меня».
        // Логин для .rdp: «user» + домен CORP -> «CORP\user»; «CORP\user» и «user@corp.local» не меняются.
        public static string FullUser(string user, string domain)
        {
            if (user.Length == 0 || domain.Length == 0 || user.IndexOf('\\') >= 0 || user.IndexOf('@') >= 0)
                return user;
            return domain + "\\" + user;
        }

        public static void Launch(RdpHost h, string domain)
        {
            string file = WriteFile(h, domain);
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mstsc.exe"), "\"" + file + "\"");
            psi.UseShellExecute = false;
            Process.Start(psi).Dispose();
        }

        public static string WriteFile(RdpHost h, string domain)
        {
            string dir = Path.Combine(Path.GetTempPath(), "OmniVPNConnect");
            Directory.CreateDirectory(dir);
            string name = h.Name;
            foreach (char ch in Path.GetInvalidFileNameChars())
                name = name.Replace(ch, '_');
            string file = Path.Combine(dir, name + ".rdp");

            var sb = new StringBuilder();
            sb.Append("full address:s:").Append(h.Host).Append("\r\n");
            if (h.Collection.Length > 0)
            {
                // Подключение к коллекции RDS через брокер.
                sb.Append("loadbalanceinfo:s:tsv://MS Terminal Services Plugin.1.").Append(h.Collection).Append("\r\n");
                sb.Append("use redirection server name:i:1\r\n");
            }
            if (h.Gateway.Length > 0)
            {
                // Через шлюз RD Gateway; один логин и пароль для шлюза и компьютера.
                sb.Append("gatewayhostname:s:").Append(h.Gateway).Append("\r\n");
                sb.Append("gatewayusagemethod:i:1\r\n");
                sb.Append("gatewayprofileusagemethod:i:1\r\n");
                sb.Append("gatewaycredentialssource:i:0\r\n");
                sb.Append("promptcredentialonce:i:1\r\n");
            }
            // Домен только внутри логина: с отдельной строкой domain:s: клиенты RDP склеивают их в «CORP\CORP\user».
            if (h.User.Length > 0)
                sb.Append("username:s:").Append(FullUser(h.User, domain)).Append("\r\n");
            else if (domain.Length > 0)
                sb.Append("domain:s:").Append(domain).Append("\r\n");
            sb.Append("screen mode id:i:").Append(h.FullScreen ? 2 : 1).Append("\r\n");
            sb.Append("use multimon:i:").Append(h.MultiMon ? 1 : 0).Append("\r\n");
            sb.Append("authentication level:i:2\r\n");
            sb.Append("prompt for credentials:i:0\r\n");
            sb.Append("redirectclipboard:i:1\r\n");
            sb.Append("redirectprinters:i:1\r\n");
            File.WriteAllText(file, sb.ToString(), Encoding.Unicode);
            return file;
        }

        // Доступность: открывается ли TCP-порт RDP (обычно 3389), а при шлюзе — порт шлюза (443).
        public static async Task<ProbeResult> CheckAsync(RdpHost h)
        {
            var result = new ProbeResult { State = Reach.Down, When = DateTime.Now };
            using (var tcp = new TcpClient())
            {
                var sw = Stopwatch.StartNew();
                Task connect;
                try
                {
                    connect = tcp.ConnectAsync(h.ProbeHost, h.ProbePort);
                }
                catch (Exception)
                {
                    return result;
                }
                var done = await Task.WhenAny(connect, Task.Delay(1500));
                if (done == connect && !connect.IsFaulted && tcp.Connected)
                {
                    result.State = Reach.Up;
                    result.RttMs = (int)sw.ElapsedMilliseconds;
                }
                else if (connect.IsFaulted)
                {
                    var se = connect.Exception.GetBaseException() as SocketException;
                    result.NameNotFound = se != null && (se.SocketErrorCode == SocketError.HostNotFound || se.SocketErrorCode == SocketError.NoData);
                }
                else
                {
                    var observed = connect.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                }
                result.When = DateTime.Now;
                return result;
            }
        }
    }

    // Список компьютеров: строка = значок, название, адрес; справа статус и «изменить».
    internal sealed class RdpList : PaintedControl
    {
        public sealed class Row
        {
            public RdpHost Host;
            public string DisplayUser = "";
            public string Status = "";
            public Color StatusColor = Theme.Muted;
        }

        public readonly List<Row> Rows = new List<Row>();
        int hover = -1;
        bool hoverEdit;
        readonly Font nameFont = new Font("Segoe UI Semibold", 10.5f);

        public event Action<int> RowClicked;
        public event Action<int> EditClicked;

        public RdpList()
        {
            Cursor = Cursors.Hand;
        }

        float S { get { using (var g = CreateGraphics()) return Theme.Scale(g); } }
        public int RowHeight { get { return (int)(58 * S); } }

        int HitRow(Point p) { int i = p.Y / RowHeight; return i >= 0 && i < Rows.Count ? i : -1; }
        bool HitEdit(Point p) { return p.X > Width - (int)(44 * S); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitRow(e.Location);
            bool he = h >= 0 && HitEdit(e.Location);
            if (h != hover || he != hoverEdit)
            {
                hover = h;
                hoverEdit = he;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = HitRow(e.Location);
            if (i >= 0 && e.Button == MouseButtons.Left)
            {
                if (HitEdit(e.Location)) { if (EditClicked != null) EditClicked(i); }
                else if (RowClicked != null) RowClicked(i);
            }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = Theme.Scale(g);
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int rh = (int)(58 * s);
            const TextFormatFlags one = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;

            for (int i = 0; i < Rows.Count; i++)
            {
                var row = Rows[i];
                int y = i * rh;
                if (i == hover)
                    using (var b = new SolidBrush(Color.FromArgb(0xF3, 0xF7, 0xFB)))
                        g.FillRectangle(b, 0, y, Width, rh);
                using (var pen = new Pen(Theme.CardBorder))
                    g.DrawLine(pen, 0, y + rh - 1, Width, y + rh - 1);

                float r = 19 * s, cx = 30 * s, cy = y + rh / 2f;
                using (var b = new SolidBrush(Theme.ArtBack))
                    g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
                Theme.Glyph(g, Theme.GDesktop, 18 * s, Theme.Blue, new RectangleF(cx - r, cy - r, 2 * r, 2 * r));

                int tx = (int)(60 * s);
                int statusW = (int)(130 * s), editW = (int)(44 * s);
                int textW = Width - tx - statusW - editW;
                TextRenderer.DrawText(g, row.Host.Name, nameFont, new Rectangle(tx, y + (int)(8 * s), textW, (int)(22 * s)), Theme.Text, one);
                string sub = row.Host.Host +
                             (row.Host.Collection.Length > 0 ? "  ·  коллекция " + row.Host.Collection : "") +
                             (row.DisplayUser.Length > 0 ? "  ·  " + row.DisplayUser : "") +
                             (row.Host.Gateway.Length > 0 ? "  ·  шлюз " + row.Host.Gateway : "") +
                             (row.Host.Vpn.Length > 0 ? "  ·  через VPN «" + row.Host.Vpn + "»" : "");
                TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(tx, y + (int)(30 * s), textW, (int)(18 * s)), Theme.Muted, one);

                if (row.Status.Length > 0)
                {
                    int sx = Width - editW - statusW + (int)(8 * s);
                    float dot = 7 * s;
                    using (var b = new SolidBrush(row.StatusColor))
                        g.FillEllipse(b, sx, cy - dot / 2, dot, dot);
                    TextRenderer.DrawText(g, row.Status, Theme.Small,
                        new Rectangle(sx + (int)(12 * s), y, statusW - (int)(20 * s), rh), row.StatusColor, one);
                }
                Color editColor = i == hover && hoverEdit ? Theme.PrimaryDown : Theme.Muted;
                Theme.Glyph(g, Theme.GEdit, 14 * s, editColor, new RectangleF(Width - editW, y, editW, rh));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                nameFont.Dispose();
            base.Dispose(disposing);
        }
    }

    // Вкладка «Удалённые ПК»: список компьютеров и форма добавления/изменения.
    internal sealed class RdpPage : Panel
    {
        public readonly Label Title;
        public readonly GlyphButton Add = new GlyphButton(Theme.GAdd, "Добавить");
        public readonly Panel ListHost = new Panel();
        public readonly RdpList Computers = new RdpList();
        public readonly MessageBar Msg = new MessageBar();
        public readonly FlatButton AddFirst = new FlatButton("Добавить компьютер", false);
        readonly Label emptyText;
        bool empty = true;

        // Форма
        public readonly Panel Editor = new Panel();
        public readonly Label EditorTitle;
        public readonly FlatField HostName = new FlatField(false);
        public readonly FlatField Address = new FlatField(false);
        public readonly FlatField User = new FlatField(false);
        public readonly FlatField Domain = new FlatField(false);
        public readonly FlatField Collection = new FlatField(false);
        public readonly FlatField Gateway = new FlatField(false);
        public readonly FlatSelect Vpn = new FlatSelect();
        public readonly CheckBox FullScreen = new CheckBox();
        public readonly CheckBox MultiMon = new CheckBox();
        public readonly MessageBar EditorMsg = new MessageBar();
        public readonly GlyphButton Delete = new GlyphButton(Theme.GDelete, "Удалить");
        public readonly FlatButton CancelButton = new FlatButton("Отмена", true);
        public readonly FlatButton SaveButton = new FlatButton("Сохранить", false);

        public RdpPage()
        {
            BackColor = Color.White;
            const ContentAlignment left = ContentAlignment.MiddleLeft;

            Title = PageKit.Label(this, "Удалённые рабочие столы", new Rectangle(30, 16, 400, 44), left, Theme.PageTitle, Theme.Title);
            PageKit.Add(this, Add, new Rectangle(450, 26, 104, 28));
            ListHost.AutoScroll = true;
            ListHost.BackColor = Color.White;
            PageKit.Add(this, ListHost, new Rectangle(14, 70, 540, 384));
            Computers.Bounds = new Rectangle(0, 0, 520, 10);
            ListHost.Controls.Add(Computers);
            PageKit.Add(this, Msg, new Rectangle(30, 460, 508, 48));

            emptyText = PageKit.Label(this, "Добавьте компьютеры, к которым сотрудники подключаются по RDP.\n" +
                                            "Клик по компьютеру откроет «Подключение к удалённому рабочему столу».",
                new Rectangle(10, 170, 548, 60), ContentAlignment.MiddleCenter, Theme.Body, Theme.Label);
            PageKit.Add(this, AddFirst, new Rectangle(184, 248, 200, 38));

            // Форма поверх списка
            Editor.BackColor = Color.White;
            PageKit.Add(this, Editor, new Rectangle(0, 0, 568, 518));
            EditorTitle = PageKit.Label(Editor, "Новый компьютер", new Rectangle(30, 16, 500, 44), left, Theme.PageTitle, Theme.Title);
            int y = 72;
            PageKit.Label(Editor, "Название", new Rectangle(40, y, 160, 30), left, Theme.BodySemibold, Theme.Label);
            PageKit.Add(Editor, HostName, new Rectangle(210, y, 318, 30));
            y += 40;
            PageKit.Label(Editor, "Адрес", new Rectangle(40, y, 160, 30), left, Theme.BodySemibold, Theme.Label);
            PageKit.Add(Editor, Address, new Rectangle(210, y, 318, 30));
            y += 40;
            PageKit.Label(Editor, "Логин", new Rectangle(40, y, 160, 30), left, Theme.BodySemibold, Theme.Label);
            PageKit.Add(Editor, User, new Rectangle(210, y, 170, 30));
            PageKit.Label(Editor, "Домен", new Rectangle(386, y, 64, 30), ContentAlignment.MiddleRight, Theme.Body, Theme.Label);
            PageKit.Add(Editor, Domain, new Rectangle(456, y, 72, 30));
            y += 40;
            PageKit.Label(Editor, "Коллекция RDS", new Rectangle(40, y, 160, 30), left, Theme.BodySemibold, Theme.Label);
            PageKit.Add(Editor, Collection, new Rectangle(210, y, 318, 30));
            y += 40;
            PageKit.Label(Editor, "Шлюз RD Gateway", new Rectangle(40, y, 160, 30), left, Theme.BodySemibold, Theme.Label);
            PageKit.Add(Editor, Gateway, new Rectangle(210, y, 318, 30));
            y += 40;
            PageKit.Label(Editor, "Нужен VPN", new Rectangle(40, y, 160, 30), left, Theme.BodySemibold, Theme.Label);
            PageKit.Add(Editor, Vpn, new Rectangle(210, y, 318, 30));
            y += 40;
            FullScreen.Text = "Во весь экран";
            MultiMon.Text = "Все мониторы";
            foreach (var cb in new[] { FullScreen, MultiMon })
            {
                cb.Font = Theme.Body;
                cb.ForeColor = Theme.Label;
                cb.AutoSize = true;
            }
            PageKit.Add(Editor, FullScreen, new Rectangle(211, y, 150, 24));
            PageKit.Add(Editor, MultiMon, new Rectangle(370, y, 150, 24));
            y += 26;
            PageKit.Label(Editor, "Пароль программа не хранит — его спросит и запомнит RDP-клиент Windows.",
                new Rectangle(210, y, 318, 36), ContentAlignment.TopLeft, Theme.Small, Theme.Muted);
            PageKit.Add(Editor, EditorMsg, new Rectangle(40, 412, 488, 46));
            PageKit.Add(Editor, Delete, new Rectangle(40, 469, 90, 28));
            Delete.Color = Theme.Err;
            PageKit.Add(Editor, CancelButton, new Rectangle(250, 464, 130, 38));
            PageKit.Add(Editor, SaveButton, new Rectangle(394, 464, 134, 38));

            HostName.Cue = "например Бухгалтерия";
            Address.Cue = "192.168.20.25, pc01.corp.local или брокер RDS";
            User.Cue = "необязательно, например user";
            Domain.Cue = "CORP";
            Collection.Cue = "необязательно: alias коллекции";
            Gateway.Cue = "необязательно, например gw.example.ru";
            Editor.Visible = false;
        }

        public void SetRows(List<RdpList.Row> rows)
        {
            Computers.Rows.Clear();
            Computers.Rows.AddRange(rows);
            Computers.Height = Math.Max(1, rows.Count * Computers.RowHeight);
            Computers.Width = ListHost.ClientSize.Width - (Computers.Height > ListHost.ClientSize.Height ? SystemInformation.VerticalScrollBarWidth : 0);
            Computers.Invalidate();
            empty = rows.Count == 0;
            UpdateVisibility();
        }

        // Форма и список не перекрываются, а сменяют друг друга.
        public void ShowEditor(bool show)
        {
            Editor.Visible = show;
            UpdateVisibility();
        }

        void UpdateVisibility()
        {
            bool list = !Editor.Visible;
            Title.Visible = list;
            ListHost.Visible = list && !empty;
            emptyText.Visible = list && empty;
            AddFirst.Visible = list && empty;
            Add.Visible = list && !empty;
            Msg.Visible = list && Msg.HasMessage;
        }
    }
}
