using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace L2TPConnect
{
    // Последний релиз на GitHub: файл omnivpn-update.txt рядом с установщиками.
    internal sealed class UpdateInfo
    {
        public string Version = "";
        public string Notes = "";
        public string File = "";      // установщик для этой системы
        public string Sha256 = "";
        public long Size;

        public string Url
        {
            get { return Updater.ReleaseBase + "v" + Version + "/" + Uri.EscapeDataString(File); }
        }
    }

    internal static class Updater
    {
        public const string Repo = "furylicouz/OmniVPN-L2TP";
        public const string ManifestName = "omnivpn-update.txt";
        public const string ReleaseBase = "https://github.com/" + Repo + "/releases/download/";
        public const string ManifestUrl = "https://github.com/" + Repo + "/releases/latest/download/" + ManifestName;
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";

        static Updater()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        // Строки «ключ = значение»; notes может повторяться (по строке на пункт).
        public static UpdateInfo Parse(string text, string platform)
        {
            var u = new UpdateInfo();
            var notes = new StringBuilder();
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string value = line.Substring(eq + 1).Trim();
                if (key == "version") u.Version = value.TrimStart('v', 'V');
                else if (key == "notes") notes.Append(notes.Length > 0 ? "\n" : "").Append(value);
                else if (key == platform) u.File = value;
                else if (key == platform + ".sha256") u.Sha256 = value.ToLowerInvariant();
                else if (key == platform + ".size") long.TryParse(value, out u.Size);
            }
            u.Notes = notes.ToString();
            Version v;
            if (!System.Version.TryParse(u.Version, out v))
                throw new InvalidDataException("в описании релиза нет номера версии");
            if (u.File.Length == 0 || u.File.IndexOfAny(new[] { '/', '\\' }) >= 0 || u.Sha256.Length != 64 || u.Size <= 0)
                throw new InvalidDataException("в релизе " + u.Version + " нет установщика для Windows");
            return u;
        }

        public static bool IsNewer(string latest, string current)
        {
            Version a, b;
            return System.Version.TryParse(latest, out a) && System.Version.TryParse(current, out b) && Normalize(a) > Normalize(b);
        }

        static Version Normalize(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        }

        public static Task<UpdateInfo> CheckAsync()
        {
            return Task.Run(() =>
            {
                var req = Request(ManifestUrl);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return Parse(reader.ReadToEnd(), "windows");
            });
        }

        // Скачивает установщик во временную папку и сверяет размер и SHA-256 с описанием релиза.
        public static Task<string> DownloadAsync(UpdateInfo u, Action<long, long> progress, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "OmniVPNConnect", "update");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, u.File);
                string part = file + ".part";
                try
                {
                    var req = Request(u.Url);
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var input = resp.GetResponseStream())
                    using (var output = new FileStream(part, FileMode.Create, FileAccess.Write))
                    using (var sha = SHA256.Create())
                    {
                        var buf = new byte[65536];
                        long done = 0;
                        int n;
                        while ((n = input.Read(buf, 0, buf.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            output.Write(buf, 0, n);
                            sha.TransformBlock(buf, 0, n, null, 0);
                            done += n;
                            if (done > u.Size) throw new InvalidDataException("файл больше, чем указано в релизе");
                            progress(done, u.Size);
                        }
                        sha.TransformFinalBlock(buf, 0, 0);
                        if (done != u.Size) throw new InvalidDataException("файл загрузился не полностью");
                        if (Hex(sha.Hash) != u.Sha256) throw new InvalidDataException("контрольная сумма не совпала");
                    }
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(part, file);
                    return file;
                }
                catch
                {
                    try { File.Delete(part); } catch { }
                    throw;
                }
            }, ct);
        }

        // Тихая установка с правами администратора; после неё установщик сам запустит программу.
        // false — пользователь отказался в окне UAC.
        public static bool Install(string file)
        {
            var psi = new ProcessStartInfo(file, "/S /RELAUNCH");
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            try
            {
                Process.Start(psi).Dispose();
                return true;
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) return false;
                throw;
            }
        }

        static HttpWebRequest Request(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "OmniVPNConnect/" + Program.Version;
            req.Timeout = 20000;
            req.ReadWriteTimeout = 30000;
            req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            return req;
        }

        static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("0.0") + " МБ";
        }

        public static string Explain(Exception ex)
        {
            while (ex is AggregateException && ex.InnerException != null) ex = ex.InnerException;
            var web = ex as WebException;
            if (web != null)
            {
                var resp = web.Response as HttpWebResponse;
                if (resp != null && resp.StatusCode == HttpStatusCode.NotFound) return "на GitHub ещё нет опубликованных релизов";
                if (web.Status == WebExceptionStatus.NameResolutionFailure) return "нет доступа к github.com (имя не найдено)";
                if (web.Status == WebExceptionStatus.Timeout) return "github.com не ответил вовремя";
                return "нет связи с github.com";
            }
            return ex.Message;
        }
    }
}
