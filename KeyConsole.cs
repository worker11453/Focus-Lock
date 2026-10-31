using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

[assembly: AssemblyTitle("FocusLock Key v2.0.1（控制台版）")]
[assembly: AssemblyDescription("FocusLock 解除工具，控制台界面")]
[assembly: AssemblyCompany("FocusLock")]
[assembly: AssemblyProduct("FocusLock")]
[assembly: AssemblyVersion("2.0.1.0")]
[assembly: AssemblyFileVersion("2.0.1.0")]

namespace FocusLock
{
    internal static class KeyConsoleMain
    {
        [STAThread]
        private static int Main(string[] args)
        {
            AppPaths.Init();

            ConsoleHost.Attach("FocusLock Key", false);
            Console.OutputEncoding = new UTF8Encoding(false);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("============================================================");
            Console.WriteLine("  FocusLock Key  " + AppInfo.VersionTag + "   解除工具 [控制台版]");
            Console.WriteLine("============================================================");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("  运行本工具会立即：");
            Console.WriteLine("    · 结束当前限制，恢复桌面壁纸");
            Console.WriteLine("    · 关闭看门狗，移除开机自启");
            Console.WriteLine("    · 结束所有 FocusLock 进程");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  这是一把“钥匙”：任何拿到它的人都可以解除 FocusLock。");
            Console.WriteLine("  请把它放在只有你自己能访问的地方。");
            Console.ResetColor();
            Console.WriteLine();

            LockState st = KeyTool.ReadDiskState();
            DateTime now = DateTime.UtcNow;
            if (st.IsLocked(now))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  当前 FocusLock 处于锁定状态（剩余 " +
                    TimeText.FormatRemaining(TimeSpan.FromSeconds(Math.Max(0, (st.EndUtc - now).TotalSeconds))) + "）。");
                Console.ResetColor();
            }
            else if (st.IsActive(now))
            {
                Console.WriteLine("  当前有一个计划中的限制。");
            }
            else
            {
                Console.WriteLine("  当前没有生效的限制。");
            }
            Console.WriteLine();

            if (!Confirm("输入 YES 确认解除并退出 FocusLock: "))
            {
                Console.WriteLine("已取消。");
                WaitEnter();
                return 0;
            }

            Console.WriteLine();
            Console.WriteLine("正在解除...");
            Locker.ReleaseAll("Key");
            int killed = KeyTool.StopFocusLock();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("完成：限制已结束，开机自启已移除，结束进程 " + killed + " 个。");
            Console.ResetColor();
            Console.WriteLine("日志：" + AppPaths.LogFile);
            WaitEnter();
            return 0;
        }

        private static bool Confirm(string prompt)
        {
            Console.Write(prompt);
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                bool available;
                try
                {
                    available = Console.KeyAvailable;
                }
                catch
                {
                    return false;
                }
                if (!available)
                {
                    Thread.Sleep(30);
                    continue;
                }
                ConsoleKeyInfo k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }
                if (k.Key == ConsoleKey.Escape)
                {
                    Console.WriteLine();
                    return false;
                }
                if (k.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0)
                    {
                        sb.Length--;
                        Console.Write("\b \b");
                    }
                    continue;
                }
                if (!char.IsControl(k.KeyChar))
                {
                    sb.Append(k.KeyChar);
                    Console.Write(k.KeyChar);
                }
            }
            string t = sb.ToString().Trim();
            return string.Equals(t, "YES", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(t, "Y", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(t, "是", StringComparison.Ordinal);
        }

        private static void WaitEnter()
        {
            Console.WriteLine();
            Console.Write("按 Enter 退出...");
            try
            {
                while (true)
                {
                    if (Console.KeyAvailable)
                    {
                        ConsoleKeyInfo k = Console.ReadKey(true);
                        if (k.Key == ConsoleKey.Enter)
                        {
                            break;
                        }
                    }
                    else
                    {
                        Thread.Sleep(40);
                    }
                }
            }
            catch
            {
            }
            Console.WriteLine();
        }
    }
}
