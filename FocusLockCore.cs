using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using Microsoft.Win32;

namespace FocusLock
{
    // ------------------------------------------------------------------
    // 常量
    // ------------------------------------------------------------------
    internal static class AppInfo
    {
        public const string Version = "2.0.1";
        public const string VersionTag = "v2.0.1";
        public const string Product = "FocusLock 专注锁";

        public const string NoticeText =
            "仅用作学习用途，请勿在互联网上传播。如有发现，必将严肃处理。" +
            "《刑法》第二百八十六条（破坏计算机信息系统罪）第三款规定：" +
            "故意传播计算机病毒等破坏性程序，影响计算机系统正常运行，后果严重的，" +
            "处五年以下有期徒刑或者拘役；后果特别严重的，处五年以上有期徒刑。";

        public const string HelpText =
            "此应用对您造成了困扰？如果有人将此程序在互联网上传播或已经锁定了您的电脑，" +
            "请向我们提供相关信息 email:huangwenbo@hgt46.wecom.work，" +
            "我们将在3个工作日里解决您的问题。";

        public const int GraceSeconds = 8;
        public const int MaxDurationMinutes = 10080;

        public const string MutexName = @"Local\FocusLock.SingleInstance";
        public const string ShowEventName = @"Local\FocusLock.Show";
        public const string WatchdogMutexName = @"Local\FocusLock.Watchdog";
        public const string TaskName = "FocusLock";
        public const string GuardTaskName = "FocusLockGuard";
    }

    // ------------------------------------------------------------------
    // 路径 / 日志
    // ------------------------------------------------------------------
    internal static class AppPaths
    {
        public static string Root;
        public static string StateFile;
        public static string SettingsFile;
        public static string LogFile;
        public static string MainPidFile;
        public static string WatchdogPidFile;
        public static string WallpaperBackupFile;
        public static string ExePath;
        public static string ExeDir;
        public static string ExeName;

        public static void Init()
        {
            Root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FocusLock");
            StateFile = Path.Combine(Root, "state.ini");
            SettingsFile = Path.Combine(Root, "settings.ini");
            LogFile = Path.Combine(Root, "focuslock.log");
            MainPidFile = Path.Combine(Root, "main.pid");
            WatchdogPidFile = Path.Combine(Root, "watchdog.pid");
            WallpaperBackupFile = Path.Combine(Root, "desktop.bak");

            try
            {
                ExePath = Process.GetCurrentProcess().MainModule.FileName;
            }
            catch
            {
                ExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FocusLock.exe");
            }
            ExeDir = Path.GetDirectoryName(ExePath);
            ExeName = Path.GetFileNameWithoutExtension(ExePath);
        }

        public static void EnsureRoot()
        {
            try
            {
                Directory.CreateDirectory(Root);
            }
            catch
            {
            }
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();
        private static int _writes;

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    AppPaths.EnsureRoot();
                    _writes++;
                    if (_writes % 50 == 0 && File.Exists(AppPaths.LogFile))
                    {
                        try
                        {
                            if (new FileInfo(AppPaths.LogFile).Length > 4 * 1024 * 1024)
                            {
                                File.Copy(AppPaths.LogFile, AppPaths.LogFile + ".old", true);
                                File.Delete(AppPaths.LogFile);
                            }
                        }
                        catch
                        {
                        }
                    }

                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                                  " [" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "] " +
                                  message + Environment.NewLine;
                    byte[] bytes = new UTF8Encoding(false).GetBytes(line);
                    using (FileStream fs = new FileStream(AppPaths.LogFile, FileMode.Append,
                        FileAccess.Write, FileShare.ReadWrite))
                    {
                        fs.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            catch
            {
            }
        }
    }

    // ------------------------------------------------------------------
    // 设置（全部开关）
    // ------------------------------------------------------------------
    internal sealed class AppSettings
    {
        public bool Autostart;
        public bool Watchdog;
        public bool Enforce;
        public bool TopMost;
        public bool Wallpaper;
        public bool Icon;
        public bool AllowHide;

        public static AppSettings CreateDefault()
        {
            AppSettings s = new AppSettings();
            s.Autostart = false;
            s.Watchdog = true;
            s.Enforce = true;
            s.TopMost = true;
            s.Wallpaper = true;
            s.Icon = true;
            s.AllowHide = true;
            return s;
        }

        public AppSettings Clone()
        {
            AppSettings s = new AppSettings();
            s.Autostart = Autostart;
            s.Watchdog = Watchdog;
            s.Enforce = Enforce;
            s.TopMost = TopMost;
            s.Wallpaper = Wallpaper;
            s.Icon = Icon;
            s.AllowHide = AllowHide;
            return s;
        }

        public static AppSettings Load()
        {
            AppSettings s = CreateDefault();
            try
            {
                if (!File.Exists(AppPaths.SettingsFile))
                {
                    return s;
                }
                foreach (string raw in File.ReadAllLines(AppPaths.SettingsFile, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                    {
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    bool b = val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
                    if (key == "autostart") s.Autostart = b;
                    else if (key == "watchdog") s.Watchdog = b;
                    else if (key == "enforce") s.Enforce = b;
                    else if (key == "topmost") s.TopMost = b;
                    else if (key == "wallpaper") s.Wallpaper = b;
                    else if (key == "icon") s.Icon = b;
                    else if (key == "allowhide") s.AllowHide = b;
                }
            }
            catch
            {
            }
            return s;
        }

        public bool Save()
        {
            try
            {
                AppPaths.EnsureRoot();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# FocusLock " + AppInfo.VersionTag + " settings");
                sb.AppendLine("Autostart=" + (Autostart ? "1" : "0"));
                sb.AppendLine("Watchdog=" + (Watchdog ? "1" : "0"));
                sb.AppendLine("Enforce=" + (Enforce ? "1" : "0"));
                sb.AppendLine("TopMost=" + (TopMost ? "1" : "0"));
                sb.AppendLine("Wallpaper=" + (Wallpaper ? "1" : "0"));
                sb.AppendLine("Icon=" + (Icon ? "1" : "0"));
                sb.AppendLine("AllowHide=" + (AllowHide ? "1" : "0"));
                string tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                File.Copy(tmp, AppPaths.SettingsFile, true);
                File.Delete(tmp);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    // ------------------------------------------------------------------
    // 限制状态
    // ------------------------------------------------------------------
    internal sealed class LockState
    {
        public DateTime StartUtc = DateTime.MinValue;
        public DateTime EndUtc = DateTime.MinValue;
        public string PasswordSalt = "";
        public string PasswordHash = "";

        public static LockState Empty()
        {
            return new LockState();
        }

        public LockState Clone()
        {
            LockState st = new LockState();
            st.StartUtc = StartUtc;
            st.EndUtc = EndUtc;
            st.PasswordSalt = PasswordSalt;
            st.PasswordHash = PasswordHash;
            return st;
        }

        public bool IsActive(DateTime now)
        {
            return EndUtc > StartUtc && now < EndUtc;
        }

        public bool IsLocked(DateTime now)
        {
            return IsActive(now) && now >= StartUtc;
        }

        public bool IsScheduled(DateTime now)
        {
            return IsActive(now) && now < StartUtc;
        }

        public string ToFileText()
        {
            if (!(EndUtc > StartUtc))
            {
                return "Version=2" + Environment.NewLine;
            }
            return "Version=2" + Environment.NewLine +
                   "StartUtc=" + StartUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + Environment.NewLine +
                   "EndUtc=" + EndUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + Environment.NewLine +
                   "PasswordSalt=" + PasswordSalt + Environment.NewLine +
                   "PasswordHash=" + PasswordHash + Environment.NewLine;
        }

        public static bool TryParse(string text, out LockState state)
        {
            state = Empty();
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string startText = null;
            string endText = null;
            string salt = "";
            string hash = "";
            string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (string.Equals(key, "StartUtc", StringComparison.OrdinalIgnoreCase)) startText = val;
                else if (string.Equals(key, "EndUtc", StringComparison.OrdinalIgnoreCase)) endText = val;
                else if (string.Equals(key, "PasswordSalt", StringComparison.OrdinalIgnoreCase)) salt = val;
                else if (string.Equals(key, "PasswordHash", StringComparison.OrdinalIgnoreCase)) hash = val;
            }

            if (string.IsNullOrEmpty(startText) || string.IsNullOrEmpty(endText))
            {
                return false;
            }

            DateTime start;
            DateTime end;
            if (!DateTime.TryParse(startText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out start) ||
                !DateTime.TryParse(endText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out end))
            {
                return false;
            }

            start = start.ToUniversalTime();
            end = end.ToUniversalTime();
            if (end <= start)
            {
                return false;
            }

            state.StartUtc = start;
            state.EndUtc = end;
            state.PasswordSalt = salt;
            state.PasswordHash = hash;
            return true;
        }
    }

    // ------------------------------------------------------------------
    // 密码：SHA256 + 盐。永久密码只保存加盐散列，并分段异或混淆。
    // ------------------------------------------------------------------
    internal static class Secrets
    {
        private static readonly string[] MasterSaltParts =
        {
            "cVlWQUV7VwJdS1YDC1APCgVQ", "EFgVVkdzCldBRnQNVlg="
        };

        private static readonly string[] MasterHashParts =
        {
            "J1xTFER8DVcKSlMFAwJbA1UG", "FQ1MXRIjCwZMF38NBVMbC1FT",
            "BQoCVFYXDkBXR3YMU0xEfQ0F", "X01RAFZRC14CXA=="
        };

        private static readonly string[] EndSaltParts =
        {
            "IwpTFhZ+DAENGwcGUw0CDAcG", "FQ5ADkN/CVNMRnQKVVs="
        };

        private static readonly string[] EndHashParts =
        {
            "cVhTEEZ9DVZcHVMCU1EMXFFc", "RFgSDkJ0WlVNR35ZVl8XUQcL",
            "BQ0IUgBFCEJdEXYNU00RKAoC", "XxxXAABRWApXVg=="
        };

        private const string ObfKey = "FocusLock/2024::desktop";

        private static string _masterSalt;
        private static string _masterHash;
        private static string _endSalt;
        private static string _endHash;

        public static string NewSalt()
        {
            byte[] buf = new byte[16];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(buf);
            }
            StringBuilder sb = new StringBuilder(buf.Length * 2);
            foreach (byte b in buf)
            {
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static string Hash(string salt, string password)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] data = Encoding.UTF8.GetBytes((salt ?? "") + "|" + (password ?? ""));
                byte[] digest = sha.ComputeHash(data);
                StringBuilder sb = new StringBuilder(digest.Length * 2);
                foreach (byte b in digest)
                {
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }

        public static bool Verify(string salt, string hash, string password)
        {
            if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt) || password == null)
            {
                return false;
            }
            string candidate = Hash(salt, password);
            return FixedEquals(candidate, hash);
        }

        private static bool FixedEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }

        private static string Decode(string[] parts)
        {
            string b64 = string.Concat(parts);
            byte[] raw = Convert.FromBase64String(b64);
            byte[] key = Encoding.UTF8.GetBytes(ObfKey);
            char[] chars = new char[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                chars[i] = (char)(raw[i] ^ key[i % key.Length]);
            }
            return new string(chars);
        }

        private static void EnsureLoaded()
        {
            if (_masterHash != null)
            {
                return;
            }
            _masterSalt = Decode(MasterSaltParts);
            _masterHash = Decode(MasterHashParts);
            _endSalt = Decode(EndSaltParts);
            _endHash = Decode(EndHashParts);
        }

        public static bool SelfCheck()
        {
            EnsureLoaded();
            return _masterHash.Length == 64 && _endHash.Length == 64 &&
                   _masterSalt.Length == 32 && _endSalt.Length == 32 &&
                   !FixedEquals(_masterHash, _endHash) &&
                   !VerifyMaster("definitely-not-the-password");
        }
        public static bool VerifyMaster(string password)
        {
            EnsureLoaded();
            return Verify(_masterSalt, _masterHash, password);
        }

        public static bool VerifyEndOnly(string password)
        {
            EnsureLoaded();
            return Verify(_endSalt, _endHash, password);
        }

        public static bool VerifyLockPassword(LockState st, string password)
        {
            if (st == null)
            {
                return false;
            }
            return Verify(st.PasswordSalt, st.PasswordHash, password);
        }

        // 提前结束：本次限制密码、仅结束密码、主密码均可。
        public static bool VerifyCanEnd(LockState st, string password)
        {
            return VerifyLockPassword(st, password) ||
                   VerifyEndOnly(password) ||
                   VerifyMaster(password);
        }

        // 管理类操作（设置、卸载）：只认主密码。
        public static bool VerifyAdmin(string password)
        {
            return VerifyMaster(password);
        }
    }

    // ------------------------------------------------------------------
    // 桌面壁纸
    // ------------------------------------------------------------------
    internal static class Desktop
    {
        private const uint SPI_SETDESKWALLPAPER = 0x0014;
        private const uint SPIF_UPDATEINIFILE = 0x01;
        private const uint SPIF_SENDCHANGE = 0x02;
        private const string DesktopKey = @"Control Panel\Desktop";

        // JPEG 直接设置失败时转成的临时 BMP。
        private static string _tempWallpaper;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, string value, uint winIni);

        public static string CurrentWallpaper()
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\" + DesktopKey, "Wallpaper", "");
                return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            catch
            {
                return "";
            }
        }

        private static string ReadStyle(string name, string fallback)
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\" + DesktopKey, name, fallback);
                return v == null ? fallback : Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private static void SetValue(string name, string value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(DesktopKey, true))
                {
                    if (k != null)
                    {
                        k.SetValue(name, value, RegistryValueKind.String);
                    }
                }
            }
            catch
            {
            }
        }

        public static bool BackupExists()
        {
            return !string.IsNullOrEmpty(AppPaths.WallpaperBackupFile) && File.Exists(AppPaths.WallpaperBackupFile);
        }

        private static void WriteBackup()
        {
            try
            {
                AppPaths.EnsureRoot();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Wallpaper=" + CurrentWallpaper());
                sb.AppendLine("Style=" + ReadStyle("WallpaperStyle", "10"));
                sb.AppendLine("Tile=" + ReadStyle("TileWallpaper", "0"));
                File.WriteAllText(AppPaths.WallpaperBackupFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        // 返回 null 表示成功，否则返回错误信息。
        public static string ApplyWallpaper(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            {
                return "找不到壁纸文件";
            }
            try
            {
                using (Image probe = Image.FromFile(imagePath))
                {
                    if (probe.Width <= 0)
                    {
                        return "壁纸文件无效";
                    }
                }
            }
            catch (Exception ex)
            {
                return "壁纸无法读取：" + ex.Message;
            }

            if (!BackupExists())
            {
                WriteBackup();
            }

            SetValue("WallpaperStyle", "10");
            SetValue("TileWallpaper", "0");

            bool ok = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, imagePath,
                SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

            // 个别系统 / 显卡驱动对 JPEG、PNG 直接设置壁纸不生效：
            // 转成 24 位 BMP 再设一次，保证 JPEG 素材也能用。
            if (!ok)
            {
                string bmp = ConvertToBmp(imagePath);
                if (bmp != null)
                {
                    ok = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, bmp,
                        SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                    if (ok)
                    {
                        _tempWallpaper = bmp;
                    }
                }
            }

            if (!ok)
            {
                return "系统拒绝更换壁纸";
            }
            Log.Write("壁纸已更换: " + imagePath);
            return null;
        }

        private static string ConvertToBmp(string imagePath)
        {
            try
            {
                AppPaths.EnsureRoot();
                string bmp = Path.Combine(AppPaths.Root, "wallpaper.bmp");
                using (Image src = Image.FromFile(imagePath))
                {
                    int w = Math.Max(1, src.Width);
                    int h = Math.Max(1, src.Height);
                    using (Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                    {
                        using (Graphics g = Graphics.FromImage(b))
                        {
                            g.Clear(Color.Black);
                            g.DrawImage(src, 0, 0, w, h);
                        }
                        b.Save(bmp, ImageFormat.Bmp);
                    }
                }
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        public static void Restore()
        {
            if (!BackupExists())
            {
                return;
            }
            string path = "";
            try
            {
                foreach (string raw in File.ReadAllLines(AppPaths.WallpaperBackupFile, Encoding.UTF8))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }
                    string k = raw.Substring(0, eq).Trim();
                    string v = raw.Substring(eq + 1).Trim();
                    if (string.Equals(k, "Wallpaper", StringComparison.OrdinalIgnoreCase)) path = v;
                    else if (string.Equals(k, "Style", StringComparison.OrdinalIgnoreCase)) SetValue("WallpaperStyle", v);
                    else if (string.Equals(k, "Tile", StringComparison.OrdinalIgnoreCase)) SetValue("TileWallpaper", v);
                }
            }
            catch
            {
            }

            try
            {
                SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            }
            catch
            {
            }

            try
            {
                File.Delete(AppPaths.WallpaperBackupFile);
            }
            catch
            {
            }

            try
            {
                string temp = Path.Combine(AppPaths.Root, "wallpaper.bmp");
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
                _tempWallpaper = null;
            }
            catch
            {
            }
            Log.Write("壁纸已恢复: " + path);
        }

        // 启动时：没有生效的限制却留有备份，说明上次异常退出，恢复之。
        public static void Recover()
        {
            if (BackupExists())
            {
                Restore();
            }
            if (DesktopIcons.BackupExists())
            {
                string iconError;
                DesktopIcons.Restore(out iconError);
            }
        }

        // 把手动应用的壁纸存档转为“固定存档”，这样下次启动不会自动还原。
        public static void Pin()
        {
            try
            {
                string pinned = Path.Combine(AppPaths.Root, "desktop.pinned");
                if (!File.Exists(AppPaths.WallpaperBackupFile))
                {
                    return;
                }
                if (File.Exists(pinned))
                {
                    File.Delete(AppPaths.WallpaperBackupFile);
                    return;
                }
                File.Move(AppPaths.WallpaperBackupFile, pinned);
            }
            catch
            {
            }
        }

        // 恢复手动应用的壁纸（用固定存档）。
        public static bool RestorePinned()
        {
            string pinned = Path.Combine(AppPaths.Root, "desktop.pinned");
            if (!File.Exists(pinned))
            {
                return false;
            }
            try
            {
                if (File.Exists(AppPaths.WallpaperBackupFile))
                {
                    File.Delete(AppPaths.WallpaperBackupFile);
                }
                File.Move(pinned, AppPaths.WallpaperBackupFile);
            }
            catch
            {
                return false;
            }
            Restore();
            return true;
        }
    }

    // ------------------------------------------------------------------
    // 桌面快捷方式图标：锁定期间把桌面上所有 .lnk 的图标换成 （2）
    // ------------------------------------------------------------------
    internal static class DesktopIcons
    {
        private const string BackupName = "desktop-icons.ini";
        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNE_UPDATEITEM = 0x00002000;
        private const uint SHCNF_PATHW = 0x0005;
        private const uint SHCNF_IDLIST = 0x0000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHChangeNotify")]
        private static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);

        [DllImport("shell32.dll", EntryPoint = "SHChangeNotify")]
        private static extern void SHChangeNotifyIdList(uint eventId, uint flags, IntPtr item1, IntPtr item2);

        private sealed class OpResult
        {
            public bool Ok;
            public string Error;
            public int Changed;
            public int Failed;
        }

        private static string BackupPath
        {
            get { return Path.Combine(AppPaths.Root, BackupName); }
        }

        public static bool BackupExists()
        {
            return File.Exists(BackupPath);
        }

        // 手动应用的图标存档转为固定存档，避免下次启动被自动还原。
        public static void Pin()
        {
            try
            {
                string pinned = Path.Combine(AppPaths.Root, "desktop-icons.pinned");
                if (!File.Exists(BackupPath))
                {
                    return;
                }
                if (File.Exists(pinned))
                {
                    File.Delete(BackupPath);
                    return;
                }
                File.Move(BackupPath, pinned);
            }
            catch
            {
            }
        }

        public static bool RestorePinned(out string error)
        {
            error = null;
            string pinned = Path.Combine(AppPaths.Root, "desktop-icons.pinned");
            if (!File.Exists(pinned))
            {
                return false;
            }
            try
            {
                if (File.Exists(BackupPath))
                {
                    File.Delete(BackupPath);
                }
                File.Move(pinned, BackupPath);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            return Restore(out error);
        }

        // 把 imagePath（通常是 （2）.jpg）生成 .ico，再把桌面上所有快捷方式的图标换成它。
        public static bool Apply(string imagePath, out string error)
        {
            OpResult r = new OpResult();
            RunSta(delegate(object state) { ApplyCore(imagePath, (OpResult)state); }, r);
            error = r.Error;
            return r.Ok;
        }

        public static bool Restore(out string error)
        {
            OpResult r = new OpResult();
            RunSta(delegate(object state) { RestoreCore((OpResult)state); }, r);
            error = r.Error;
            return r.Ok;
        }

        private static void RunSta(WaitCallback body, object state)
        {
            Thread t = new Thread(new ParameterizedThreadStart(delegate(object s) { body(s); }));
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Name = "FocusLock.DesktopIcons";
            t.Start(state);
            if (!t.Join(20000))
            {
                Log.Write("桌面图标操作超时");
            }
        }

        private static void ApplyCore(string imagePath, OpResult r)
        {
            try
            {
                if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                {
                    r.Error = "找不到 （2） 图片";
                    return;
                }

                AppPaths.EnsureRoot();
                string ico = Path.Combine(AppPaths.Root, "desktop-icon.ico");
                string icoError;
                if (!WriteIco(imagePath, ico, out icoError))
                {
                    r.Error = "生成图标失败：" + icoError;
                    return;
                }

                List<string> links = EnumerateShortcuts();
                if (links.Count == 0)
                {
                    r.Error = "桌面上没有快捷方式";
                    return;
                }

                Dictionary<string, string> backup = LoadBackup();
                string want = ico + ",0";

                foreach (string lnk in links)
                {
                    try
                    {
                        string current = ReadIcon(lnk);
                        if (current == null)
                        {
                            r.Failed++;
                            continue;
                        }
                        if (string.Equals(current, want, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (!backup.ContainsKey(lnk))
                        {
                            backup[lnk] = current;
                        }
                        WriteIcon(lnk, want);
                        r.Changed++;
                        RefreshItem(lnk);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("设置快捷方式图标失败 " + lnk + ": " + ex.Message);
                        r.Failed++;
                    }
                }

                SaveBackup(backup);
                RefreshAll();
                Log.Write("桌面图标已替换（" + r.Changed + " 个，失败 " + r.Failed + "）");
                r.Ok = true;
                if (r.Failed > 0)
                {
                    r.Error = "有 " + r.Failed + " 个快捷方式未能修改";
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                Log.Write("桌面图标替换失败: " + ex);
            }
        }

        private static void RestoreCore(OpResult r)
        {
            try
            {
                Dictionary<string, string> backup = LoadBackup();
                if (backup.Count == 0)
                {
                    if (File.Exists(BackupPath))
                    {
                        try { File.Delete(BackupPath); } catch { }
                    }
                    r.Ok = true;
                    return;
                }

                foreach (KeyValuePair<string, string> kv in backup)
                {
                    try
                    {
                        if (!File.Exists(kv.Key))
                        {
                            continue;
                        }
                        WriteIcon(kv.Key, kv.Value);
                        r.Changed++;
                        RefreshItem(kv.Key);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("恢复快捷方式图标失败 " + kv.Key + ": " + ex.Message);
                        r.Failed++;
                    }
                }

                try { File.Delete(BackupPath); } catch { }
                RefreshAll();
                Log.Write("桌面图标已恢复（" + r.Changed + " 个）");
                r.Ok = true;
                if (r.Failed > 0)
                {
                    r.Error = "有 " + r.Failed + " 个未能恢复";
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                Log.Write("恢复桌面图标失败: " + ex);
            }
        }

        private static List<string> EnumerateShortcuts()
        {
            List<string> list = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] dirs = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };

            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    continue;
                }
                try
                {
                    foreach (string f in Directory.GetFiles(dir, "*.lnk"))
                    {
                        if (seen.Add(f))
                        {
                            list.Add(f);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("枚举桌面失败 " + dir + ": " + ex.Message);
                }
            }
            return list;
        }

        private static object NewShell()
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null)
            {
                throw new InvalidOperationException("系统不支持 WScript.Shell");
            }
            return Activator.CreateInstance(t);
        }

        private static string ReadIcon(string lnk)
        {
            object shell = NewShell();
            try
            {
                dynamic sc = ((dynamic)shell).CreateShortcut(lnk);
                return (string)sc.IconLocation;
            }
            finally
            {
                ReleaseCom(shell);
            }
        }

        private static void WriteIcon(string lnk, string icon)
        {
            object shell = NewShell();
            try
            {
                dynamic sc = ((dynamic)shell).CreateShortcut(lnk);
                sc.IconLocation = icon ?? "";
                sc.Save();
            }
            finally
            {
                ReleaseCom(shell);
            }
        }

        private static void ReleaseCom(object o)
        {
            try
            {
                if (o != null && Marshal.IsComObject(o))
                {
                    Marshal.ReleaseComObject(o);
                }
            }
            catch
            {
            }
        }

        private static Dictionary<string, string> LoadBackup()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(BackupPath))
                {
                    return map;
                }
                string path = null;
                foreach (string raw in File.ReadAllLines(BackupPath, Encoding.UTF8))
                {
                    if (raw.StartsWith("Path=", StringComparison.Ordinal))
                    {
                        path = raw.Substring(5);
                    }
                    else if (raw.StartsWith("Icon=", StringComparison.Ordinal) && path != null)
                    {
                        map[path] = raw.Substring(5);
                        path = null;
                    }
                }
            }
            catch
            {
            }
            return map;
        }

        private static void SaveBackup(Dictionary<string, string> map)
        {
            try
            {
                AppPaths.EnsureRoot();
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in map)
                {
                    sb.AppendLine("Path=" + kv.Key);
                    sb.AppendLine("Icon=" + kv.Value);
                }
                File.WriteAllText(BackupPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Write("保存桌面图标备份失败: " + ex.Message);
            }
        }

        private static void RefreshItem(string path)
        {
            try { SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, path, IntPtr.Zero); } catch { }
        }

        private static void RefreshAll()
        {
            try { SHChangeNotifyIdList(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); } catch { }
        }

        private static bool WriteIco(string source, string icoPath, out string error)
        {
            error = null;
            try
            {
                int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
                byte[][] blobs = new byte[sizes.Length][];
                using (Image src = Image.FromFile(source))
                {
                    for (int i = 0; i < sizes.Length; i++)
                    {
                        using (Bitmap bmp = RenderSquare(src, sizes[i]))
                        using (MemoryStream ms = new MemoryStream())
                        {
                            bmp.Save(ms, ImageFormat.Png);
                            blobs[i] = ms.ToArray();
                        }
                    }
                }

                using (FileStream fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write))
                using (BinaryWriter w = new BinaryWriter(fs))
                {
                    w.Write((ushort)0);
                    w.Write((ushort)1);
                    w.Write((ushort)sizes.Length);
                    int offset = 6 + 16 * sizes.Length;
                    for (int i = 0; i < sizes.Length; i++)
                    {
                        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                        w.Write((byte)0);
                        w.Write((byte)0);
                        w.Write((ushort)1);
                        w.Write((ushort)32);
                        w.Write((uint)blobs[i].Length);
                        w.Write((uint)offset);
                        offset += blobs[i].Length;
                    }
                    for (int i = 0; i < sizes.Length; i++)
                    {
                        w.Write(blobs[i]);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static Bitmap RenderSquare(Image src, int size)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                float scale = Math.Min((float)size / src.Width, (float)size / src.Height);
                int w = Math.Max(1, (int)Math.Round(src.Width * scale));
                int h = Math.Max(1, (int)Math.Round(src.Height * scale));
                g.DrawImage(src, new Rectangle((size - w) / 2, (size - h) / 2, w, h));
            }
            return bmp;
        }
    }

    // ------------------------------------------------------------------
    // 素材查找：同目录下的 （1）.jpg/png、（2）.jpg/png
    // ------------------------------------------------------------------
    internal static class Assets
    {
        private static readonly string[] WallpaperBase = { "（1）", "(1)", "1" };
        private static readonly string[] IconBase = { "（2）", "(2)", "2" };
        // JPEG 优先：同目录同时有 （1）.jpg 和示例 （1）.png 时用前者。
        private static readonly string[] Extensions =
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp"
        };

        public static string FindWallpaper()
        {
            string over = Environment.GetEnvironmentVariable("FOCUSLOCK_WALLPAPER");
            if (!string.IsNullOrEmpty(over) && File.Exists(over))
            {
                return over;
            }
            return Find(WallpaperBase);
        }

        public static string FindIcon()
        {
            string over = Environment.GetEnvironmentVariable("FOCUSLOCK_ICON");
            if (!string.IsNullOrEmpty(over) && File.Exists(over))
            {
                return over;
            }
            return Find(IconBase);
        }

        private static string Find(string[] bases)
        {
            string dir = AppPaths.ExeDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return null;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch
            {
                return null;
            }

            // 先按格式优先级（JPEG 整体优先），再按文件名写法（全角 → 半角）。
            // 这样即使目录里同时有示例 （1）.png 和用户自己放的 (1).jpeg，
            // 也会优先用 JPEG，符合“兼容/优先 JPEG”的预期。
            foreach (string e in Extensions)
            {
                foreach (string b in bases)
                {
                    string target = b + e;
                    foreach (string f in files)
                    {
                        if (string.Equals(Path.GetFileName(f), target, StringComparison.OrdinalIgnoreCase))
                        {
                            return f;
                        }
                    }
                }
            }
            return null;
        }

        // 任意图片格式（JPEG / PNG / BMP）统一缩放成 256×256 的图标，
        // 避免把整张照片原尺寸塞进任务栏导致图标过大或显示异常。
        public static Icon LoadIcon(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }
            IntPtr handle = IntPtr.Zero;
            try
            {
                using (Image src = Image.FromFile(path))
                using (Bitmap canvas = new Bitmap(256, 256, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(canvas))
                    {
                        g.Clear(Color.Transparent);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        float scale = Math.Min(256f / src.Width, 256f / src.Height);
                        int w = Math.Max(1, (int)Math.Round(src.Width * scale));
                        int h = Math.Max(1, (int)Math.Round(src.Height * scale));
                        g.DrawImage(src, new Rectangle((256 - w) / 2, (256 - h) / 2, w, h));
                    }
                    handle = canvas.GetHicon();
                }
                using (Icon temp = Icon.FromHandle(handle))
                {
                    return (Icon)temp.Clone();
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    Native.DestroyIcon(handle);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // 开机自启（计划任务）
    // ------------------------------------------------------------------
    internal static class Tasks
    {
        public static bool Enable(out string error)
        {
            error = null;
            string logonError;
            bool logon = Create(AppInfo.TaskName, false, out logonError);
            if (!logon)
            {
                error = "登录任务创建失败：" + Trim(logonError);
                return false;
            }

            string guardError;
            bool guard = Create(AppInfo.GuardTaskName, true, out guardError);
            if (!guard)
            {
                error = "守护任务创建失败：" + Trim(guardError);
                return false;
            }

            DeleteRunKey();
            Log.Write("开机自启已开启: " + AppPaths.ExePath);
            return true;
        }

        public static bool Disable(out string error)
        {
            error = null;
            bool ok = true;
            string output;

            int code = Schtasks("/Delete /TN \"" + AppName(AppInfo.TaskName) + "\" /F", out output);
            if (code != 0 && Query(AppName(AppInfo.TaskName)) != 0)
            {
                ok = false;
                error = "删除登录任务失败：" + Trim(output);
            }

            code = Schtasks("/Delete /TN \"" + AppName(AppInfo.GuardTaskName) + "\" /F", out output);
            if (code != 0 && Query(AppName(AppInfo.GuardTaskName)) != 0)
            {
                ok = false;
                error = "删除守护任务失败：" + Trim(output);
            }

            DeleteRunKey();
            Log.Write("开机自启已关闭");
            return ok;
        }

        public static string CurrentTaskName(bool guard)
        {
            return AppName(guard ? AppInfo.GuardTaskName : AppInfo.TaskName);
        }

        // 两个版本共用同一组任务名：后注册的按钮覆盖前一个，
        // 也保证 Key 无论自己叫什么名字都能删掉它们。
        private static string AppName(string baseName)
        {
            return baseName;
        }

        private static bool Create(string name, bool guard, out string error)
        {
            error = "";
            string xmlPath = Path.Combine(Path.GetTempPath(),
                "focuslock_" + (guard ? "guard" : "logon") + "_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(xmlPath, BuildXml(guard), Encoding.Unicode);
                string output;
                int code = Schtasks("/Create /TN \"" + AppName(name) + "\" /XML \"" + xmlPath + "\" /F", out output);
                if (code == 0)
                {
                    return true;
                }
                error = "退出码 " + code.ToString(CultureInfo.InvariantCulture) + " " + output;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                DeleteFile(xmlPath);
            }
        }

        private static string BuildXml(bool guard)
        {
            string sid = WindowsIdentity.GetCurrent().User.Value;
            string command = XmlEscape(AppPaths.ExePath);
            string arguments = guard ? "--guard" : "--autostart";
            string description = guard
                ? "FocusLock guard: restarts the lockdown console if it was terminated"
                : "FocusLock: opens the password-protected lockdown console at logon";
            string level = IsElevated() ? "HighestAvailable" : "LeastPrivilege";

            string trigger;
            if (guard)
            {
                string start = DateTime.Now.AddMinutes(1)
                    .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
                trigger = "<TimeTrigger>" +
                          "<StartBoundary>" + start + "</StartBoundary>" +
                          "<Repetition><Interval>PT1M</Interval><Duration>P3650D</Duration>" +
                          "<StopAtDurationEnd>false</StopAtDurationEnd></Repetition>" +
                          "</TimeTrigger>";
            }
            else
            {
                trigger = "<LogonTrigger><Enabled>true</Enabled><Delay>PT5S</Delay></LogonTrigger>";
            }

            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" +
                   "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                   "<RegistrationInfo><Description>" + XmlEscape(description) + "</Description></RegistrationInfo>" +
                   "<Principals><Principal id=\"Author\">" +
                   "<UserId>" + XmlEscape(sid) + "</UserId>" +
                   "<LogonType>InteractiveToken</LogonType>" +
                   "<RunLevel>" + level + "</RunLevel>" +
                   "</Principal></Principals>" +
                   "<Settings>" +
                   "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                   "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                   "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>" +
                   "<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
                   "<StartWhenAvailable>true</StartWhenAvailable>" +
                   "<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
                   "</Settings>" +
                   "<Triggers>" + trigger + "</Triggers>" +
                   "<Actions Context=\"Author\"><Exec>" +
                   "<Command>" + command + "</Command>" +
                   "<Arguments>" + arguments + "</Arguments>" +
                   "</Exec></Actions>" +
                   "</Task>";
        }

        public static int Schtasks(string arguments, out string output)
        {
            output = "";
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
                ProcessStartInfo psi = new ProcessStartInfo(path, arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);

                using (Process p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        output = "无法启动 schtasks.exe";
                        return -1;
                    }
                    Locker.ProtectChild(p.Id);
                    try
                    {
                        string stdout = p.StandardOutput.ReadToEnd();
                        string stderr = p.StandardError.ReadToEnd();
                        if (!p.WaitForExit(15000))
                        {
                            try
                            {
                                p.Kill();
                            }
                            catch
                            {
                            }
                            output = "schtasks 超时";
                            return -1;
                        }
                        output = (stdout + " " + stderr).Trim();
                        return p.ExitCode;
                    }
                    finally
                    {
                        Locker.UnprotectChild(p.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -1;
            }
        }

        private static int Query(string name)
        {
            string output;
            int code = Schtasks("/Query /TN \"" + name + "\"", out output);
            if (code == 0)
            {
                return 1;
            }
            return code == -1 ? -1 : 0;
        }

        private static bool TryDeleteRunKey()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        key.DeleteValue("FocusLock", false);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void DeleteRunKey()
        {
            TryDeleteRunKey();
        }

        public static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private static string XmlEscape(string text)
        {
            if (text == null)
            {
                return "";
            }
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                       .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        private static string Trim(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "未知错误";
            }
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length > 160)
            {
                text = text.Substring(0, 160) + "...";
            }
            return text;
        }

        private static void DeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }

    // ------------------------------------------------------------------
    // 状态快照
    // ------------------------------------------------------------------
    internal struct StatusSnapshot
    {
        public string Phase;
        public DateTime StartUtc;
        public DateTime EndUtc;
        public bool Active;
        public bool Locked;
        public bool Scheduled;
        public int Killed;
        public int GraceRemaining;
        public double UntilStartSeconds;
        public double UntilEndSeconds;

        public string Title()
        {
            if (Locked)
            {
                if (GraceRemaining > 0)
                {
                    return AppInfo.Product + " | " + GraceRemaining.ToString(CultureInfo.InvariantCulture) + " 秒后开始清理";
                }
                return AppInfo.Product + " | 锁定中 | 剩余 " + TimeText.FormatRemaining(
                    TimeSpan.FromSeconds(UntilEndSeconds)) +
                    " | 已结束 " + Killed.ToString(CultureInfo.InvariantCulture);
            }
            if (Scheduled)
            {
                return AppInfo.Product + " | 计划 " +
                    StartUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " 开始";
            }
            return AppInfo.Product + " | 空闲";
        }
    }

    // ------------------------------------------------------------------
    // 引擎
    // ------------------------------------------------------------------
    internal static class Locker
    {
        private static readonly object Gate = new object();
        private static readonly object NoticeGate = new object();
        private static readonly object ChildGate = new object();
        private static readonly Queue<string> Notices = new Queue<string>();
        private static readonly HashSet<int> ChildPids = new HashSet<int>();

        private static LockState _state = LockState.Empty();
        private static AppSettings _settings = AppSettings.CreateDefault();
        private static string _phase = "idle";
        private static DateTime _graceUntilUtc;
        private static int _killedTotal;
        private static DateTime _lastKeyCheck = DateTime.MinValue;
        private static Thread _worker;
        private static volatile bool _stop;
        private static bool _started;
        private static int _watchdogPid;

        private static readonly HashSet<string> KeepAliveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "360sd", "360rp", "360tray", "360safe", "360bpsvc", "360bdoctor",
            "zhudongfangyu", "qqpcmgr", "qqpcrtp", "qhsafe", "qhtray", "wsctrl",
            "usysdiag", "hipsdaemon", "hipstray", "avp", "avpui", "kxe", "kxetray",
            "kavstart", "msmpeng", "nisrv", "securityhealthservice", "securityhealthsystray",
            "sgrmbroker", "smartscreen", "secmon", "sgtool", "sogoucloud", "sogouime", "sginput"
        };

        private static readonly HashSet<string> CoreSystemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "system", "idle", "registry", "memory compression", "secure system"
        };

        private static readonly HashSet<string> SystemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "winlogon", "csrss", "wininit", "services", "lsass", "smss",
            "svchost", "wmiprvse", "dashost", "wsappx", "dwm", "fontdrvhost",
            "conhost", "chsime", "comppkgsrv", "useroobebroker", "sihost", "taskhostw",
            "ctfmon", "startmenuexperiencehost", "shellexperiencehost", "searchhost",
            "searchapp", "textinputhost", "applicationframehost", "runtimebroker",
            "backgroundtaskhost", "dllhost", "lockapp", "logonui", "consent", "audiodg",
            "spoolsv", "wudfhost", "securityhealthservice", "securityhealthsystray",
            "sgrmbroker", "msmpeng", "nisrv", "mpdefendercoreservice", "smartscreen",
            "searchindexer", "searchprotocolhost", "searchfilterhost",
            "key", "keygui"
        };

        public static bool Stopping
        {
            get { return _stop; }
        }

        public static AppSettings Settings
        {
            get
            {
                lock (Gate)
                {
                    return _settings.Clone();
                }
            }
        }

        public static LockState State
        {
            get { return SnapshotState(); }
        }

        public static void ProtectChild(int pid)
        {
            lock (ChildGate)
            {
                ChildPids.Add(pid);
            }
        }

        public static void UnprotectChild(int pid)
        {
            lock (ChildGate)
            {
                ChildPids.Remove(pid);
            }
        }

        private static bool IsProtectedChild(int pid)
        {
            lock (ChildGate)
            {
                return ChildPids.Contains(pid);
            }
        }

        public static void Notify(string text)
        {
            lock (NoticeGate)
            {
                Notices.Enqueue(text);
            }
        }

        public static string[] TakeNotices()
        {
            lock (NoticeGate)
            {
                if (Notices.Count == 0)
                {
                    return new string[0];
                }
                string[] arr = Notices.ToArray();
                Notices.Clear();
                return arr;
            }
        }

        public static void Start()
        {
            if (_started)
            {
                return;
            }
            _started = true;
            _stop = false;

            _settings = AppSettings.Load();
            _state = ReadStateFromDisk();

            // 上次异常退出留下的壁纸备份，在没有生效限制时恢复。
            if (!_state.IsActive(DateTime.UtcNow))
            {
                Desktop.Recover();
            }

            Log.Write("启动 " + AppInfo.VersionTag + "，自启=" + _settings.Autostart +
                      "，看门狗=" + _settings.Watchdog);

            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "FocusLock.Worker";
            _worker.Start();

            if (_settings.Watchdog)
            {
                StartWatchdog();
            }
        }

        public static void Stop()
        {
            _stop = true;
            try
            {
                DeleteFileSafe(AppPaths.MainPidFile);
            }
            catch
            {
            }
            Log.Write("退出");
        }

        public static StatusSnapshot Snapshot()
        {
            DateTime now = DateTime.UtcNow;
            LockState st;
            lock (Gate)
            {
                st = _state.Clone();
            }

            StatusSnapshot s = new StatusSnapshot();
            s.Phase = _phase;
            s.StartUtc = st.StartUtc;
            s.EndUtc = st.EndUtc;
            s.Active = st.IsActive(now);
            s.Locked = st.IsLocked(now);
            s.Scheduled = st.IsScheduled(now);
            s.Killed = _killedTotal;
            if (string.Equals(_phase, "grace", StringComparison.Ordinal))
            {
                int sec = (int)Math.Ceiling((_graceUntilUtc - now).TotalSeconds);
                s.GraceRemaining = sec > 0 ? sec : 0;
            }
            if (s.Locked)
            {
                s.UntilEndSeconds = Math.Max(0, (st.EndUtc - now).TotalSeconds);
            }
            if (s.Scheduled)
            {
                s.UntilStartSeconds = Math.Max(0, (st.StartUtc - now).TotalSeconds);
            }
            return s;
        }

        public static bool VisualLocked
        {
            get
            {
                string p = _phase;
                return string.Equals(p, "grace", StringComparison.Ordinal) ||
                       string.Equals(p, "locked", StringComparison.Ordinal);
            }
        }

        // ---------------- 计划 ----------------

        public static bool SetSchedule(DateTime startLocal, int minutes, string password,
            string passwordConfirm, out string error)
        {
            error = null;
            if (password == null || password.Length < 4)
            {
                error = "密码至少 4 位。";
                return false;
            }
            if (!string.Equals(password, passwordConfirm, StringComparison.Ordinal))
            {
                error = "两次输入的密码不一致。";
                return false;
            }

            DateTime startUtc = startLocal.ToUniversalTime();
            DateTime endUtc;
            try
            {
                endUtc = startUtc.AddMinutes(minutes);
            }
            catch (ArgumentOutOfRangeException)
            {
                error = "结束时间超出范围。";
                return false;
            }
            if (endUtc <= DateTime.UtcNow)
            {
                error = "结束时间必须晚于现在。";
                return false;
            }

            LockState st = new LockState();
            st.StartUtc = startUtc;
            st.EndUtc = endUtc;
            st.PasswordSalt = Secrets.NewSalt();
            st.PasswordHash = Secrets.Hash(st.PasswordSalt, password);

            if (!TrySetState(st))
            {
                error = "保存状态失败，请检查 " + AppPaths.Root + " 的写入权限。";
                return false;
            }

            Log.Write("设置计划: " + TimeText.FormatLocal(st.StartUtc) + " -> " + TimeText.FormatLocal(st.EndUtc));
            Notify("计划已保存：" + TimeText.FormatLocal(st.StartUtc) + " 开始。");
            return true;
        }

        public static bool EndRestriction(string reason)
        {
            bool ok = TrySetState(LockState.Empty());
            if (ok)
            {
                Log.Write("结束限制: " + reason);
                Notify("限制已结束。");
            }
            return ok;
        }

        // 解除全部功能并退出（Key / 卸载）。
        public static void ReleaseAll(string reason)
        {
            // 先把状态清掉，工作线程下一拍就会停止清理进程；
            // 等清理动作全部做完再置 _stop，避免界面提前退出打断收尾。
            try
            {
                TrySetState(LockState.Empty());
            }
            catch
            {
            }
            try
            {
                Desktop.Restore();
            }
            catch
            {
            }
            try
            {
                string iconError;
                DesktopIcons.Restore(out iconError);
            }
            catch
            {
            }
            string err;
            Tasks.Disable(out err);
            lock (Gate)
            {
                _settings.Autostart = false;
                _settings.Watchdog = false;
                _settings.Save();
            }
            _phase = "idle";
            Log.Write("释放全部功能: " + reason);
            Notify("已解除全部限制并退出。");
            _stop = true;
        }

        public static bool ApplySettings(AppSettings next, out string error)
        {
            error = null;
            AppSettings old;
            lock (Gate)
            {
                old = _settings.Clone();
            }

            bool autostartChanged = next.Autostart != old.Autostart;
            bool watchdogChanged = next.Watchdog != old.Watchdog;

            lock (Gate)
            {
                _settings = next.Clone();
            }

            if (!next.Save())
            {
                lock (Gate)
                {
                    _settings = old;
                }
                error = "保存设置文件失败。";
                return false;
            }

            if (autostartChanged)
            {
                if (next.Autostart)
                {
                    if (!Tasks.Enable(out error))
                    {
                        next.Autostart = false;
                        lock (Gate)
                        {
                            _settings = next.Clone();
                        }
                        next.Save();
                        // 登录任务可能已经建成，回滚时一并删掉。
                        string rollback;
                        Tasks.Disable(out rollback);
                        return false;
                    }
                }
                else
                {
                    Tasks.Disable(out error);
                }
            }

            if (watchdogChanged && next.Watchdog)
            {
                StartWatchdog();
            }

            return true;
        }

        // ---------------- 状态读写 ----------------

        private static LockState SnapshotState()
        {
            lock (Gate)
            {
                return _state.Clone();
            }
        }

        private static bool TrySetState(LockState st)
        {
            lock (Gate)
            {
                LockState old = _state;
                _state = st;
                try
                {
                    WriteStateFile(st);
                    return true;
                }
                catch (Exception ex)
                {
                    _state = old;
                    Log.Write("保存状态失败: " + ex.Message);
                    return false;
                }
            }
        }

        private static LockState ReadStateFromDisk()
        {
            try
            {
                if (!File.Exists(AppPaths.StateFile))
                {
                    return LockState.Empty();
                }
                LockState st;
                if (LockState.TryParse(File.ReadAllText(AppPaths.StateFile, Encoding.UTF8), out st))
                {
                    return st;
                }
            }
            catch
            {
            }
            return LockState.Empty();
        }

        private static void WriteStateFile(LockState st)
        {
            AppPaths.EnsureRoot();
            string tmp = AppPaths.StateFile + ".tmp";
            File.WriteAllText(tmp, st.ToFileText(), new UTF8Encoding(false));
            if (File.Exists(AppPaths.StateFile))
            {
                try
                {
                    File.Replace(tmp, AppPaths.StateFile, null);
                }
                catch
                {
                    File.Copy(tmp, AppPaths.StateFile, true);
                    File.Delete(tmp);
                }
            }
            else
            {
                File.Move(tmp, AppPaths.StateFile);
            }
        }

        // ---------------- 工作线程 ----------------

        private static void WorkerLoop()
        {
            while (!_stop)
            {
                int sleep = 400;
                try
                {
                    DateTime now = DateTime.UtcNow;
                    LockState st = SnapshotState();

                    if (!st.IsActive(now))
                    {
                        if (!string.Equals(_phase, "idle", StringComparison.Ordinal))
                        {
                            bool expired = st.EndUtc > st.StartUtc && now >= st.EndUtc;
                            EndVisual(expired);
                        }
                    }
                    else if (st.IsScheduled(now))
                    {
                        if (!string.Equals(_phase, "scheduled", StringComparison.Ordinal))
                        {
                            _phase = "scheduled";
                            Log.Write("进入计划等待");
                        }
                    }
                    else
                    {
                        if (!string.Equals(_phase, "grace", StringComparison.Ordinal) &&
                            !string.Equals(_phase, "locked", StringComparison.Ordinal))
                        {
                            BeginVisual(st, now);
                        }

                        if (string.Equals(_phase, "grace", StringComparison.Ordinal) && now >= _graceUntilUtc)
                        {
                            _phase = "locked";
                            Notify("限制已生效，开始清理非系统程序。");
                            Log.Write("限制生效");
                        }

                        if (string.Equals(_phase, "locked", StringComparison.Ordinal))
                        {
                            sleep = 600;
                            AppSettings s = Settings;
                            if (s.Enforce)
                            {
                                Enforce(st, now);
                            }
                        }
                    }

                    if ((now - _lastKeyCheck).TotalSeconds >= 1.5)
                    {
                        _lastKeyCheck = now;
                        if (KeyProcessRunning())
                        {
                            Log.Write("检测到 Key 进程，解除限制");
                            Notify("检测到 Key：已停止全部功能。");
                            ReleaseAll("检测到 Key 进程");
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("工作线程错误: " + ex.Message);
                    sleep = 1000;
                }

                Thread.Sleep(sleep);
            }
        }

        private static void BeginVisual(LockState st, DateTime now)
        {
            _phase = "grace";
            _killedTotal = 0;

            int grace = AppInfo.GraceSeconds;
            if (now > st.StartUtc.AddSeconds(30))
            {
                grace = 0;
            }
            _graceUntilUtc = now.AddSeconds(grace);

            AppSettings s = Settings;
            if (s.Wallpaper)
            {
                string image = Assets.FindWallpaper();
                if (image == null)
                {
                    Notify("未找到 （1）.jpg/png，跳过壁纸。");
                }
                else
                {
                    string err = Desktop.ApplyWallpaper(image);
                    if (err != null)
                    {
                        Notify("壁纸更换失败：" + err);
                    }
                }
            }

            if (s.Icon)
            {
                string icon = Assets.FindIcon();
                if (icon == null)
                {
                    Notify("未找到 （2）.jpg/png，跳过桌面图标。");
                }
                else
                {
                    string iconError;
                    DesktopIcons.Apply(icon, out iconError);
                    if (iconError != null)
                    {
                        Notify("桌面图标：" + iconError);
                    }
                }
            }

            Notify("限制开始：" + TimeText.FormatLocal(st.StartUtc) + " → " + TimeText.FormatLocal(st.EndUtc));
            Log.Write("开始限制，宽限 " + grace + " 秒");
        }

        private static void EndVisual(bool clearExpiredState)
        {
            _phase = "idle";
            Desktop.Restore();
            string iconError;
            DesktopIcons.Restore(out iconError);

            if (clearExpiredState)
            {
                TrySetState(LockState.Empty());
                Log.Write("限制到期，状态已清除");
            }
            Notify("限制已结束，桌面已恢复。");
        }

        private static void Enforce(LockState st, DateTime now)
        {
            if (!st.IsLocked(now))
            {
                return;
            }

            int session;
            try
            {
                session = Process.GetCurrentProcess().SessionId;
            }
            catch
            {
                return;
            }

            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch
            {
                return;
            }

            int killed = 0;
            List<string> names = new List<string>();

            foreach (Process p in processes)
            {
                try
                {
                    int pid = p.Id;
                    if (pid <= 4 || pid == Process.GetCurrentProcess().Id || pid == _watchdogPid)
                    {
                        continue;
                    }
                    if (IsProtectedChild(pid))
                    {
                        continue;
                    }
                    if (IsOwnProcess(p))
                    {
                        continue;
                    }

                    int pSession;
                    try
                    {
                        pSession = p.SessionId;
                    }
                    catch
                    {
                        continue;
                    }
                    if (pSession == 0 || pSession != session)
                    {
                        continue;
                    }

                    string name;
                    try
                    {
                        name = p.ProcessName;
                    }
                    catch
                    {
                        continue;
                    }

                    if (IsSystemProcess(p, name))
                    {
                        continue;
                    }

                    try
                    {
#if DRY_RUN
                        Log.Write("[dry-run] would kill " + name + "(" + pid.ToString(CultureInfo.InvariantCulture) + ")");
                        killed++;
#else
                        p.Kill();
                        killed++;
#endif
                        if (names.Count < 12)
                        {
                            names.Add(name + "(" + pid.ToString(CultureInfo.InvariantCulture) + ")");
                        }
                    }
                    catch (Win32Exception)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (NotSupportedException)
                    {
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (Win32Exception)
                {
                }
                finally
                {
                    try
                    {
                        p.Dispose();
                    }
                    catch
                    {
                    }
                }
            }

            _killedTotal += killed;
            if (killed > 0)
            {
                Log.Write("清理 " + killed.ToString(CultureInfo.InvariantCulture) + " 个程序: " +
                          string.Join(", ", names.ToArray()));
            }
        }

        private static bool IsOwnProcess(Process p)
        {
            try
            {
                string name = p.ProcessName;
                if (string.Equals(name, "key", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "keygui", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string path = TryPath(p);
                if (path == null)
                {
                    return string.Equals(name, AppPaths.ExeName, StringComparison.OrdinalIgnoreCase);
                }
                return PathsEqual(path, AppPaths.ExePath) ||
                       PathsEqual(Path.GetDirectoryName(path), AppPaths.ExeDir);
            }
            catch
            {
                return true;
            }
        }

        private static bool IsSystemProcess(Process p, string name)
        {
            string lower = name.ToLowerInvariant();
            if (CoreSystemNames.Contains(lower) || KeepAliveNames.Contains(lower))
            {
                return true;
            }
            if (!SystemNames.Contains(lower))
            {
                return false;
            }

            string path = TryPath(p);
            if (path == null)
            {
                return true;
            }
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
            return path.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static string TryPath(Process p)
        {
            try
            {
                return p.MainModule.FileName;
            }
            catch
            {
                return null;
            }
        }

        private static bool KeyProcessRunning()
        {
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        string n = p.ProcessName;
                        if (!string.Equals(n, "key", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(n, "keygui", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        string path = TryPath(p);
                        if (path == null)
                        {
                            continue;
                        }
                        if (PathsEqual(Path.GetDirectoryName(path), AppPaths.ExeDir))
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        // ---------------- 看门狗 / 守护 ----------------

        public static void StartWatchdog()
        {
            if (_watchdogPid > 0 && IsOurProcess(_watchdogPid))
            {
                return;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(AppPaths.ExePath, "--watchdog");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.WorkingDirectory = AppPaths.ExeDir;
                Process.Start(psi);
                Thread.Sleep(250);
                _watchdogPid = ReadValidPid(AppPaths.WatchdogPidFile);
            }
            catch (Exception ex)
            {
                Log.Write("启动看门狗失败: " + ex.Message);
            }
        }

        public static int WatchdogMain()
        {
            Mutex watchdog = new Mutex(false, AppInfo.WatchdogMutexName);
            bool acquired;
            try
            {
                acquired = watchdog.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            if (!acquired)
            {
                watchdog.Close();
                return 0;
            }

            WritePidFile(AppPaths.WatchdogPidFile, Process.GetCurrentProcess().Id);
            Log.Write("看门狗启动");

            try
            {
                while (true)
                {
                    LockState st = ReadStateFromDisk();
                    AppSettings s = AppSettings.Load();
                    if (!s.Watchdog)
                    {
                        break;
                    }

                    int mainPid = ReadValidPid(AppPaths.MainPidFile);
                    if (mainPid <= 0)
                    {
                        if (!st.IsActive(DateTime.UtcNow))
                        {
                            break;
                        }
                        Log.Write("主进程不在，尝试重启");
                        try
                        {
                            ProcessStartInfo psi = new ProcessStartInfo(AppPaths.ExePath, "--resume");
                            psi.UseShellExecute = false;
                            psi.WorkingDirectory = AppPaths.ExeDir;
                            Process.Start(psi);
                        }
                        catch (Exception ex)
                        {
                            Log.Write("重启主进程失败: " + ex.Message);
                        }
                        Thread.Sleep(3000);
                    }
                    else
                    {
                        Thread.Sleep(1200);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("看门狗错误: " + ex.Message);
            }
            finally
            {
                DeleteFileSafe(AppPaths.WatchdogPidFile);
                Log.Write("看门狗退出");
                try
                {
                    watchdog.ReleaseMutex();
                }
                catch
                {
                }
                watchdog.Close();
            }
            return 0;
        }

        public static int GuardMain()
        {
            try
            {
                int session;
                try
                {
                    session = Process.GetCurrentProcess().SessionId;
                }
                catch
                {
                    return 0;
                }
                if (session == 0)
                {
                    return 0;
                }

                LockState st = ReadStateFromDisk();
                if (!st.IsActive(DateTime.UtcNow))
                {
                    return 0;
                }
                if (MainInstanceExists())
                {
                    return 0;
                }

                Log.Write("守护任务重启主进程");
                ProcessStartInfo psi = new ProcessStartInfo(AppPaths.ExePath, "--resume");
                psi.UseShellExecute = false;
                psi.WorkingDirectory = AppPaths.ExeDir;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log.Write("守护任务错误: " + ex.Message);
            }
            return 0;
        }

        private static bool MainInstanceExists()
        {
            try
            {
                using (Mutex m = Mutex.OpenExisting(AppInfo.MutexName))
                {
                    bool acquired;
                    try
                    {
                        acquired = m.WaitOne(0);
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }
                    if (acquired)
                    {
                        try
                        {
                            m.ReleaseMutex();
                        }
                        catch
                        {
                        }
                        return false;
                    }
                    return true;
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static bool IsOurProcess(int pid)
        {
            try
            {
                Process p = Process.GetProcessById(pid);
                if (!string.Equals(p.ProcessName, AppPaths.ExeName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                string path = TryPath(p);
                return path == null || PathsEqual(path, AppPaths.ExePath);
            }
            catch
            {
                return false;
            }
        }

        public static void WritePidFile(string file, int pid)
        {
            try
            {
                AppPaths.EnsureRoot();
                File.WriteAllText(file, pid.ToString(CultureInfo.InvariantCulture), new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        public static int ReadValidPid(string file)
        {
            try
            {
                if (!File.Exists(file))
                {
                    return 0;
                }
                int pid;
                if (!int.TryParse(File.ReadAllText(file).Trim(), out pid) || pid <= 0)
                {
                    return 0;
                }
                return IsOurProcess(pid) ? pid : 0;
            }
            catch
            {
                return 0;
            }
        }

        // ---------------- 信号：把已隐藏的界面唤回 ----------------

        public static EventWaitHandle CreateShowEvent()
        {
            bool created;
            return new EventWaitHandle(false, EventResetMode.AutoReset, AppInfo.ShowEventName, out created);
        }

        public static void SignalShow()
        {
            for (int i = 0; i < 12; i++)
            {
                try
                {
                    using (EventWaitHandle ev = EventWaitHandle.OpenExisting(AppInfo.ShowEventName))
                    {
                        ev.Set();
                        return;
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(200);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(200);
                }
            }
        }

        public static bool PathsEqual(string a, string b)
        {
            if (a == null || b == null)
            {
                return false;
            }
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'),
                    Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
        }

        private static void DeleteFileSafe(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }

    // ------------------------------------------------------------------
    // 时间输入解析 / 格式化
    // ------------------------------------------------------------------
    internal static class TimeText
    {
        public static string FormatLocal(DateTime utc)
        {
            return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        public static string FormatDuration(TimeSpan span)
        {
            if (span < TimeSpan.Zero)
            {
                span = TimeSpan.Zero;
            }
            long total = (long)Math.Round(span.TotalMinutes);
            if (total < 60)
            {
                return total.ToString(CultureInfo.InvariantCulture) + " 分钟";
            }
            long h = total / 60;
            long m = total % 60;
            if (m == 0)
            {
                return h.ToString(CultureInfo.InvariantCulture) + " 小时";
            }
            return h.ToString(CultureInfo.InvariantCulture) + " 小时 " +
                   m.ToString(CultureInfo.InvariantCulture) + " 分钟";
        }

        public static string FormatRemaining(TimeSpan t)
        {
            if (t < TimeSpan.Zero)
            {
                t = TimeSpan.Zero;
            }
            if (t.TotalDays >= 1)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}天 {1:00}:{2:00}:{3:00}",
                    (int)t.TotalDays, t.Hours, t.Minutes, t.Seconds);
            }
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
                (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        public static bool TryParseStart(string input, out DateTime startLocal, out string note)
        {
            note = null;
            string text = (input ?? "").Trim();
            DateTime now = DateTime.Now;

            if (text.Length == 0 ||
                string.Equals(text, "now", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "立即", StringComparison.Ordinal))
            {
                startLocal = now;
                return true;
            }

            bool timeOnly = Regex.IsMatch(text, @"^\d{1,2}:\d{2}(:\d{2})?$");
            string[] formats = new string[]
            {
                "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy/M/d HH:mm", "yyyy/M/d H:mm",
                "yyyy/M/d HH:mm:ss", "MM-dd HH:mm", "M/d HH:mm", "MM-dd HH:mm:ss",
                "M/d HH:mm:ss", "HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss"
            };

            DateTime parsed;
            bool ok = DateTime.TryParseExact(text, formats, CultureInfo.CurrentCulture,
                DateTimeStyles.None, out parsed);
            if (!ok)
            {
                ok = DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsed);
            }
            if (!ok)
            {
                startLocal = now;
                return false;
            }

            startLocal = DateTime.SpecifyKind(parsed, DateTimeKind.Local);

            if (timeOnly)
            {
                DateTime today = now.Date.Add(parsed.TimeOfDay);
                if (today < now.AddMinutes(-2))
                {
                    today = today.AddDays(1);
                    note = "今天该时间已过，已改为明天 " + today.ToString("HH:mm", CultureInfo.InvariantCulture) + "。";
                }
                else if (today < now)
                {
                    today = now;
                    note = "开始时间已到，按立即开始处理。";
                }
                startLocal = today;
                return true;
            }

            if (startLocal < now.AddMinutes(-2))
            {
                note = "开始时间不能早于当前时间。";
                return false;
            }
            if (startLocal < now)
            {
                startLocal = now;
                note = "开始时间已到，按立即开始处理。";
            }
            return true;
        }

        public static bool TryParseDuration(string input, out int minutes)
        {
            minutes = 0;
            string text = (input ?? "").Trim().ToLowerInvariant();
            text = text.Replace("分钟", "").Replace("分", "").Replace("小时", "h").Replace("时", "h").Trim();
            if (text.Length == 0)
            {
                return false;
            }

            Match m = Regex.Match(text, @"^(?:(\d+)\s*h)?\s*(?:(\d+)\s*m)?$");
            if (m.Success && (m.Groups[1].Success || m.Groups[2].Success))
            {
                int h = 0;
                int mins = 0;
                if (m.Groups[1].Success) int.TryParse(m.Groups[1].Value, out h);
                if (m.Groups[2].Success) int.TryParse(m.Groups[2].Value, out mins);
                minutes = h * 60 + mins;
            }
            else
            {
                m = Regex.Match(text, @"^(\d+):(\d{1,2})$");
                if (m.Success)
                {
                    int h;
                    int mins;
                    if (!int.TryParse(m.Groups[1].Value, out h) || !int.TryParse(m.Groups[2].Value, out mins))
                    {
                        return false;
                    }
                    if (mins > 59)
                    {
                        return false;
                    }
                    minutes = h * 60 + mins;
                }
                else if (!int.TryParse(text, out minutes))
                {
                    return false;
                }
            }

            return minutes >= 1 && minutes <= AppInfo.MaxDurationMinutes;
        }
    }

    // ------------------------------------------------------------------
    // Win32
    // ------------------------------------------------------------------
    internal static class Native
    {
        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;

        public const uint MF_BYCOMMAND = 0x00000000;
        public const uint MF_GRAYED = 0x00000001;
        public const uint SC_CLOSE = 0xF060;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_SHOWWINDOW = 0x0040;

        public const int WM_SETICON = 0x0080;
        public const int ICON_SMALL = 0;
        public const int ICON_BIG = 1;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public delegate bool ConsoleCtrlDelegate(uint ctrlType);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool SetConsoleTitle(string title);

        [DllImport("kernel32.dll")]
        public static extern uint SetConsoleOutputCP(uint codePage);

        [DllImport("kernel32.dll")]
        public static extern uint SetConsoleCP(uint codePage);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetSystemMenu(IntPtr hWnd, bool revert);

        [DllImport("user32.dll")]
        public static extern bool DeleteMenu(IntPtr hMenu, uint position, uint flags);

        [DllImport("user32.dll")]
        public static extern bool EnableMenuItem(IntPtr hMenu, uint item, uint enable);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
            int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
        public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
    }

    // ------------------------------------------------------------------
    // 控制台外壳（两个控制台程序共用）
    // ------------------------------------------------------------------
    internal static class ConsoleHost
    {
        private static IntPtr _hwnd;
        private static Native.ConsoleCtrlDelegate _handler;

        public static IntPtr Handle
        {
            get
            {
                if (_hwnd == IntPtr.Zero)
                {
                    _hwnd = Native.GetConsoleWindow();
                }
                return _hwnd;
            }
        }

        public static void Attach(string title, bool lockedClose)
        {
            if (Native.GetConsoleWindow() == IntPtr.Zero)
            {
                if (!Native.AllocConsole())
                {
                    return;
                }
            }
            _hwnd = Native.GetConsoleWindow();
            Native.SetConsoleOutputCP(65001);
            Native.SetConsoleCP(65001);

            StreamWriter stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            stdout.AutoFlush = true;
            Console.SetOut(stdout);

            StreamWriter stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false));
            stderr.AutoFlush = true;
            Console.SetError(stderr);

            Console.SetIn(new StreamReader(Console.OpenStandardInput(), Encoding.UTF8));

            try
            {
                Console.BackgroundColor = ConsoleColor.Black;
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Clear();
                Console.CursorVisible = true;
            }
            catch
            {
            }

            try
            {
                int width = Math.Min(100, Console.LargestWindowWidth);
                int height = Math.Min(34, Console.LargestWindowHeight);
                if (width >= 60 && height >= 20)
                {
                    Console.SetBufferSize(width, Math.Max(500, Console.BufferHeight));
                    Console.SetWindowSize(width, height);
                }
            }
            catch
            {
            }

            Native.SetConsoleTitle(title);

            if (lockedClose && _hwnd != IntPtr.Zero)
            {
                IntPtr menu = Native.GetSystemMenu(_hwnd, false);
                if (menu != IntPtr.Zero)
                {
                    Native.DeleteMenu(menu, Native.SC_CLOSE, Native.MF_BYCOMMAND);
                    Native.EnableMenuItem(menu, Native.SC_CLOSE, Native.MF_BYCOMMAND | Native.MF_GRAYED);
                }
            }

            _handler = ConsoleCtrlHandler;
            Native.SetConsoleCtrlHandler(_handler, true);
            try
            {
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                };
            }
            catch
            {
            }
        }

        private static bool ConsoleCtrlHandler(uint ctrlType)
        {
            // 0 Ctrl+C, 1 Ctrl+Break, 2 关闭按钮：拒绝；注销/关机放行。
            return ctrlType == 0 || ctrlType == 1 || ctrlType == 2;
        }

        public static void Show()
        {
            IntPtr h = Handle;
            if (h == IntPtr.Zero)
            {
                return;
            }
            Native.ShowWindow(h, Native.SW_SHOW);
            Native.ShowWindow(h, Native.SW_RESTORE);
            Native.SetForegroundWindow(h);
        }

        public static void Hide()
        {
            IntPtr h = Handle;
            if (h != IntPtr.Zero)
            {
                Native.ShowWindow(h, Native.SW_HIDE);
            }
        }

        public static bool Visible
        {
            get
            {
                IntPtr h = Handle;
                return h != IntPtr.Zero && Native.IsWindowVisible(h);
            }
        }

        public static void TopMost(bool on)
        {
            IntPtr h = Handle;
            if (h == IntPtr.Zero)
            {
                return;
            }
            uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE;
            if (on)
            {
                flags |= Native.SWP_SHOWWINDOW;
            }
            Native.SetWindowPos(h, on ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        }

        public static void SetIcon(Icon icon)
        {
            IntPtr h = Handle;
            if (h == IntPtr.Zero || icon == null)
            {
                return;
            }
            Native.SendMessage(h, Native.WM_SETICON, new IntPtr(Native.ICON_BIG), icon.Handle);
            Native.SendMessage(h, Native.WM_SETICON, new IntPtr(Native.ICON_SMALL), icon.Handle);
        }

        public static void WriteLine(string text, ConsoleColor? color)
        {
            if (color.HasValue)
            {
                Console.ForegroundColor = color.Value;
            }
            Console.WriteLine(text);
            if (color.HasValue)
            {
                Console.ResetColor();
            }
        }
    }

    // ------------------------------------------------------------------
    // 自检
    // ------------------------------------------------------------------
    internal static class SelfTest
    {
        public static int Run(string[] args)
        {
            StringBuilder log = new StringBuilder();
            int failures = 0;

            Action<string, bool> check = delegate(string name, bool ok)
            {
                log.AppendLine((ok ? "PASS " : "FAIL ") + name);
                if (!ok)
                {
                    failures++;
                }
            };

            int minutes;
            check("duration 90", TimeText.TryParseDuration("90", out minutes) && minutes == 90);
            check("duration 2h", TimeText.TryParseDuration("2h", out minutes) && minutes == 120);
            check("duration 1h30m", TimeText.TryParseDuration("1h30m", out minutes) && minutes == 90);
            check("duration 01:30", TimeText.TryParseDuration("01:30", out minutes) && minutes == 90);
            check("duration 0 invalid", !TimeText.TryParseDuration("0", out minutes));
            check("duration 10081 invalid", !TimeText.TryParseDuration("10081", out minutes));
            check("duration abc invalid", !TimeText.TryParseDuration("abc", out minutes));

            DateTime start;
            string note;
            check("start now", TimeText.TryParseStart("now", out start, out note) &&
                              Math.Abs((start - DateTime.Now).TotalSeconds) < 10);
            check("start empty", TimeText.TryParseStart("", out start, out note) &&
                                 Math.Abs((start - DateTime.Now).TotalSeconds) < 10);
            check("start 23:59", TimeText.TryParseStart("23:59", out start, out note));
            check("start invalid", !TimeText.TryParseStart("not-a-time", out start, out note));

            LockState state = new LockState();
            state.StartUtc = DateTime.UtcNow.AddMinutes(5);
            state.EndUtc = state.StartUtc.AddMinutes(30);
            state.PasswordSalt = Secrets.NewSalt();
            state.PasswordHash = Secrets.Hash(state.PasswordSalt, "abc12345");
            LockState parsed;
            check("state roundtrip", LockState.TryParse(state.ToFileText(), out parsed) &&
                                    parsed.StartUtc == state.StartUtc &&
                                    parsed.EndUtc == state.EndUtc &&
                                    parsed.PasswordHash == state.PasswordHash);
            check("state garbage invalid", !LockState.TryParse("garbage", out parsed));
            check("hash is sha256", Secrets.Hash("salt", "x").Length == 64);

            check("fixed password blobs", Secrets.SelfCheck());
            check("master rejects junk", !Secrets.VerifyMaster("not-the-password"));
            check("end-only rejects junk", !Secrets.VerifyEndOnly("0123456789"));
            check("lock password verify", Secrets.VerifyLockPassword(state, "abc12345"));
            check("lock password wrong", !Secrets.VerifyLockPassword(state, "abc1234"));
            check("can end with lock pw", Secrets.VerifyCanEnd(state, "abc12345"));
            check("end rejects junk", !Secrets.VerifyCanEnd(state, "nope"));

            check("remaining format", TimeText.FormatRemaining(TimeSpan.FromSeconds(3661)) == "01:01:01");
            check("remaining days",
                TimeText.FormatRemaining(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1)) == "1天 00:00:01");
            check("keep explorer", Locker.Settings != null);

            bool xmlOk = true;
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml("<x/>");
            }
            catch
            {
                xmlOk = false;
            }
            check("xml engine", xmlOk);

            string path = Environment.GetEnvironmentVariable("FOCUSLOCK_TEST_LOG");
            if (string.IsNullOrEmpty(path))
            {
                path = Path.Combine(Path.GetTempPath(), "FocusLock-selftest.log");
            }
            log.AppendLine();
            log.AppendLine(failures == 0 ? "RESULT: PASS" : "RESULT: FAIL (" + failures + ")");
            try
            {
                File.WriteAllText(path, log.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }
            return failures == 0 ? 0 : 1;
        }
    }
}
