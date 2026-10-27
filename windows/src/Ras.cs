using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace L2TPConnect
{
    // Тонкая обёртка над rasapi32.dll: набор, разрыв, статус и статистика соединения.
    internal static class Ras
    {
        public const int ERROR_INVALID_HANDLE = 6;
        public const int ERROR_BUFFER_TOO_SMALL = 603;
        public const int ERROR_USER_DISCONNECTION = 631;
        public const int ERROR_INVALID_SIZE = 632;

        public const int RASCS_Connected = 0x2000;
        public const int RASCS_Disconnected = 0x2001;

        const int RASP_PppIp = 0x8021;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct RASCONN
        {
            public int dwSize;
            public IntPtr hrasconn;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string szEntryName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string szDeviceType;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string szDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPhonebook;
            public int dwSubEntry;
            public Guid guidEntry;
            public int dwFlags;
            public int luidLow;
            public int luidHigh;
            public Guid guidCorrelationId;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct RASDIALPARAMS
        {
            public int dwSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string szEntryName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string szPhoneNumber;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string szCallbackNumber;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string szUserName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string szPassword;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string szDomain;
            public int dwSubEntry;
            public IntPtr dwCallbackId;
            public int dwIfIndex;
            public IntPtr szEncPassword;
        }

        // RasDialFunc: вызывается RAS на своём потоке при каждой смене этапа набора.
        public delegate void DialCallback(uint unMsg, int rasConnState, int dwError);

        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RasDial(IntPtr lpRasDialExtensions, string lpszPhonebook, ref RASDIALPARAMS lpRasDialParams,
            int dwNotifierType, DialCallback lpvNotifier, out IntPtr lphRasConn);

        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RasHangUp(IntPtr hrasconn);

        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RasEnumConnections(IntPtr lprasconn, ref int lpcb, out int lpcConnections);

        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RasGetConnectStatus(IntPtr hrasconn, IntPtr lprasconnstatus);

        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RasGetConnectionStatistics(IntPtr hrasconn, IntPtr lpStatistics);

        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RasGetProjectionInfo(IntPtr hrasconn, int rasprojection, IntPtr lpprojection, ref int lpcb);

        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RasGetErrorString(int uErrorValue, StringBuilder lpszErrorString, int cBufSize);

        // Add-VpnConnection без -AllUserConnection пишет профиль сюда.
        public static string UserPhonebook
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Network\Connections\Pbk\rasphone.pbk");
            }
        }

        // Асинхронный набор: возвращает сразу (0 = набор начат), дальше этапы приходят в callback.
        // Вызывающий обязан держать ссылку на callback до конца набора.
        public static int Dial(string phonebook, string entry, string user, string password, DialCallback callback, out IntPtr handle)
        {
            var p = new RASDIALPARAMS();
            p.szEntryName = entry;
            p.szPhoneNumber = "";
            p.szCallbackNumber = "";
            p.szUserName = user;
            p.szPassword = password;
            p.szDomain = "";

            int fullSize = Marshal.SizeOf(typeof(RASDIALPARAMS));
            int legacySize = (int)Marshal.OffsetOf(typeof(RASDIALPARAMS), "dwIfIndex");
            int result = ERROR_INVALID_SIZE;
            handle = IntPtr.Zero;
            foreach (int size in new[] { fullSize, legacySize })
            {
                p.dwSize = size;
                handle = IntPtr.Zero;
                result = RasDial(IntPtr.Zero, phonebook, ref p, 0, callback, out handle);
                if (result != ERROR_INVALID_SIZE)
                    break;
                if (handle != IntPtr.Zero)
                    HangUp(handle);
            }
            p.szPassword = "";
            return result;
        }

        // Разрыв с ожиданием, пока RAS освободит дескриптор (как требует документация).
        public static void HangUp(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return;
            RasHangUp(handle);
            for (int i = 0; i < 50; i++)
            {
                int state, error;
                if (GetConnectStatus(handle, out state, out error) == ERROR_INVALID_HANDLE)
                    break;
                Thread.Sleep(100);
            }
        }

        public static IntPtr FindConnection(string entryName)
        {
            int size = Marshal.SizeOf(typeof(RASCONN));
            int count = 8;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int cb = size * count;
                IntPtr buf = Marshal.AllocHGlobal(cb);
                try
                {
                    Marshal.Copy(new byte[cb], 0, buf, cb);
                    Marshal.WriteInt32(buf, 0, size);
                    int n;
                    int r = RasEnumConnections(buf, ref cb, out n);
                    if (r == ERROR_BUFFER_TOO_SMALL)
                    {
                        count = cb / size + 1;
                        continue;
                    }
                    if (r != 0)
                        return IntPtr.Zero;
                    for (int i = 0; i < n; i++)
                    {
                        var c = (RASCONN)Marshal.PtrToStructure(new IntPtr(buf.ToInt64() + (long)i * size), typeof(RASCONN));
                        if (string.Equals(c.szEntryName, entryName, StringComparison.OrdinalIgnoreCase))
                            return c.hrasconn;
                    }
                    return IntPtr.Zero;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            return IntPtr.Zero;
        }

        // RASCONNSTATUSW: Win7+ = 608 байт, старый вариант = 564. Читаем поля по смещениям.
        public static int GetConnectStatus(IntPtr handle, out int state, out int error)
        {
            state = 0;
            error = 0;
            IntPtr buf = Marshal.AllocHGlobal(1024);
            try
            {
                foreach (int size in new[] { 608, 564 })
                {
                    Marshal.Copy(new byte[1024], 0, buf, 1024);
                    Marshal.WriteInt32(buf, 0, size);
                    int r = RasGetConnectStatus(handle, buf);
                    if (r == ERROR_INVALID_SIZE)
                        continue;
                    if (r == 0)
                    {
                        state = Marshal.ReadInt32(buf, 4);
                        error = Marshal.ReadInt32(buf, 8);
                    }
                    return r;
                }
                return ERROR_INVALID_SIZE;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        // RAS_STATS: 15 DWORD. Счётчики 32-битные, переполнение учитывает вызывающий код.
        public static int GetStatistics(IntPtr handle, out uint bytesSent, out uint bytesReceived, out uint durationMs)
        {
            bytesSent = bytesReceived = durationMs = 0;
            IntPtr buf = Marshal.AllocHGlobal(60);
            try
            {
                Marshal.Copy(new byte[60], 0, buf, 60);
                Marshal.WriteInt32(buf, 0, 60);
                int r = RasGetConnectionStatistics(handle, buf);
                if (r == 0)
                {
                    bytesSent = (uint)Marshal.ReadInt32(buf, 4);
                    bytesReceived = (uint)Marshal.ReadInt32(buf, 8);
                    durationMs = (uint)Marshal.ReadInt32(buf, 56);
                }
                return r;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        // RASPPPIPW: адрес клиента внутри туннеля (смещение 8).
        public static string GetIpAddress(IntPtr handle)
        {
            IntPtr buf = Marshal.AllocHGlobal(256);
            try
            {
                foreach (int size in new[] { 80, 72 })
                {
                    Marshal.Copy(new byte[256], 0, buf, 256);
                    Marshal.WriteInt32(buf, 0, size);
                    int cb = size;
                    int r = RasGetProjectionInfo(handle, RASP_PppIp, buf, ref cb);
                    if (r == ERROR_INVALID_SIZE)
                        continue;
                    if (r != 0 || Marshal.ReadInt32(buf, 4) != 0)
                        return null;
                    string ip = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 8));
                    return string.IsNullOrEmpty(ip) ? null : ip;
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        public static string ErrorString(int code)
        {
            var sb = new StringBuilder(512);
            if (RasGetErrorString(code, sb, sb.Capacity) == 0 && sb.Length > 0)
                return sb.ToString().Trim();
            return new Win32Exception(code).Message;
        }
    }
}
