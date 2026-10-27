using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace L2TPConnect
{
    internal enum PageMode { Empty, Idle, Connecting, Connected }

    internal static class PageKit
    {
        public static Label Label(Control parent, string text, Rectangle bounds, ContentAlignment align, Font font, Color color)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.Bounds = bounds;
            l.TextAlign = align;
            l.Font = font;
            l.ForeColor = color;
            l.BackColor = Color.Transparent;
            parent.Controls.Add(l);
            return l;
        }

        public static T Add<T>(Control parent, T control, Rectangle bounds) where T : Control
        {
            control.Bounds = bounds;
            parent.Controls.Add(control);
            return control;
        }
    }

    // «Удалённый доступ»: выбор подключения и вход / ход подключения / сведения о сессии.
    internal sealed class RemotePage : Panel
    {
        public readonly NetIllustration Art = new NetIllustration();
        public readonly NetIllustration ArtConnected = new NetIllustration();
        public readonly FlatSelect Profile = new FlatSelect();
        public readonly GlyphButton EditLink = new GlyphButton(Theme.GSettings, "Настроить");
        public readonly FlatField User = new FlatField(false);
        public readonly FlatField Pass = new FlatField(true);
        public readonly CheckBox SavePass = new CheckBox();
        public readonly MessageBar Msg = new MessageBar();
        public readonly FlatButton Connect = new FlatButton("Подключиться", false);
        public readonly FlatButton Cancel = new FlatButton("Отмена", true);
        public readonly FlatButton Disconnect = new FlatButton("Отключиться", false);
        public readonly FlatButton AddFirst = new FlatButton("Добавить подключение", false);
        public readonly FlatButton Fallback = new FlatButton("", true);
        public string FallbackName;
        public readonly GlyphButton ImportFirst = new GlyphButton(Theme.GImport, "Открыть файл профиля (.omnivpn)");
        public readonly ProgressStripe Progress = new ProgressStripe();
        public readonly InfoTable Info = new InfoTable();
        public readonly SpeedGraph Graph = new SpeedGraph();
        public readonly Label Title, BigStatus, Stage;

        readonly Control[] idleControls, connectingControls, connectedControls, emptyControls;

        public RemotePage()
        {
            BackColor = Color.White;
            const ContentAlignment right = ContentAlignment.MiddleRight;

            // Вход
            PageKit.Add(this, Art, new Rectangle(134, 20, 300, 160));
            var lProfile = PageKit.Label(this, "Подключение", new Rectangle(30, 202, 180, 28), right, Theme.Body, Theme.Label);
            PageKit.Add(this, Profile, new Rectangle(220, 201, 240, 30));
            PageKit.Add(this, EditLink, new Rectangle(468, 202, 96, 28));
            var lUser = PageKit.Label(this, "Логин", new Rectangle(30, 240, 180, 30), right, Theme.Body, Theme.Label);
            PageKit.Add(this, User, new Rectangle(220, 240, 240, 30));
            var lPass = PageKit.Label(this, "Пароль", new Rectangle(30, 278, 180, 30), right, Theme.Body, Theme.Label);
            PageKit.Add(this, Pass, new Rectangle(220, 278, 240, 30));
            SavePass.Text = "Сохранить пароль";
            SavePass.Font = Theme.Body;
            SavePass.ForeColor = Theme.Label;
            SavePass.AutoSize = true;
            PageKit.Add(this, SavePass, new Rectangle(221, 316, 200, 24));
            PageKit.Add(this, Msg, new Rectangle(54, 342, 460, 64));
            PageKit.Add(this, Connect, new Rectangle(204, 414, 160, 38));
            PageKit.Add(this, Fallback, new Rectangle(124, 462, 320, 36));

            // Подключение
            BigStatus = PageKit.Label(this, "", new Rectangle(10, 196, 548, 36), ContentAlignment.MiddleCenter, Theme.Status, Theme.Title);
            Stage = PageKit.Label(this, "", new Rectangle(30, 234, 508, 40), ContentAlignment.TopCenter, Theme.Body, Theme.Label);
            PageKit.Add(this, Progress, new Rectangle(184, 280, 200, 4));
            PageKit.Add(this, Cancel, new Rectangle(204, 306, 160, 38));

            // Подключено
            Title = PageKit.Label(this, "VPN подключён", new Rectangle(30, 16, 500, 44), ContentAlignment.MiddleLeft, Theme.PageTitle, Theme.Title);
            PageKit.Add(this, ArtConnected, new Rectangle(134, 62, 300, 134));
            ArtConnected.Mode = ArtMode.Connected;
            Info.LabelWidth = 180;
            PageKit.Add(this, Info, new Rectangle(24, 206, 520, 176));
            PageKit.Add(this, Graph, new Rectangle(84, 392, 400, 58));
            PageKit.Add(this, Disconnect, new Rectangle(204, 462, 160, 38));

            // Нет ни одного подключения
            var emptyText = PageKit.Label(this, "Ещё нет ни одного VPN-подключения.\nДобавьте его вручную или откройте файл профиля от администратора.",
                new Rectangle(10, 200, 548, 50), ContentAlignment.MiddleCenter, Theme.Body, Theme.Label);
            PageKit.Add(this, AddFirst, new Rectangle(184, 266, 200, 38));
            PageKit.Add(this, ImportFirst, new Rectangle(164, 316, 250, 28));

            idleControls = new Control[] { Art, lProfile, Profile, EditLink, lUser, User, lPass, Pass, SavePass, Msg, Connect, Fallback };
            connectingControls = new Control[] { Art, BigStatus, Stage, Progress, Cancel };
            connectedControls = new Control[] { Title, ArtConnected, Info, Graph, Disconnect };
            emptyControls = new Control[] { Art, emptyText, AddFirst, ImportFirst, Msg };
        }

        public void ShowMode(PageMode mode)
        {
            Control[] visible;
            switch (mode)
            {
                case PageMode.Empty: visible = emptyControls; break;
                case PageMode.Connecting: visible = connectingControls; break;
                case PageMode.Connected: visible = connectedControls; break;
                default: visible = idleControls; break;
            }
            SuspendLayout();
            foreach (Control c in Controls)
            {
                bool show = Array.IndexOf(visible, c) >= 0;
                if (c == Msg)
                    show = show && Msg.HasMessage;
                if (c == Fallback)
                    show = show && FallbackName != null;
                c.Visible = show;
            }
            ResumeLayout();
        }
    }

    // «Подключения»: создание и редактирование VPN-подключений (как «New VPN Connection» в FortiClient).
    internal sealed class ConnectionsPage : Panel
    {
        public readonly Label Title;
        public readonly FlatSelect Selector = new FlatSelect();
        public readonly GlyphButton New = new GlyphButton(Theme.GAdd, "Новое");
        public readonly GlyphButton Delete = new GlyphButton(Theme.GDelete, "Удалить");
        public readonly GlyphButton Import = new GlyphButton(Theme.GImport, "Импорт");
        public readonly GlyphButton Export = new GlyphButton(Theme.GExport, "Экспорт");
        public readonly FlatField ConnName = new FlatField(false);
        public readonly FlatField Server = new FlatField(false);
        public readonly FlatField Psk = new FlatField(true);
        public readonly FlatField Routes = new FlatField(false);
        public readonly FlatField User = new FlatField(false);
        public readonly FlatField Group = new FlatField(false);
        public readonly FlatField Domain = new FlatField(false);
        public readonly FlatSelect Balance = new FlatSelect();
        public readonly FlatField Dns = new FlatField(false);
        public readonly FlatField DnsDomain = new FlatField(false);
        public readonly MessageBar Msg = new MessageBar();
        public readonly FlatButton CancelButton = new FlatButton("Отмена", true);
        public readonly FlatButton SaveButton = new FlatButton("Сохранить", false);

        public ConnectionsPage()
        {
            BackColor = Color.White;
            const ContentAlignment left = ContentAlignment.MiddleLeft;

            Title = PageKit.Label(this, "Настройка подключения", new Rectangle(30, 16, 340, 44), left, Theme.PageTitle, Theme.Title);
            PageKit.Add(this, Import, new Rectangle(380, 26, 84, 28));
            PageKit.Add(this, Export, new Rectangle(468, 26, 92, 28));

            PageKit.Label(this, "Подключение", new Rectangle(40, 78, 160, 30), left, Theme.BodySemibold, Theme.Label);
            PageKit.Add(this, Selector, new Rectangle(210, 78, 196, 30));
            PageKit.Add(this, New, new Rectangle(414, 79, 70, 28));
            PageKit.Add(this, Delete, new Rectangle(488, 79, 78, 28));
            Delete.Color = Theme.Err;

            var line = new Panel();
            line.BackColor = Theme.CardBorder;
            PageKit.Add(this, line, new Rectangle(40, 122, 488, 1));

            PageKit.Label(this, "Название", new Rectangle(40, 132, 160, 30), left, Theme.BodySemibold, Theme.Label);
            PageKit.Add(this, ConnName, new Rectangle(210, 132, 318, 30));
            PageKit.Label(this, "VPN-сервер", new Rectangle(40, 168, 160, 30), left, Theme.BodySemibold, Theme.Label);
            PageKit.Add(this, Server, new Rectangle(210, 168, 318, 30));
            PageKit.Label(this, "Общий ключ (PSK)", new Rectangle(40, 204, 160, 30), left, Theme.BodySemibold, Theme.Label);
            PageKit.Add(this, Psk, new Rectangle(210, 204, 318, 30));
            PageKit.Label(this, "Сети за VPN", new Rectangle(40, 240, 160, 30), left, Theme.BodySemibold, Theme.Label);
            PageKit.Add(this, Routes, new Rectangle(210, 240, 318, 30));
            PageKit.Label(this, "Необязательно. Интернет идёт напрямую, не через VPN.",
                new Rectangle(210, 272, 318, 16), ContentAlignment.TopLeft, Theme.Small, Theme.Muted);
            PageKit.Label(this, "Логин", new Rectangle(40, 292, 160, 30), left, Theme.BodySemibold, Theme.Label);
            PageKit.Add(this, User, new Rectangle(210, 292, 170, 30));
            PageKit.Label(this, "Домен", new Rectangle(386, 292, 64, 30), ContentAlignment.MiddleRight, Theme.Body, Theme.Label);
            PageKit.Add(this, Domain, new Rectangle(456, 292, 72, 30));

            PageKit.Label(this, "Группа", new Rectangle(40, 328, 160, 30), left, Theme.BodySemibold, Theme.Label);
            PageKit.Add(this, Group, new Rectangle(210, 328, 150, 30));
            PageKit.Add(this, Balance, new Rectangle(366, 328, 162, 30));

            PageKit.Label(this, "DNS", new Rectangle(40, 364, 160, 30), left, Theme.BodySemibold, Theme.Label);
            PageKit.Add(this, Dns, new Rectangle(210, 364, 180, 30));
            PageKit.Add(this, DnsDomain, new Rectangle(396, 364, 132, 30));
            Balance.SetItems(new[] { "По порядку", "Меньше задержка" });
            Balance.SelectedIndex = 0;

            PageKit.Add(this, Msg, new Rectangle(40, 404, 488, 48));
            PageKit.Add(this, CancelButton, new Rectangle(134, 464, 140, 38));
            PageKit.Add(this, SaveButton, new Rectangle(294, 464, 140, 38));

            ConnName.Cue = "например Офис";
            Server.Cue = "vpn.example.ru или 203.0.113.10";
            Routes.Cue = "например 192.168.88.0/24, 10.10.0.0/16";
            User.Cue = "можно указать при подключении";
            Group.Cue = "напр. Офис";
            Domain.Cue = "CORP";
            Dns.Cue = "192.168.10.11, 192.168.10.12";
            DnsDomain.Cue = "домен: corp.local";
        }
    }

    internal sealed class AboutPage : Panel
    {
        public readonly Label UpdateStatus;
        public readonly Label UpdateNotes;
        public readonly ProgressLine UpdateProgress = new ProgressLine();
        public readonly FlatButton UpdateButton = new FlatButton("Обновить", false);
        public readonly FlatButton CheckButton = new FlatButton("Проверить обновления", true);

        public AboutPage()
        {
            BackColor = Color.White;
            PageKit.Label(this, "О программе", new Rectangle(30, 16, 520, 44), ContentAlignment.MiddleLeft, Theme.PageTitle, Theme.Title);
            PageKit.Add(this, new AppIconView(), new Rectangle(40, 82, 72, 72));
            PageKit.Label(this, Program.AppName, new Rectangle(128, 86, 400, 34), ContentAlignment.MiddleLeft,
                new Font("Segoe UI Semibold", 16f), Theme.Text);
            PageKit.Label(this, "Версия " + Program.Version + "  ·  клиент L2TP/IPsec VPN для Windows",
                new Rectangle(128, 120, 420, 24), ContentAlignment.MiddleLeft, Theme.Body, Theme.Muted);

            var info = new InfoTable();
            info.LabelWidth = 170;
            PageKit.Add(this, info, new Rectangle(24, 170, 520, 156));
            info.Set("Протокол", "L2TP/IPsec с общим ключом (PSK)", null);
            info.Set("VPN-клиент", "встроенный в Windows", null);
            info.Set("Основной шлюз", "не используется: интернет идёт напрямую", null);
            info.Set("Сервер за NAT", NatTraversalEnabled() ? "поддержка включена" : "поддержка выключена (включается установщиком)", null);
            info.Set("Хранение данных", "зашифровано средствами Windows (DPAPI)", null);
            info.Set("Папка данных", AppSettings.Folder, null);

            // Обновления: последний релиз на GitHub.
            PageKit.Label(this, "Обновления", new Rectangle(40, 340, 300, 24), ContentAlignment.MiddleLeft, Theme.BodySemibold, Theme.Text);
            UpdateStatus = PageKit.Label(this, "Ещё не проверялись", new Rectangle(40, 366, 500, 22), ContentAlignment.MiddleLeft, Theme.Body, Theme.Label);
            UpdateNotes = PageKit.Label(this, "", new Rectangle(40, 390, 500, 54), ContentAlignment.TopLeft, Theme.Small, Theme.Muted);
            PageKit.Add(this, UpdateProgress, new Rectangle(40, 450, 488, 6));
            UpdateProgress.Visible = false;
            PageKit.Add(this, CheckButton, new Rectangle(40, 466, 200, 36));
            PageKit.Add(this, UpdateButton, new Rectangle(250, 466, 200, 36));
            UpdateButton.Visible = false;
        }

        public void SetUpdateStatus(string text, Color color)
        {
            UpdateStatus.Text = text;
            UpdateStatus.ForeColor = color;
        }

        static bool NatTraversalEnabled()
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PolicyAgent"))
                {
                    object v = key != null ? key.GetValue("AssumeUDPEncapsulationContextOnSendRule") : null;
                    return v is int && (int)v == 2;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
