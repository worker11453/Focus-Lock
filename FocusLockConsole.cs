using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

[assembly: AssemblyTitle("FocusLock 专注锁 v2.0.1（控制台版）")]
[assembly: AssemblyDescription("密码保护的定时专注锁，控制台界面")]
[assembly: AssemblyCompany("FocusLock")]
[assembly: AssemblyProduct("FocusLock")]
[assembly: AssemblyVersion("2.0.1.0")]
[assembly: AssemblyFileVersion("2.0.1.0")]

namespace FocusLock
{
    internal static class ConsoleMain
    {
        private static Mutex _mutex;
        private static EventWaitHandle _showEvent;
        private static Thread _showThread;
        private static bool _visualActive;
        private static Icon _ownIcon;

        [STAThread]
        private static int Main(string[] args)
        {
            AppPaths.Init();

            if (HasArg(args, "--selftest"))
            {
                return SelfTest.Run(args);
            }
            if (HasArg(args, "--watchdog"))
            {
                FreeConsoleQuiet();
                return Locker.WatchdogMain();
            }
            if (HasArg(args, "--guard"))
            {
                FreeConsoleQuiet();
                return Locker.GuardMain();
            }
            if (HasArg(args, "--selftest-tasks"))
            {
                return TestTasks();
            }
            if (HasArg(args, "--selftest-wallpaper"))
            {
                return TestWallpaper();
            }
            if (HasArg(args, "--apply-desktop"))
            {
                return DesktopCommand(true);
            }
            if (HasArg(args, "--restore-desktop"))
            {
                return DesktopCommand(false);
            }

            _ownIcon = OwnIcon();

            // 单实例：先拿互斥体再开窗口，重复启动时不会闪一下黑框。
            _mutex = new Mutex(false, AppInfo.MutexName);
            bool acquired;
            try
            {
                acquired = _mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            catch (UnauthorizedAccessException)
            {
                acquired = false;
            }
            if (!acquired)
            {
                Locker.SignalShow();
                return 0;
            }

            ConsoleHost.Attach(AppInfo.Product, true);
            Console.OutputEncoding = new UTF8Encoding(false);

            try
            {
                AppPaths.EnsureRoot();
                Locker.WritePidFile(AppPaths.MainPidFile, System.Diagnostics.Process.GetCurrentProcess().Id);

                PrintBanner();

                // 先引擎后提示：即使正在锁定中重启，清理也会立即开始，
                // 不会因为提示界面停留而暂停限制。
                Locker.Start();
                CreateShowEvent();

                ShowStartupNotice();

                Loop();
            }
            catch (Exception ex)
            {
                Log.Write("致命错误: " + ex);
                Console.WriteLine("FocusLock 发生错误：" + ex.Message);
            }
            finally
            {
                Locker.Stop();
                Cleanup();
            }
            return 0;
        }

        // 仅供构建期自测：创建并删除计划任务。
        private static int TestTasks()
        {
            StringBuilder sb = new StringBuilder();
            string err;
            bool enabled = Tasks.Enable(out err);
            sb.AppendLine("Enable=" + enabled + " error=" + (err ?? ""));
            string output;
            int q1 = Tasks.Schtasks("/Query /TN \"" + Tasks.CurrentTaskName(false) + "\"", out output);
            int q2 = Tasks.Schtasks("/Query /TN \"" + Tasks.CurrentTaskName(true) + "\"", out output);
            sb.AppendLine("QueryLogon=" + q1 + " QueryGuard=" + q2);
            string derr;
            bool disabled = Tasks.Disable(out derr);
            int q3 = Tasks.Schtasks("/Query /TN \"" + Tasks.CurrentTaskName(false) + "\"", out output);
            int q4 = Tasks.Schtasks("/Query /TN \"" + Tasks.CurrentTaskName(true) + "\"", out output);
            sb.AppendLine("Disable=" + disabled + " error=" + (derr ?? ""));
            sb.AppendLine("QueryAfterLogon=" + q3 + " QueryAfterGuard=" + q4);
            sb.AppendLine("RESULT=" + ((enabled && q1 == 0 && q2 == 0 && q3 != 0 && q4 != 0) ? "PASS" : "FAIL"));
            File.WriteAllText(Path.Combine(AppPaths.ExeDir, "task-test.log"), sb.ToString(), new UTF8Encoding(false));
            return 0;
        }

        // 仅供构建期自测：更换并恢复壁纸。
        private static int TestWallpaper()
        {
            StringBuilder sb = new StringBuilder();
            string image = Assets.FindWallpaper();
            sb.AppendLine("Image=" + (image ?? "(none)"));
            string before = Desktop.CurrentWallpaper();
            sb.AppendLine("Before=" + before);
            string err = null;
            if (image != null)
            {
                err = Desktop.ApplyWallpaper(image);
            }
            else
            {
                err = "no image";
            }
            sb.AppendLine("Apply=" + (err ?? "ok"));
            Thread.Sleep(1200);
            sb.AppendLine("During=" + Desktop.CurrentWallpaper());
            Desktop.Restore();
            Thread.Sleep(600);
            string after = Desktop.CurrentWallpaper();
            sb.AppendLine("After=" + after);
            sb.AppendLine("BackupLeft=" + Desktop.BackupExists());
            bool pass = err == null && !Desktop.BackupExists() &&
                        string.Equals(before, after, StringComparison.OrdinalIgnoreCase);
            sb.AppendLine("RESULT=" + (pass ? "PASS" : "FAIL"));
            File.WriteAllText(Path.Combine(AppPaths.ExeDir, "wallpaper-test.log"), sb.ToString(), new UTF8Encoding(false));
            return 0;
        }
        // 手动把桌面换成 （1） 壁纸 + （2） 图标，或恢复。
        private static int DesktopCommand(bool apply)
        {
            ConsoleHost.Attach(AppInfo.Product, false);

            if (apply)
            {
                string wallpaper = Assets.FindWallpaper();
                if (wallpaper == null)
                {
                    Console.WriteLine("未找到壁纸素材 （1）.jpg/png");
                }
                else
                {
                    string e = Desktop.ApplyWallpaper(wallpaper);
                    Console.WriteLine(e == null ? ("壁纸已换成 " + wallpaper) : ("壁纸失败：" + e));
                }

                string icon = Assets.FindIcon();
                if (icon == null)
                {
                    Console.WriteLine("未找到图标素材 （2）.jpg/png");
                }
                else
                {
                    string e2;
                    bool ok = DesktopIcons.Apply(icon, out e2);
                    Console.WriteLine(ok
                        ? ("桌面快捷方式图标已替换为 " + icon)
                        : ("图标替换失败：" + e2));
                }

                // 手动应用视为“固定样式”：把存档转存，启动程序时不会自动还原。
                Desktop.Pin();
                DesktopIcons.Pin();
                Console.WriteLine("提示：运行 FocusLock.exe --restore-desktop 可还原到应用前的样子。");
            }
            else
            {
                if (!Desktop.RestorePinned())
                {
                    Desktop.Restore();
                }
                string e3;
                if (!DesktopIcons.RestorePinned(out e3))
                {
                    DesktopIcons.Restore(out e3);
                }
                Console.WriteLine("桌面壁纸与快捷方式图标已恢复。");
            }

            Console.WriteLine("日志：" + AppPaths.LogFile);
            Thread.Sleep(2500);
            return 0;
        }

        private static void Loop()
        {
            while (!Locker.Stopping)
            {
                StatusSnapshot s = Locker.Snapshot();
                TryApplyVisual(s);

                if (s.Locked)
                {
                    LockScreen();
                    continue;
                }

                PrintMenu(s);
                InputResult r = ReadLine("请选择: ", false,
                    delegate { return Locker.Snapshot().Locked; }, true);
                if (r.Aborted)
                {
                    continue;
                }
                if (r.Escaped)
                {
                    HideToBackground();
                    continue;
                }

                string cmd = (r.Text ?? "").Trim();
                if (cmd == "1") PromptSchedule(false);
                else if (cmd == "2") PromptSchedule(true);
                else if (cmd == "3") PromptEnd();
                else if (cmd == "4") PrintDetails();
                else if (cmd == "5") SettingsMenu();
                else if (cmd == "6") ShowHelp();
                else if (cmd == "7") HideToBackground();
                else if (cmd == "8") { if (UninstallFlow()) return; }
                else if (cmd == "0") { if (TryExit()) return; }
                else if (cmd.Length > 0) Console.WriteLine("无效选项。");
            }
        }

        // ---------------- 界面 ----------------

        private static void PrintBanner()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("============================================================");
            Console.WriteLine("  FocusLock 专注锁  " + AppInfo.VersionTag + "   [控制台版]");
            Console.WriteLine("============================================================");
            Console.ResetColor();
            Console.WriteLine("  密码在每次设置限制时自行设定（SHA256 加密保存）。");
            Console.WriteLine("  设置 / HELP 无需密码；按 Esc 可隐藏，再运行本程序可唤回。");
            Console.WriteLine("  日志：" + AppPaths.LogFile);
            Console.WriteLine();
        }

        private static void ShowStartupNotice()
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("--------------------------- 使用提示 ---------------------------");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Wrap(AppInfo.NoticeText, 92);
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("---------------------------------------------------------------");
            Console.ResetColor();
            Log.Write("显示启动提示");

            while (true)
            {
                InputResult r = ReadLine("按 Enter 表示已阅读并继续（Esc 退出）: ", false, null, true);
                if (r.Escaped)
                {
                    _mutex.ReleaseMutex();
                    _mutex.Close();
                    Environment.Exit(0);
                }
                if (!r.Aborted)
                {
                    return;
                }
            }
        }

        // 按“列”换行：中文算 2 列，保证在 100 列的控制台里不会硬折行。
        private static void Wrap(string text, int width)
        {
            int line = 0;
            foreach (char c in text)
            {
                int cells = (c >= 0x2E80) ? 2 : 1;
                if (line + cells > width)
                {
                    Console.WriteLine();
                    line = 0;
                }
                Console.Write(c);
                line += cells;
            }
            Console.WriteLine();
        }

        private static void PrintMenu(StatusSnapshot s)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("--------------------- FocusLock 菜单 ---------------------");
            Console.ResetColor();
            Console.WriteLine("  1. 设置限制（开始时间 + 持续时间，自设密码）");
            Console.WriteLine("  2. 立即开始限制");
            if (s.Active)
            {
                Console.WriteLine("  3. 取消计划 / 提前结束限制");
            }
            else
            {
                Console.WriteLine("  3. 取消计划 / 提前结束限制（当前没有）");
            }
            Console.WriteLine("  4. 查看详细状态");
            Console.WriteLine("  5. 设置");
            Console.WriteLine("  6. HELP");
            Console.WriteLine("  7. 隐藏到后台");
            Console.WriteLine("  8. 卸载（移除开机自启并退出）");
            Console.WriteLine("  0. 退出程序");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  开机自启：" + OnOff(Locker.Settings.Autostart) +
                              "   看门狗：" + OnOff(Locker.Settings.Watchdog) +
                              "   进程清理：" + OnOff(Locker.Settings.Enforce));
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("----------------------------------------------------------");
            Console.ResetColor();
        }

        private static string OnOff(bool on)
        {
            return on ? "ON" : "OFF";
        }

        private static void PrintOnOff(string label, bool on)
        {
            Console.Write("  " + label);
            Console.ForegroundColor = on ? ConsoleColor.Red : ConsoleColor.Green;
            Console.Write(on ? "ON" : "OFF");
            Console.ResetColor();
            Console.WriteLine();
        }

        private static void PrintDetails()
        {
            StatusSnapshot s = Locker.Snapshot();
            Console.WriteLine();
            Console.WriteLine("================ 详细状态 ================");
            if (s.Locked)
            {
                Console.WriteLine("状态：锁定中" + (s.GraceRemaining > 0 ? "（缓冲 " + s.GraceRemaining + " 秒）" : ""));
                Console.WriteLine("开始：" + TimeText.FormatLocal(s.StartUtc));
                Console.WriteLine("结束：" + TimeText.FormatLocal(s.EndUtc));
                Console.WriteLine("剩余：" + TimeText.FormatRemaining(TimeSpan.FromSeconds(s.UntilEndSeconds)));
                Console.WriteLine("已结束程序数：" + s.Killed);
            }
            else if (s.Scheduled)
            {
                Console.WriteLine("状态：等待开始");
                Console.WriteLine("开始：" + TimeText.FormatLocal(s.StartUtc));
                Console.WriteLine("结束：" + TimeText.FormatLocal(s.EndUtc));
                Console.WriteLine("距开始：" + TimeText.FormatRemaining(TimeSpan.FromSeconds(s.UntilStartSeconds)));
            }
            else
            {
                Console.WriteLine("状态：空闲");
            }
            Console.WriteLine("保护范围：当前登录会话（Session 0 与其他用户会话不处理）");
            Console.WriteLine("保留：Windows 关键进程、explorer、安全软件、本程序与 Key");
            Console.WriteLine("结束方式：锁定中输入「本次密码 / 永久结束密码 / 主密码」");
            Console.WriteLine("==========================================");
        }

        private static void LockScreen()
        {
            bool announced = false;
            DateTime lastTitle = DateTime.MinValue;

            while (!Locker.Stopping)
            {
                StatusSnapshot s = Locker.Snapshot();
                if (!s.Locked)
                {
                    break;
                }
                TryApplyVisual(s);

                if ((DateTime.UtcNow - lastTitle).TotalSeconds >= 1)
                {
                    lastTitle = DateTime.UtcNow;
                    try { Console.Title = s.Title(); } catch { }
                }

                if (!announced)
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("==================== 限制进行中 ====================");
                    Console.WriteLine("  开始：" + TimeText.FormatLocal(s.StartUtc));
                    Console.WriteLine("  结束：" + TimeText.FormatLocal(s.EndUtc));
                    if (s.GraceRemaining > 0)
                    {
                        Console.WriteLine("  缓冲中：" + s.GraceRemaining + " 秒后开始清理，输入密码可取消。");
                    }
                    else
                    {
                        Console.WriteLine("  输入本次密码（或永久结束密码 / 主密码）可提前结束。");
                        Console.WriteLine("  输入 help 可查看帮助信息。");
                    }
                    Console.WriteLine("====================================================");
                    Console.ResetColor();
                    announced = true;
                }

                InputResult r = ReadLine("密码: ", true,
                    delegate { return !Locker.Snapshot().Locked; }, true);

                if (r.Aborted)
                {
                    continue;
                }
                if (r.Escaped)
                {
                    if (Locker.Settings.AllowHide)
                    {
                        ConsoleHost.Hide();
                    }
                    else
                    {
                        Console.Beep();
                    }
                    continue;
                }

                string text = (r.Text ?? "").Trim();
                if (text.Length == 0)
                {
                    continue;
                }
                if (IsHelpText(text))
                {
                    PrintHelpBlock();
                    continue;
                }

                LockState st = Locker.State;
                if (Secrets.VerifyCanEnd(st, text))
                {
                    if (Locker.EndRestriction("密码验证通过，提前结束限制"))
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("限制已结束。");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.WriteLine("无法保存状态，限制继续。");
                    }
                    break;
                }

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("密码错误，限制继续。");
                Console.ResetColor();
                Thread.Sleep(500);
            }
        }

        // ---------------- 设置限制 ----------------

        private static void PromptSchedule(bool immediate)
        {
            StatusSnapshot before = Locker.Snapshot();
            if (before.Active)
            {
                Console.WriteLine("已有计划或限制，请先结束它。");
                return;
            }

            DateTime startLocal = DateTime.Now;
            if (!immediate)
            {
                Console.WriteLine();
                Console.WriteLine("开始时间格式：HH:mm 或 yyyy-MM-dd HH:mm；直接回车 / now = 立即开始。");
                InputResult timeInput = ReadLine("开始时间: ", false, null, true);
                if (timeInput.Aborted || timeInput.Escaped)
                {
                    Console.WriteLine("已取消。");
                    return;
                }
                string note;
                if (!TimeText.TryParseStart(timeInput.Text, out startLocal, out note))
                {
                    Console.WriteLine("开始时间无效，请重新设置。");
                    return;
                }
                if (!string.IsNullOrEmpty(note))
                {
                    Console.WriteLine(note);
                }
            }

            InputResult durInput = ReadLine("持续时间（分钟，1-10080，也可写 1h30m）: ", false, null, true);
            if (durInput.Aborted || durInput.Escaped)
            {
                Console.WriteLine("已取消。");
                return;
            }
            int minutes;
            if (!TimeText.TryParseDuration(durInput.Text, out minutes))
            {
                Console.WriteLine("持续时间无效。请输入 1 到 10080 分钟。");
                return;
            }

            Console.WriteLine();
            Console.WriteLine("请设置本次限制的密码（至少 4 位，锁定后用它提前结束）。");
            InputResult pw1 = ReadLine("本次密码: ", true, null, true);
            if (pw1.Aborted || pw1.Escaped)
            {
                Console.WriteLine("已取消。");
                return;
            }
            InputResult pw2 = ReadLine("再次输入: ", true, null, true);
            if (pw2.Aborted || pw2.Escaped)
            {
                Console.WriteLine("已取消。");
                return;
            }

            string error;
            if (!Locker.SetSchedule(startLocal, minutes, pw1.Text, pw2.Text, out error))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(error);
                Console.ResetColor();
                return;
            }

            LockState st = Locker.State;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("已保存。");
            Console.ResetColor();
            Console.WriteLine("开始：" + TimeText.FormatLocal(st.StartUtc));
            Console.WriteLine("结束：" + TimeText.FormatLocal(st.EndUtc));
            Console.WriteLine("时长：" + TimeText.FormatDuration(st.EndUtc - st.StartUtc));
            if (st.StartUtc <= DateTime.UtcNow.AddSeconds(3))
            {
                Console.WriteLine("限制将在几秒后开始。");
            }
        }

        private static void PromptEnd()
        {
            StatusSnapshot s = Locker.Snapshot();
            if (!s.Active)
            {
                Console.WriteLine("当前没有生效的计划或限制。");
                return;
            }

            Console.WriteLine("提前结束需要密码：" + (s.Locked ? "本次密码" : "本次密码"));
            InputResult r = ReadLine("密码: ", true, null, true);
            if (r.Aborted || r.Escaped)
            {
                Console.WriteLine("已取消。");
                return;
            }
            if (!Secrets.VerifyCanEnd(Locker.State, (r.Text ?? "").Trim()))
            {
                if (IsHelpText(r.Text))
                {
                    PrintHelpBlock();
                    return;
                }
                Console.WriteLine("密码错误，未取消。");
                return;
            }
            if (Locker.EndRestriction("用户从菜单结束"))
            {
                Console.WriteLine("已结束。");
            }
            else
            {
                Console.WriteLine("无法保存状态，限制仍然有效。");
            }
        }

        private static bool UninstallFlow()
        {
            if (Locker.Snapshot().Active)
            {
                Console.WriteLine("请先结束当前限制，再卸载。");
                return false;
            }

            InputResult pw = ReadLine("输入主密码确认卸载: ", true, null, true);
            if (pw.Aborted || pw.Escaped)
            {
                Console.WriteLine("已取消。");
                return false;
            }
            if (!Secrets.VerifyAdmin((pw.Text ?? "").Trim()))
            {
                if (IsHelpText(pw.Text))
                {
                    PrintHelpBlock();
                    Console.WriteLine("已取消。");
                    return false;
                }
                Console.WriteLine("密码错误，未卸载。");
                return false;
            }
            if (!Confirm("将移除开机自启并退出。输入 YES 确认: "))
            {
                Console.WriteLine("已取消。");
                return false;
            }

            Locker.ReleaseAll("卸载");
            Console.WriteLine("已移除开机自启，程序退出。");
            return true;
        }

        private static bool TryExit()
        {
            if (Locker.Snapshot().Active)
            {
                Console.WriteLine("限制生效期间不能退出；请先结束限制，或选择 7 隐藏到后台。");
                return false;
            }
            if (Locker.Settings.Autostart)
            {
                if (!Confirm("退出后开机自启仍然保留。输入 YES 退出: "))
                {
                    return false;
                }
            }
            Console.WriteLine("程序退出。");
            return true;
        }

        // ---------------- 设置 ----------------

        private static void SettingsMenu()
        {
            while (true)
            {
                AppSettings s = Locker.Settings;
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("----------------------- 设置 -----------------------");
                Console.ResetColor();
                PrintOnOff("1. 开机自启动    ", s.Autostart);
                PrintOnOff("2. 看门狗守护    ", s.Watchdog);
                PrintOnOff("3. 结束非系统程序", s.Enforce);
                PrintOnOff("4. 窗口置顶      ", s.TopMost);
                PrintOnOff("5. 锁定更换壁纸  ", s.Wallpaper);
                PrintOnOff("6. 锁定替换桌面图标", s.Icon);
                PrintOnOff("7. 允许隐藏窗口  ", s.AllowHide);
                Console.WriteLine("  0. 返回");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  本页无需密码；除第 7 项外，修改其他开关需要输入主密码。");
                Console.ResetColor();

                InputResult r = ReadLine("选择要切换的项目: ", false,
                    delegate { return Locker.Snapshot().Locked; }, true);
                if (r.Aborted || r.Escaped)
                {
                    return;
                }
                string cmd = (r.Text ?? "").Trim();
                if (cmd == "0" || cmd.Length == 0)
                {
                    return;
                }
                if (cmd.Length != 1 || cmd[0] < '1' || cmd[0] > '7')
                {
                    Console.WriteLine("无效选项。");
                    continue;
                }
                int idx = cmd[0] - '1';

                if (Locker.Snapshot().Active)
                {
                    Console.WriteLine("限制生效期间不能修改设置。");
                    continue;
                }

                AppSettings next = s.Clone();
                bool isHide = idx == 6;
                if (idx == 0) next.Autostart = !next.Autostart;
                else if (idx == 1) next.Watchdog = !next.Watchdog;
                else if (idx == 2) next.Enforce = !next.Enforce;
                else if (idx == 3) next.TopMost = !next.TopMost;
                else if (idx == 4) next.Wallpaper = !next.Wallpaper;
                else if (idx == 5) next.Icon = !next.Icon;
                else next.AllowHide = !next.AllowHide;

                if (!isHide)
                {
                    InputResult pw = ReadLine("请输入主密码以应用修改: ", true, null, true);
                    if (pw.Aborted || pw.Escaped)
                    {
                        Console.WriteLine("已取消。");
                        continue;
                    }
                    if (!Secrets.VerifyAdmin((pw.Text ?? "").Trim()))
                    {
                        if (IsHelpText(pw.Text))
                        {
                            PrintHelpBlock();
                            continue;
                        }
                        Console.WriteLine("密码错误，修改未生效。");
                        continue;
                    }
                }

                string error;
                if (!Locker.ApplySettings(next, out error))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("保存失败：" + (error ?? "未知错误"));
                    Console.ResetColor();
                    continue;
                }
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("设置已更新。");
                Console.ResetColor();
            }
        }

        private static bool IsHelpText(string text)
        {
            return string.Equals((text ?? "").Trim(), "help", StringComparison.OrdinalIgnoreCase);
        }

        private static void PrintHelpBlock()
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("============================ HELP ============================");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Wrap(AppInfo.HelpText, 92);
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==============================================================");
            Console.ResetColor();
        }

        private static void ShowHelp()
        {
            PrintHelpBlock();
            ReadLine("按 Enter 返回: ", false, null, true);
        }

        private static void HideToBackground()
        {
            if (!Locker.Settings.AllowHide)
            {
                Console.WriteLine("设置中已禁止隐藏窗口。");
                return;
            }
            ConsoleHost.Hide();
            Console.WriteLine("已隐藏到后台。再次运行 FocusLock.exe 可唤回。");

            while (!Locker.Stopping)
            {
                Thread.Sleep(250);
                if (ConsoleHost.Visible)
                {
                    return;
                }
                if (Locker.Snapshot().Locked)
                {
                    ConsoleHost.Show();
                    return;
                }
            }
        }

        // ---------------- 视觉 ----------------

        private static void TryApplyVisual(StatusSnapshot s)
        {
            AppSettings cfg = Locker.Settings;
            bool active = s.Locked;
            if (active != _visualActive)
            {
                _visualActive = active;
                if (active)
                {
                    ConsoleHost.Show();
                    if (cfg.TopMost) ConsoleHost.TopMost(true);
                    if (cfg.Icon) ApplyIcon();
                }
                else
                {
                    ConsoleHost.TopMost(false);
                    ConsoleHost.SetIcon(_ownIcon);
                }
            }

            if (s.Locked)
            {
                ConsoleHost.TopMost(cfg.TopMost);
            }
        }

        private static void ApplyIcon()
        {
            string path = Assets.FindIcon();
            if (path == null)
            {
                return;
            }
            Icon icon = Assets.LoadIcon(path);
            if (icon != null)
            {
                ConsoleHost.SetIcon(icon);
            }
        }

        private static Icon OwnIcon()
        {
            try
            {
                return Icon.ExtractAssociatedIcon(AppPaths.ExePath);
            }
            catch
            {
                return null;
            }
        }

        // ---------------- 输入 ----------------

        private static bool Confirm(string prompt)
        {
            InputResult r = ReadLine(prompt, false, null, true);
            if (r.Aborted || r.Escaped)
            {
                return false;
            }
            string t = (r.Text ?? "").Trim();
            return string.Equals(t, "YES", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(t, "Y", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(t, "是", StringComparison.Ordinal);
        }

        private sealed class InputResult
        {
            public string Text = "";
            public bool Escaped;
            public bool Aborted;
        }

        private static InputResult ReadLine(string prompt, bool mask, Func<bool> abort, bool allowEscape)
        {
            InputResult result = new InputResult();
            StringBuilder buffer = new StringBuilder();
            Console.Write(prompt);

            while (true)
            {
                if (buffer.Length == 0)
                {
                    foreach (string n in Locker.TakeNotices())
                    {
                        Console.WriteLine();
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine("  [" + DateTime.Now.ToString("HH:mm:ss") + "] " + n);
                        Console.ResetColor();
                        Console.Write(prompt);
                    }
                }

                if (abort != null)
                {
                    try
                    {
                        if (abort())
                        {
                            Console.WriteLine();
                            result.Aborted = true;
                            return result;
                        }
                    }
                    catch
                    {
                    }
                }

                bool available;
                try
                {
                    available = Console.KeyAvailable;
                }
                catch
                {
                    available = false;
                }
                if (!available)
                {
                    Thread.Sleep(40);
                    continue;
                }

                ConsoleKeyInfo key;
                try
                {
                    key = Console.ReadKey(true);
                }
                catch
                {
                    Thread.Sleep(60);
                    continue;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    result.Text = buffer.ToString();
                    return result;
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        Console.Write("\b \b");
                    }
                    continue;
                }
                if (key.Key == ConsoleKey.Escape)
                {
                    if (allowEscape)
                    {
                        Console.WriteLine();
                        result.Escaped = true;
                        return result;
                    }
                    Console.Beep();
                    continue;
                }
                if (char.IsControl(key.KeyChar))
                {
                    continue;
                }

                buffer.Append(key.KeyChar);
                Console.Write(mask ? '*' : key.KeyChar);
            }
        }

        // ---------------- 杂项 ----------------

        private static void CreateShowEvent()
        {
            _showEvent = Locker.CreateShowEvent();
            _showThread = new Thread(delegate()
            {
                while (!Locker.Stopping)
                {
                    try
                    {
                        if (_showEvent.WaitOne(500))
                        {
                            ConsoleHost.Show();
                        }
                    }
                    catch
                    {
                        return;
                    }
                }
            });
            _showThread.IsBackground = true;
            _showThread.Name = "FocusLock.ShowSignal";
            _showThread.Start();
        }

        private static void Cleanup()
        {
            if (_showEvent != null)
            {
                try
                {
                    _showEvent.Set();
                    _showEvent.Close();
                }
                catch
                {
                }
            }
            if (_mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch
                {
                }
                try
                {
                    _mutex.Close();
                }
                catch
                {
                }
            }
        }

        private static void FreeConsoleQuiet()
        {
            try
            {
                if (Native.GetConsoleWindow() != IntPtr.Zero)
                {
                    Native.FreeConsole();
                }
            }
            catch
            {
            }
        }

        private static bool HasArg(string[] args, string name)
        {
            if (args == null)
            {
                return false;
            }
            foreach (string a in args)
            {
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
