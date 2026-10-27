using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace L2TPConnect
{
    internal abstract class PaintedControl : Control
    {
        protected PaintedControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        }

        protected Color ParentBack
        {
            get { return Parent != null ? Parent.BackColor : Color.White; }
        }
    }

    // Плоская кнопка: основная (синяя) или второстепенная (белая с рамкой).
    internal sealed class FlatButton : Button
    {
        bool hover, down;
        public bool Secondary;

        public FlatButton(string text, bool secondary)
        {
            Text = text;
            Secondary = secondary;
            Font = Theme.Button;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = Theme.Scale(g);
            g.Clear(Parent != null ? Parent.BackColor : Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color back, fore, border;
            if (Secondary)
            {
                back = down ? Color.FromArgb(0xE6, 0xEC, 0xF2) : hover ? Color.FromArgb(0xF2, 0xF6, 0xF9) : Color.White;
                fore = Enabled ? Theme.Title : Theme.Muted;
                border = Focused ? Theme.Primary : Theme.FieldBorder;
            }
            else
            {
                back = !Enabled ? Theme.Blend(Theme.Primary, Color.White, 0.45f)
                     : down ? Theme.PrimaryDown : hover ? Theme.PrimaryHover : Theme.Primary;
                fore = Color.White;
                border = Focused ? Theme.PrimaryDown : back;
            }

            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.RoundRect(r, 3 * s))
            using (var br = new SolidBrush(back))
            using (var pen = new Pen(border))
            {
                g.FillPath(br, path);
                g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    // Значок с подписью, работает как ссылка («Настроить», «Новое», «Удалить», глаз у пароля).
    internal sealed class GlyphButton : PaintedControl
    {
        readonly string glyph;
        bool hover;
        public Color Color = Theme.Primary;

        public GlyphButton(string glyph, string text)
        {
            this.glyph = glyph;
            Text = text;
            Font = Theme.Body;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = Theme.Scale(g);
            g.Clear(ParentBack);
            Color c = !Enabled ? Theme.Dots : hover ? Theme.PrimaryDown : Color;
            float icon = 14 * s;
            float iconBox = string.IsNullOrEmpty(Text) ? Width : icon + 6 * s;
            Theme.Glyph(g, glyph, icon, c, new RectangleF(0, 0, iconBox, Height));
            if (!string.IsNullOrEmpty(Text))
            {
                var tf = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
                var font = hover ? new Font(Font, FontStyle.Underline) : Font;
                TextRenderer.DrawText(g, Text, font, new Rectangle((int)iconBox, 0, Width - (int)iconBox, Height), c, tf);
                if (hover) font.Dispose();
            }
        }
    }

    // Поле ввода с тонкой рамкой, подсветкой фокуса и (для паролей) кнопкой «показать».
    internal sealed class FlatField : PaintedControl
    {
        public readonly TextBox Box = new TextBox();
        readonly GlyphButton eye;
        string cue;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public FlatField(bool password)
        {
            BackColor = Color.White;
            Cursor = Cursors.IBeam;
            Box.BorderStyle = BorderStyle.None;
            Box.Font = Theme.Body;
            Box.ForeColor = Theme.Text;
            Box.UseSystemPasswordChar = password;
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Box.HandleCreated += delegate { ApplyCue(); };
            Controls.Add(Box);
            if (password)
            {
                eye = new GlyphButton(Theme.GEye, "");
                eye.Color = Theme.Muted;
                eye.Click += delegate { Box.UseSystemPasswordChar = !Box.UseSystemPasswordChar; };
                Controls.Add(eye);
            }
        }

        public string Cue
        {
            set { cue = value; ApplyCue(); }
        }

        void ApplyCue()
        {
            if (cue != null && Box.IsHandleCreated)
                SendMessage(Box.Handle, 0x1501, (IntPtr)1, cue);
        }

        public override string Text
        {
            get { return Box.Text; }
            set { Box.Text = value; }
        }

        public bool ReadOnly
        {
            get { return Box.ReadOnly; }
            set
            {
                Box.ReadOnly = value;
                Box.BackColor = BackColor = value ? Theme.FieldReadOnly : Color.White;
                Invalidate();
            }
        }

        protected override void OnClick(EventArgs e)
        {
            Box.Focus();
            base.OnClick(e);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = (int)(8 * Box.Font.Height / 18f);
            int eyeW = eye != null ? Height : 0;
            Box.Location = new Point(pad, (Height - Box.Height) / 2);
            Box.Width = Math.Max(10, Width - 2 * pad - eyeW);
            if (eye != null)
                eye.Bounds = new Rectangle(Width - eyeW, 1, eyeW - 1, Height - 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            using (var pen = new Pen(Box.Focused ? Theme.Primary : Theme.FieldBorder))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    // Выпадающий список в стиле полей ввода (системный ComboBox выбивается из дизайна).
    internal sealed class FlatSelect : PaintedControl
    {
        readonly List<string> items = new List<string>();
        readonly List<string> statusText = new List<string>();
        readonly List<Color> statusColor = new List<Color>();
        int selectedIndex = -1;
        bool hover, dropped;
        ContextMenuStrip openMenu;

        public event EventHandler SelectedIndexChanged;
        public event EventHandler DropDownOpening;

        public FlatSelect()
        {
            Font = Theme.Body;
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(ControlStyles.Selectable, true);
        }

        public void SetItems(IEnumerable<string> names)
        {
            items.Clear();
            items.AddRange(names);
            statusText.Clear();
            statusColor.Clear();
            foreach (var n in items)
            {
                statusText.Add("");
                statusColor.Add(Theme.Muted);
            }
            if (selectedIndex >= items.Count)
                selectedIndex = -1;
            Invalidate();
        }

        // Статус рядом с названием: «доступен, 24 мс» (в поле показывается часть до запятой).
        public void SetStatus(int index, string text, Color color)
        {
            if (index < 0 || index >= items.Count)
                return;
            statusText[index] = text ?? "";
            statusColor[index] = color;
            Invalidate();
            if (openMenu != null && index < openMenu.Items.Count)
            {
                var mi = (ToolStripMenuItem)openMenu.Items[index];
                mi.ShortcutKeyDisplayString = MenuStatus(index);
                mi.Tag = color;
                openMenu.Invalidate();
            }
        }

        public int SelectedIndex
        {
            get { return selectedIndex; }
            set
            {
                if (value < -1 || value >= items.Count || value == selectedIndex)
                    return;
                selectedIndex = value;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        string MenuStatus(int i)
        {
            return statusText[i].Length > 0 ? "●  " + statusText[i] : null;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left)
                Open();
            base.OnMouseDown(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.F4 || (e.Alt && e.KeyCode == Keys.Down))
                Open();
            else if (e.KeyCode == Keys.Down && selectedIndex < items.Count - 1)
                SelectedIndex = selectedIndex + 1;
            else if (e.KeyCode == Keys.Up && selectedIndex > 0)
                SelectedIndex = selectedIndex - 1;
            base.OnKeyDown(e);
        }

        void Open()
        {
            if (items.Count == 0 || dropped)
                return;
            var menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
            menu.Font = Font;
            menu.MinimumSize = new Size(Width, 0);
            menu.Renderer = new StatusMenuRenderer();
            for (int i = 0; i < items.Count; i++)
            {
                int index = i;
                var item = new ToolStripMenuItem(items[i]);
                item.Padding = new Padding(0, 3, 0, 3);
                item.ShortcutKeyDisplayString = MenuStatus(i);
                item.Tag = statusColor[i];
                if (i == selectedIndex)
                    item.Font = new Font(Font, FontStyle.Bold);
                item.Click += delegate { SelectedIndex = index; };
                menu.Items.Add(item);
            }
            menu.Closed += delegate
            {
                dropped = false;
                openMenu = null;
                Invalidate();
                BeginInvoke(new Action(menu.Dispose));
            };
            dropped = true;
            openMenu = menu;
            Invalidate();
            menu.Show(this, new Point(0, Height));
            if (DropDownOpening != null) DropDownOpening(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = Theme.Scale(g);
            g.Clear(!Enabled ? Theme.FieldReadOnly : hover ? Color.FromArgb(0xF7, 0xF9, 0xFB) : Color.White);
            using (var pen = new Pen(Focused || dropped ? Theme.Primary : Theme.FieldBorder))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            string text = selectedIndex >= 0 ? items[selectedIndex] : "";
            int pad = (int)(8 * s);
            int arrow = (int)(26 * s);
            const TextFormatFlags one = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

            int statusWidth = 0;
            if (selectedIndex >= 0 && statusText[selectedIndex].Length > 0)
            {
                string st = statusText[selectedIndex];
                int comma = st.IndexOf(',');
                if (comma > 0)
                    st = st.Substring(0, comma);
                Size sz = TextRenderer.MeasureText(g, st, Theme.Small, Size.Empty, one);
                int dot = (int)(7 * s);
                statusWidth = sz.Width + dot + (int)(6 * s);
                int x = Width - arrow - statusWidth;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(statusColor[selectedIndex]))
                    g.FillEllipse(b, x, (Height - dot) / 2f, dot, dot);
                TextRenderer.DrawText(g, st, Theme.Small, new Rectangle(x + dot + (int)(5 * s), 0, sz.Width + 2, Height),
                    statusColor[selectedIndex], one);
                statusWidth += (int)(8 * s);
            }
            TextRenderer.DrawText(g, text, Font, new Rectangle(pad, 0, Width - pad - arrow - statusWidth, Height),
                Enabled ? Theme.Text : Theme.Muted, one | TextFormatFlags.EndEllipsis);
            Theme.Glyph(g, "", 10 * s, Theme.Label, new RectangleF(Width - arrow, 0, arrow - 4 * s, Height));
        }

        // Красит статус в меню (он выводится в колонке «сочетание клавиш») цветом из Tag.
        sealed class StatusMenuRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                var mi = e.Item as ToolStripMenuItem;
                if (mi != null && mi.Tag is Color && e.Text == mi.ShortcutKeyDisplayString)
                    e.TextColor = (Color)mi.Tag;
                base.OnRenderItemText(e);
            }
        }
    }

    // Плашка с сообщением: ошибка (красная) или успех (зелёная).
    internal sealed class MessageBar : PaintedControl
    {
        bool error = true;

        public MessageBar()
        {
            Font = Theme.Body;
            Visible = false;
        }

        public void ShowError(string text) { Show(text, true); }
        public void ShowOk(string text) { Show(text, false); }

        // Visible у дочернего контрола ложен, пока скрыта страница, поэтому храним своё состояние.
        public bool HasMessage { get; private set; }

        void Show(string text, bool isError)
        {
            Text = text;
            error = isError;
            HasMessage = true;
            Visible = true;
            Invalidate();
        }

        public void Clear()
        {
            HasMessage = false;
            Visible = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = Theme.Scale(g);
            g.Clear(ParentBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color accent = error ? Theme.Err : Theme.Ok;
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 3 * s))
            using (var br = new SolidBrush(error ? Theme.ErrBack : Theme.OkBack))
            using (var pen = new Pen(Theme.Blend(accent, Color.White, 0.6f)))
            {
                g.FillPath(br, path);
                g.DrawPath(pen, path);
            }
            Theme.Glyph(g, error ? Theme.GError : Theme.GCheck, 16 * s, accent, new RectangleF(8 * s, 0, 22 * s, Height));
            var rect = new Rectangle((int)(38 * s), (int)(2 * s), Width - (int)(46 * s), Height - (int)(4 * s));
            TextRenderer.DrawText(g, Text, Font, rect, Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    // Белая карточка с рамкой — основная область справа от меню.
    internal sealed class Card : Panel
    {
        public Card()
        {
            BackColor = Color.White;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.CardBorder))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    // Левая синяя панель: аватар, логин, статус и пункты меню.
    internal sealed class Sidebar : PaintedControl
    {
        static readonly string[] Items = { "УДАЛЁННЫЙ ДОСТУП", "Удалённые ПК", "Подключения", "О программе" };
        static readonly string[] Glyphs = { Theme.GLaptop, Theme.GDesktop, Theme.GSettings, Theme.GInfo };

        int selected, hover = -1;
        string userName = "", status = "";
        Color statusColor = Theme.Dots;
        string netText = "проверка…", serverText = "—";
        Color netColor = Theme.Dots, serverColor = Theme.Dots;
        string updateText;

        public event EventHandler SelectedChanged;
        public event EventHandler UpdateClicked;

        public int Selected
        {
            get { return selected; }
            set
            {
                if (selected == value) return;
                selected = value;
                Invalidate();
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
        }

        public void SetUser(string name, string statusText, Color color)
        {
            userName = name ?? "";
            status = statusText ?? "";
            statusColor = color;
            Invalidate();
        }

        // Нижний блок: «● Интернет — работает, 12 мс» и «● VPN-сервер — 24 мс».
        public void SetNet(string internet, Color internetColor, string server, Color srvColor)
        {
            if (netText == internet && netColor == internetColor && serverText == server && serverColor == srvColor)
                return;
            netText = internet;
            netColor = internetColor;
            serverText = server;
            serverColor = srvColor;
            Invalidate();
        }

        // Строка под версией: «Доступна версия 1.9.1», «Обновление: 45%»; null — обновлений нет.
        public void SetUpdate(string text)
        {
            if (updateText == text) return;
            updateText = text;
            Invalidate();
        }

        float S { get { return Width / 230f; } }
        float ItemTop { get { return 196 * S; } }

        Rectangle UpdateRect
        {
            get { return new Rectangle((int)(14 * S), Height - (int)(40 * S), Width - (int)(28 * S), (int)(26 * S)); }
        }

        bool OverUpdate(Point p)
        {
            return updateText != null && UpdateRect.Contains(p);
        }
        float ItemHeight { get { return 52 * S; } }

        int HitTest(Point p)
        {
            if (p.Y < ItemTop) return -1;
            int i = (int)((p.Y - ItemTop) / ItemHeight);
            return i < Items.Length ? i : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != hover)
            {
                hover = h;
                Invalidate();
            }
            Cursor = h >= 0 || OverUpdate(e.Location) ? Cursors.Hand : Cursors.Default;
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
            int h = HitTest(e.Location);
            if (h >= 0) Selected = h;
            else if (OverUpdate(e.Location) && UpdateClicked != null) UpdateClicked(this, EventArgs.Empty);
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = S;
            g.Clear(Theme.Blue);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Аватар
            float cx = Width / 2f, cy = 80 * s, r = 46 * s;
            using (var ring = new SolidBrush(Color.FromArgb(70, Color.White)))
                g.FillEllipse(ring, cx - r - 4 * s, cy - r - 4 * s, (r + 4 * s) * 2, (r + 4 * s) * 2);
            using (var fill = new SolidBrush(Theme.ArtBack))
                g.FillEllipse(fill, cx - r, cy - r, r * 2, r * 2);
            Theme.Glyph(g, Theme.GPerson, 46 * s, Theme.Blue, new RectangleF(cx - r, cy - r + 2 * s, r * 2, r * 2));

            const TextFormatFlags center = TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine |
                                           TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, userName, Theme.NavSelected, new Rectangle(8, (int)(138 * s), Width - 16, (int)(24 * s)), Color.White, center);

            // Статус: ● Подключено
            Size st = TextRenderer.MeasureText(g, status, Theme.Small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            float dot = 8 * s;
            float total = dot + 6 * s + st.Width;
            float sx = (Width - total) / 2;
            float sy = 166 * s;
            using (var b = new SolidBrush(statusColor))
                g.FillEllipse(b, sx, sy + (st.Height - dot) / 2, dot, dot);
            TextRenderer.DrawText(g, status, Theme.Small, new Point((int)(sx + dot + 6 * s), (int)sy), Theme.SideMuted,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

            using (var line = new Pen(Theme.SideLine))
            {
                g.DrawLine(line, 0, ItemTop - 1, Width, ItemTop - 1);
                for (int i = 0; i < Items.Length; i++)
                {
                    var rect = new RectangleF(0, ItemTop + i * ItemHeight, Width, ItemHeight - 1);
                    if (i == selected)
                    {
                        using (var b = new SolidBrush(Theme.Navy))
                            g.FillRectangle(b, rect);
                        using (var b = new SolidBrush(Theme.NavyAccent))
                            g.FillRectangle(b, 0, rect.Y, 4 * s, rect.Height);
                    }
                    else if (i == hover)
                    {
                        using (var b = new SolidBrush(Theme.BlueHover))
                            g.FillRectangle(b, rect);
                    }
                    Color fore = i == selected ? Color.White : Theme.SideMuted;
                    var iconRect = new RectangleF(16 * s, rect.Y, 30 * s, rect.Height);
                    Theme.Glyph(g, Glyphs[i], 18 * s, fore, iconRect);
                    if (Glyphs[i] == Theme.GLaptop)
                    {
                        // Замок поверх ноутбука, как значок Remote Access в FortiClient.
                        Color under = i == selected ? Theme.Navy : i == hover ? Theme.BlueHover : Theme.Blue;
                        float lr = 6.5f * s, lx = iconRect.X + 23 * s, ly = rect.Y + rect.Height / 2 + 4 * s;
                        using (var b = new SolidBrush(under))
                            g.FillEllipse(b, lx - lr, ly - lr, 2 * lr, 2 * lr);
                        Theme.Glyph(g, Theme.GLock, 10 * s, fore, new RectangleF(lx - lr, ly - lr, 2 * lr, 2 * lr));
                    }
                    TextRenderer.DrawText(g, Items[i], i == selected ? Theme.NavSelected : Theme.Nav,
                        new Rectangle((int)(56 * s), (int)rect.Y, Width - (int)(60 * s), (int)rect.Height), fore,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    g.DrawLine(line, 0, rect.Bottom, Width, rect.Bottom);
                }
            }

            int top = Height - (int)(124 * s);
            using (var line = new Pen(Theme.SideLine))
                g.DrawLine(line, 0, top, Width, top);
            NetRow(g, s, top + (int)(12 * s), "Интернет", netText, netColor);
            NetRow(g, s, top + (int)(36 * s), "VPN-сервер", serverText, serverColor);
            NetRow(g, s, top + (int)(60 * s), "Версия", Program.Version, updateText != null ? Theme.NavyAccent : Theme.Ok);

            if (updateText != null)
            {
                // Кнопка-ссылка на страницу «О программе», где идёт обновление.
                Rectangle pill = UpdateRect;
                using (var path = Theme.RoundRect(pill, 4 * s))
                using (var b = new SolidBrush(Theme.Navy))
                    g.FillPath(b, path);
                Size ts = TextRenderer.MeasureText(g, updateText, Theme.Small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                float icon = 16 * s, gap = 4 * s;
                float x = pill.X + (pill.Width - icon - gap - ts.Width) / 2;
                Theme.Glyph(g, Theme.GDownload, 11 * s, Color.White, new RectangleF(x, pill.Y, icon, pill.Height));
                TextRenderer.DrawText(g, updateText, Theme.Small, new Rectangle((int)(x + icon + gap), pill.Y, pill.Right - (int)(x + icon + gap), pill.Height),
                    Color.White, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            else
            {
                TextRenderer.DrawText(g, "L2TP/IPsec  ·  " + Program.AppName, Theme.Small,
                    new Rectangle(0, Height - (int)(34 * s), Width, (int)(20 * s)), Theme.SideMuted, center);
            }
        }

        void NetRow(Graphics g, float s, int y, string label, string value, Color dotColor)
        {
            const TextFormatFlags one = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            int h = (int)(20 * s);
            float dot = 8 * s;
            using (var b = new SolidBrush(dotColor))
                g.FillEllipse(b, 20 * s, y + (h - dot) / 2, dot, dot);
            TextRenderer.DrawText(g, label, Theme.Small, new Rectangle((int)(36 * s), y, (int)(90 * s), h), Theme.SideMuted, one);
            TextRenderer.DrawText(g, value, Theme.Small, new Rectangle((int)(110 * s), y, Width - (int)(126 * s), h), Color.White,
                one | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }
    }

    internal enum ArtMode { Idle, Connecting, Connected, Error }

    // Иллюстрация «компьютер ● ● ● ● глобус» с замком, как в FortiClient.
    internal sealed class NetIllustration : PaintedControl
    {
        ArtMode mode;
        readonly DateTime start = DateTime.Now;

        public ArtMode Mode
        {
            get { return mode; }
            set { mode = value; Invalidate(); }
        }

        public void Tick()
        {
            if (mode == ArtMode.Connecting) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(ParentBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Базовая сетка 300x150, вписываем по центру.
            float s = Math.Min(Width / 300f, Height / 150f);
            float ox = (Width - 300 * s) / 2, oy = (Height - 150 * s) / 2;
            Func<float, float> X = v => ox + v * s;
            Func<float, float> Y = v => oy + v * s;

            float r = 48 * s;
            using (var back = new SolidBrush(Theme.ArtBack))
            {
                g.FillEllipse(back, X(58) - r, Y(92) - r, 2 * r, 2 * r);
                g.FillEllipse(back, X(242) - r, Y(92) - r, 2 * r, 2 * r);
            }
            Color iconColor = mode == ArtMode.Error ? Theme.Blend(Theme.Blue, Color.White, 0.25f) : Theme.Blue;
            Theme.Glyph(g, Theme.GLaptop, 52 * s, iconColor, new RectangleF(X(58) - r, Y(92) - r + 2 * s, 2 * r, 2 * r));
            Theme.Glyph(g, Theme.GGlobe, 56 * s, iconColor, new RectangleF(X(242) - r, Y(92) - r, 2 * r, 2 * r));

            // Точки между ними
            const int n = 9;
            double t = (DateTime.Now - start).TotalSeconds;
            for (int i = 0; i < n; i++)
            {
                float x = X(116 + i * 68f / (n - 1));
                float y = Y(92);
                Color c;
                float rad = 3.4f * s;
                switch (mode)
                {
                    case ArtMode.Connected: c = Theme.Ok; break;
                    case ArtMode.Error: c = Theme.Blend(Theme.Err, Color.White, 0.35f); break;
                    case ArtMode.Connecting:
                    {
                        double phase = (t * 9 - i) % (n + 3);
                        if (phase < 0) phase += n + 3;
                        float k = phase < 3 ? (float)(1 - Math.Abs(phase - 1.5) / 1.5) : 0f;
                        c = Theme.Blend(Theme.Dots, Theme.Primary, k);
                        rad *= 1 + 0.35f * k;
                        break;
                    }
                    default: c = Theme.Dots; break;
                }
                using (var b = new SolidBrush(c))
                    g.FillEllipse(b, x - rad, y - rad, 2 * rad, 2 * rad);
            }

            // Значок-замок над линией
            Color badge;
            string glyph;
            switch (mode)
            {
                case ArtMode.Connected: badge = Theme.Ok; glyph = Theme.GLock; break;
                case ArtMode.Error: badge = Theme.Err; glyph = Theme.GCancel; break;
                case ArtMode.Connecting: badge = Theme.Primary; glyph = Theme.GLock; break;
                default: badge = Theme.Muted; glyph = Theme.GUnlock; break;
            }
            float br = 19 * s;
            float bx = X(150), by = Y(46);
            g.FillEllipse(Brushes.White, bx - br - 3 * s, by - br - 3 * s, 2 * (br + 3 * s), 2 * (br + 3 * s));
            using (var b = new SolidBrush(badge))
                g.FillEllipse(b, bx - br, by - br, 2 * br, 2 * br);
            Theme.Glyph(g, glyph, 18 * s, Color.White, new RectangleF(bx - br, by - br, 2 * br, 2 * br));
        }
    }

    // Бегущая полоска «идёт подключение».
    internal sealed class ProgressStripe : PaintedControl
    {
        readonly DateTime start = DateTime.Now;

        public void Tick()
        {
            if (Visible) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.FromArgb(0xE3, 0xEA, 0xF1));
            double t = (DateTime.Now - start).TotalSeconds;
            float seg = Width * 0.3f;
            float x = (float)((t * 0.8 % 1.0) * (Width + seg)) - seg;
            using (var b = new SolidBrush(Theme.Primary))
                g.FillRectangle(b, x, 0, seg, Height);
        }
    }

    // Полоска хода загрузки обновления: Value от 0 до 1.
    internal sealed class ProgressLine : PaintedControl
    {
        double fraction;

        public double Value
        {
            get { return fraction; }
            set
            {
                double v = Math.Max(0, Math.Min(1, value));
                if (v == fraction) return;
                fraction = v;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.FromArgb(0xE3, 0xEA, 0xF1));
            using (var b = new SolidBrush(Theme.Primary))
                g.FillRectangle(b, 0, 0, (float)(Width * fraction), Height);
        }
    }

    // Таблица «подпись — значение», подписи выровнены вправо, как в FortiClient.
    internal sealed class InfoTable : PaintedControl
    {
        sealed class Row
        {
            public string Label, Value, Extra;
        }

        readonly List<Row> rows = new List<Row>();
        public int LabelWidth = 180;

        public void Set(string label, string value, string extra)
        {
            Row row = rows.Find(x => x.Label == label);
            if (row == null)
            {
                row = new Row { Label = label };
                rows.Add(row);
            }
            if (row.Value == value && row.Extra == extra) return;
            row.Value = value;
            row.Extra = extra;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = Theme.Scale(g);
            g.Clear(ParentBack);
            int rowH = (int)(25 * s);
            int split = (int)(LabelWidth * s);
            const TextFormatFlags one = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                int y = i * rowH;
                TextRenderer.DrawText(g, row.Label, Theme.Body, new Rectangle(0, y, split, rowH), Theme.Label, one | TextFormatFlags.Right);
                int vx = split + (int)(14 * s);
                TextRenderer.DrawText(g, row.Value ?? "", Theme.Body, new Rectangle(vx, y, Width - vx, rowH), Theme.Text, one | TextFormatFlags.EndEllipsis);
                if (!string.IsNullOrEmpty(row.Extra))
                {
                    int w = TextRenderer.MeasureText(g, row.Value ?? "", Theme.Body, Size.Empty, one).Width;
                    TextRenderer.DrawText(g, row.Extra, Theme.Body, new Rectangle(vx + w + (int)(12 * s), y, Width, rowH), Theme.Muted, one);
                }
            }
        }
    }

    // Компактный график скорости приёма/передачи за минуту.
    internal sealed class SpeedGraph : PaintedControl
    {
        const int N = 60;
        readonly double[] rx = new double[N], tx = new double[N];

        public void Push(double received, double sent)
        {
            Array.Copy(rx, 1, rx, 0, N - 1);
            Array.Copy(tx, 1, tx, 0, N - 1);
            rx[N - 1] = received;
            tx[N - 1] = sent;
            Invalidate();
        }

        public void Reset()
        {
            Array.Clear(rx, 0, N);
            Array.Clear(tx, 0, N);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = Theme.Scale(g);
            g.Clear(ParentBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            const TextFormatFlags one = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            double max = 1024;
            for (int i = 0; i < N; i++)
                max = Math.Max(max, Math.Max(rx[i], tx[i]));
            max *= 1.15;

            TextRenderer.DrawText(g, "Скорость за минуту, до " + Fmt.Speed(max), Theme.Small, new Point(0, 0), Theme.Muted, one);
            int lx = Width;
            lx = Legend(g, "передача", Theme.Primary, lx, s);
            Legend(g, "приём", Theme.Ok, lx - (int)(10 * s), s);

            var plot = new RectangleF(0, 18 * s, Width - 1, Height - 18 * s - 1);
            using (var bg = new SolidBrush(Color.FromArgb(0xF7, 0xF9, 0xFB)))
                g.FillRectangle(bg, plot);
            using (var axis = new Pen(Theme.CardBorder))
                g.DrawLine(axis, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
            Series(g, plot, tx, max, Theme.Primary, s);
            Series(g, plot, rx, max, Theme.Ok, s);
        }

        static int Legend(Graphics g, string text, Color color, int right, float s)
        {
            const TextFormatFlags one = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            Size sz = TextRenderer.MeasureText(g, text, Theme.Small, Size.Empty, one);
            int x = right - sz.Width;
            TextRenderer.DrawText(g, text, Theme.Small, new Point(x, 0), Theme.Muted, one);
            int box = (int)(8 * s);
            using (var b = new SolidBrush(color))
                g.FillRectangle(b, x - box - (int)(4 * s), (sz.Height - box) / 2, box, box);
            return x - box - (int)(4 * s);
        }

        static void Series(Graphics g, RectangleF plot, double[] data, double max, Color color, float s)
        {
            var pts = new PointF[N];
            for (int i = 0; i < N; i++)
                pts[i] = new PointF(plot.Left + plot.Width * i / (N - 1), plot.Bottom - (float)(data[i] / max) * plot.Height);
            using (var path = new GraphicsPath())
            {
                path.AddLine(plot.Left, plot.Bottom, pts[0].X, pts[0].Y);
                path.AddLines(pts);
                path.AddLine(pts[N - 1].X, pts[N - 1].Y, plot.Right, plot.Bottom);
                path.CloseFigure();
                using (var fill = new SolidBrush(Color.FromArgb(45, color)))
                    g.FillPath(fill, path);
            }
            using (var pen = new Pen(color, 1.5f * s))
            {
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, pts);
            }
        }
    }

    // Крупный значок приложения для страницы «О программе».
    internal sealed class AppIconView : PaintedControl
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(ParentBack);
            float side = Math.Min(Width, Height) - 2;
            Theme.AppIcon(e.Graphics, new RectangleF((Width - side) / 2, (Height - side) / 2, side, side));
        }
    }
}
