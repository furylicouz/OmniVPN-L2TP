using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("OmniVPN Connect")]
[assembly: AssemblyProduct("OmniVPN Connect")]
[assembly: AssemblyDescription("Клиент L2TP/IPsec VPN")]
[assembly: AssemblyVersion("1.9.1.0")]
[assembly: AssemblyFileVersion("1.9.1.0")]
[assembly: ComVisible(false)]

namespace L2TPConnect
{
    internal static class Program
    {
        public const string AppName = "OmniVPN Connect";
        public const string Version = "1.9.1";

        // Второй экземпляр просит первый показать окно (оно может быть скрыто в трее).
        public static readonly int ShowMessage = RegisterWindowMessage("OmniVPNConnect.ShowWindow");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int RegisterWindowMessage(string message);

        public const int WM_COPYDATA = 0x004A;
        static readonly IntPtr CopyDataMagic = (IntPtr)0x4F4D4E49; // «OMNI»: путь к файлу профилей

        [StructLayout(LayoutKind.Sequential)]
        struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);

        [DllImport("user32.dll")]
        static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref COPYDATASTRUCT data);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);

        [STAThread]
        static int Main(string[] args)
        {
            // Вызывается деинсталлятором: разорвать соединение, удалить профиль и настройки.
            if (args.Length > 0 && args[0] == "--cleanup")
            {
                Ras.HangUp(Ras.FindConnection(VpnProfile.EntryName));
                VpnProfile.Remove(true);
                try { Directory.Delete(AppSettings.Folder, true); } catch { }
                return 0;
            }

            // Двойной щелчок по файлу .omnivpn: путь приходит первым аргументом.
            string profileFile = args.Length > 0 && File.Exists(args[0]) ? Path.GetFullPath(args[0]) : null;

            bool created;
            using (var mutex = new Mutex(true, "Local\\OmniVPNConnect.SingleInstance", out created))
            {
                if (!created)
                {
                    IntPtr wnd = FindMainWindow();
                    if (wnd != IntPtr.Zero)
                    {
                        AllowSetForegroundWindow(-1);
                        if (profileFile != null)
                            SendProfileFile(wnd, profileFile);
                        else
                            PostMessage(wnd, ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    }
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(profileFile));
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        // Главное окно уже запущенного экземпляра: заголовок программы, не диалог (#32770), чужой процесс.
        static IntPtr FindMainWindow()
        {
            IntPtr found = IntPtr.Zero;
            int self = Process.GetCurrentProcess().Id;
            EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                var title = new StringBuilder(256);
                var cls = new StringBuilder(256);
                GetWindowText(hWnd, title, title.Capacity);
                GetClassName(hWnd, cls, cls.Capacity);
                int pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (title.ToString() == AppName && cls.ToString() != "#32770" && pid != self)
                {
                    found = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        static void SendProfileFile(IntPtr wnd, string path)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(path + "\0");
            IntPtr buf = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, buf, bytes.Length);
                var cds = new COPYDATASTRUCT { dwData = CopyDataMagic, cbData = bytes.Length, lpData = buf };
                SendMessage(wnd, WM_COPYDATA, IntPtr.Zero, ref cds);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        public static string ReadProfileFile(IntPtr lParam)
        {
            var cds = (COPYDATASTRUCT)Marshal.PtrToStructure(lParam, typeof(COPYDATASTRUCT));
            if (cds.dwData != CopyDataMagic || cds.lpData == IntPtr.Zero || cds.cbData <= 0 || cds.cbData > 65536)
                return null;
            return Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2).TrimEnd('\0');
        }
    }
}
