using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("FocusLock 专注锁 v2.0.1（图形版）")]
[assembly: AssemblyDescription("密码保护的定时专注锁，图形界面")]
[assembly: AssemblyCompany("FocusLock")]
[assembly: AssemblyProduct("FocusLock")]
[assembly: AssemblyVersion("2.0.1.0")]
[assembly: AssemblyFileVersion("2.0.1.0")]

namespace FocusLock
{
    internal static class GuiMain
    {
        private static Mutex _mutex;
        private static EventWaitHandle _showEvent;
        private static MainForm _form;

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
                return Locker.WatchdogMain();
            }
            if (HasArg(args, "--guard"))
            {
                return Locker.GuardMain();
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (HasArg(args, "--preview"))
            {
                return Preview(args);
            }

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

            try
            {
                AppPaths.EnsureRoot();
                Locker.WritePidFile(AppPaths.MainPidFile, Process.GetCurrentProcess().Id);

                _form = new MainForm(false);
                Application.Run(_form);
            }
            catch (Exception ex)
            {
                Log.Write("图形版致命错误: " + ex);
                MessageBox.Show("FocusLock 发生错误：\r\n" + ex.Message + "\r\n\r\n日志：" + AppPaths.LogFile,
                    AppInfo.Product, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Locker.Stop();
                if (_showEvent != null)
                {
                    try
                    {
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
            return 0;
        }

        public static void StartShowListener()
        {
            _showEvent = Locker.CreateShowEvent();
            Thread t = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        if (!_showEvent.WaitOne(500))
                        {
                            continue;
                        }
                        MainForm f = _form;
                        if (f != null && !f.IsDisposed)
                        {
                            f.BeginInvoke(new Action(f.RestoreFromTray));
                        }
                    }
                    catch
                    {
                        return;
                    }
                }
            });
            t.IsBackground = true;
            t.Name = "FocusLock.ShowSignal";
            t.Start();
        }

        private static int Preview(string[] args)
        {
            string dir = "preview";
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--preview", StringComparison.OrdinalIgnoreCase))
                {
                    dir = args[i + 1];
                }
            }
            Directory.CreateDirectory(dir);

            MainForm main = new MainForm(true);
            main.PreviewMode = true;
            main.ShowOffscreen();
            Snapshot(main, Path.Combine(dir, "main.png"));

            main.ShowLockedPreview();
            Snapshot(main, Path.Combine(dir, "locked.png"));
            main.Close();

            using (SettingsForm sf = new SettingsForm())
            {
                sf.ShowOffscreen();
                Snapshot(sf, Path.Combine(dir, "settings.png"));
            }
            using (NoticeForm nf = new NoticeForm(true))
            {
                nf.ShowOffscreen();
                Snapshot(nf, Path.Combine(dir, "notice.png"));
            }
            using (HelpForm hf = new HelpForm())
            {
                hf.ShowOffscreen();
                Snapshot(hf, Path.Combine(dir, "help.png"));
            }
            using (ScheduleForm sch = new ScheduleForm(false))
            {
                sch.ShowOffscreen();
                Snapshot(sch, Path.Combine(dir, "schedule.png"));
            }
            return 0;
        }

        private static void Snapshot(Form f, string path)
        {
            Application.DoEvents();
            f.Refresh();
            Application.DoEvents();
            Bitmap bmp = new Bitmap(f.Width, f.Height);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(f.BackColor);
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            }
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            bmp.Dispose();
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

    // ==================================================================
    // 主窗口
    // ==================================================================
    internal sealed class MainForm : Form
    {
        public bool PreviewMode;

        private readonly Label _statusBig;
        private readonly Label _statusDetail1;
        private readonly Label _statusDetail2;
        private readonly Label _statusDetail3;
        private readonly TextBox _log;
        private readonly System.Windows.Forms.Timer _timer;

        private Panel _overlay;
        private Label _overTitle;
        private Label _overCountdown;
        private Label _overInfo;
        private Label _overError;
        private TextBox _pw;
        private Button _endButton;

        private readonly List<Control> _content = new List<Control>();
        private bool _iconApplied;
        private Icon _normalIcon;
        private bool _noticeShown;

        public MainForm(bool preview)
        {
            PreviewMode = preview;
            Text = AppInfo.Product + " " + AppInfo.VersionTag;
            ClientSize = new Size(940, 640);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Ui.Bg;
            ForeColor = Ui.Text;
            AutoScaleMode = AutoScaleMode.None;
            Font = Ui.Font(9.5f);
            DoubleBuffered = true;
            try
            {
                _normalIcon = Icon.ExtractAssociatedIcon(AppPaths.ExePath);
                if (_normalIcon != null)
                {
                    Icon = _normalIcon;
                }
            }
            catch
            {
            }

            // 顶部
            Label title = Ui.MakeLabel(AppInfo.Product, 17f, Ui.Text, true);
            title.Location = new Point(26, 22);
            Controls.Add(title);

            Label version = Ui.MakeLabel(AppInfo.VersionTag + "  ·  控制台/图形双版本", 9f, Ui.Muted, false);
            version.Location = new Point(30, 56);
            Controls.Add(version);

            Label legal = Ui.MakeLabel("仅用于学习研究 · 严禁非法使用", 9f, Ui.Yellow, false);
            legal.AutoSize = false;
            legal.Bounds = new Rectangle(640, 30, 280, 24);
            legal.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(legal);

            Panel line = new Panel();
            line.Bounds = new Rectangle(24, 84, 892, 1);
            line.BackColor = Ui.Line;
            Controls.Add(line);

            // 左侧按钮
            int y = 106;
            AddAction("设置限制", 0, ref y, delegate { OpenSchedule(false); });
            AddAction("立即开始", 1, ref y, delegate { OpenSchedule(true); });
            AddAction("结束限制", 2, ref y, delegate { EndNow(); });
            AddAction("设置", 3, ref y, delegate { OpenSettings(); });
            AddAction("HELP", 4, ref y, delegate { OpenHelp(); });
            AddAction("隐藏到后台", 5, ref y, delegate { HideToTray(); });
            AddAction("卸载", 6, ref y, delegate { Uninstall(); });
            AddAction("退出", 7, ref y, delegate { Quit(); });

            // 右侧状态卡
            Panel card = new Panel();
            card.Bounds = new Rectangle(296, 106, 620, 214);
            card.BackColor = Ui.Card;
            Controls.Add(card);

            Label cap = Ui.MakeLabel("当前状态", 9f, Ui.Muted, false);
            cap.Location = new Point(22, 16);
            card.Controls.Add(cap);

            _statusBig = Ui.MakeLabel("空闲", 24f, Ui.Text, true);
            _statusBig.Location = new Point(20, 40);
            card.Controls.Add(_statusBig);

            _statusDetail1 = Ui.MakeLabel("", 9.5f, Ui.Muted, false);
            _statusDetail1.Location = new Point(24, 96);
            card.Controls.Add(_statusDetail1);

            _statusDetail2 = Ui.MakeLabel("", 9.5f, Ui.Muted, false);
            _statusDetail2.Location = new Point(24, 122);
            card.Controls.Add(_statusDetail2);

            _statusDetail3 = Ui.MakeLabel("", 9.5f, Ui.Muted, false);
            _statusDetail3.Location = new Point(24, 148);
            card.Controls.Add(_statusDetail3);

            Label foot = Ui.MakeLabel("密码仅在设置限制时设定，以 SHA256 保存", 9f, Ui.Muted, false);
            foot.Location = new Point(24, 180);
            card.Controls.Add(foot);

            // 日志
            Label logCap = Ui.MakeLabel("运行记录", 9f, Ui.Muted, false);
            logCap.Location = new Point(298, 334);
            Controls.Add(logCap);

            _log = new TextBox();
            _log.Bounds = new Rectangle(296, 358, 620, 244);
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = Ui.Card;
            _log.ForeColor = Ui.Text;
            _log.BorderStyle = BorderStyle.FixedSingle;
            _log.Font = Ui.Font(9f);
            Controls.Add(_log);

            Label footHint = Ui.MakeLabel(
                "设置与 HELP 无需密码 · 修改设置需主密码 · 限制期间窗口置顶且不能关闭",
                9f, Ui.Muted, false);
            footHint.Location = new Point(26, 612);
            Controls.Add(footHint);

            BuildOverlay();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 500;
            _timer.Tick += delegate { UpdateUi(); };
        }

        private void AddAction(string text, int index, ref int y, EventHandler handler)
        {
            Button b = Ui.MakeButton(text, 24, y, 248, 42);
            b.Click += handler;
            Controls.Add(b);
            y += 50;
        }

        private void BuildOverlay()
        {
            foreach (Control c in Controls)
            {
                _content.Add(c);
            }

            _overlay = new Panel();
            _overlay.Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            _overlay.BackColor = Color.FromArgb(40, 10, 12);
            _overlay.Visible = false;

            _overTitle = Ui.MakeLabel("限制进行中", 18f, Ui.Red, true);
            _overTitle.Location = new Point(60, 70);
            _overlay.Controls.Add(_overTitle);

            _overCountdown = Ui.MakeLabel("00:00:00", 48f, Color.White, true);
            _overCountdown.Location = new Point(54, 108);
            _overlay.Controls.Add(_overCountdown);

            _overInfo = Ui.MakeLabel("", 10f, Color.FromArgb(220, 200, 200), false);
            _overInfo.Location = new Point(62, 262);
            _overlay.Controls.Add(_overInfo);

            Label hint = Ui.MakeLabel(
                "输入本次密码，或永久结束密码 / 主密码，可提前结束", 9f,
                Color.FromArgb(200, 160, 160), false);
            hint.Location = new Point(62, 312);
            _overlay.Controls.Add(hint);

            _pw = new TextBox();
            _pw.Bounds = new Rectangle(60, 344, 380, 38);
            _pw.Font = Ui.Font(12f);
            _pw.PasswordChar = '●';
            _pw.BackColor = Color.FromArgb(30, 30, 34);
            _pw.ForeColor = Color.White;
            _pw.BorderStyle = BorderStyle.FixedSingle;
            _pw.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SubmitPassword();
                }
            };
            _overlay.Controls.Add(_pw);

            _endButton = new Button();
            _endButton.Text = "结束限制";
            _endButton.Bounds = new Rectangle(456, 344, 150, 38);
            _endButton.FlatStyle = FlatStyle.Flat;
            _endButton.FlatAppearance.BorderColor = Ui.Red;
            _endButton.BackColor = Ui.Red;
            _endButton.ForeColor = Color.White;
            _endButton.Font = Ui.FontBold(10f);
            _endButton.Cursor = Cursors.Hand;
            _endButton.Click += delegate { SubmitPassword(); };
            _overlay.Controls.Add(_endButton);

            _overError = Ui.MakeLabel("", 9.5f, Color.FromArgb(255, 150, 150), false);
            _overError.Location = new Point(62, 396);
            _overlay.Controls.Add(_overError);

            Controls.Add(_overlay);
            _overlay.BringToFront();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (PreviewMode)
            {
                return;
            }

            GuiMain.StartShowListener();
            Locker.Start();

            if (!_noticeShown)
            {
                _noticeShown = true;
                using (NoticeForm nf = new NoticeForm(false))
                {
                    if (nf.ShowDialog(this) != DialogResult.OK)
                    {
                        Close();
                        return;
                    }
                }
            }

            _timer.Start();
            UpdateUi();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 关机 / 重启必须放行，否则会把这个窗口以外的系统流程一起卡住。
            if (e.CloseReason == CloseReason.WindowsShutDown)
            {
                base.OnFormClosing(e);
                return;
            }
            if (!PreviewMode && Locker.VisualLocked)
            {
                e.Cancel = true;
                _overError.Text = "限制进行中，不能关闭窗口。请输入密码结束限制。";
                return;
            }
            base.OnFormClosing(e);
        }

        // ---------------- 状态刷新 ----------------

        private void UpdateUi()
        {
            if (Locker.Stopping)
            {
                Close();
                return;
            }

            StatusSnapshot s = Locker.Snapshot();
            AppSettings cfg = Locker.Settings;

            foreach (string n in Locker.TakeNotices())
            {
                AppendLog(n);
            }

            if (s.Locked)
            {
                if (!Visible)
                {
                    RestoreFromTray();
                }
                _overlay.Visible = true;
                _overlay.BringToFront();
                SetContentVisible(false);
                TopMost = cfg.TopMost;
                ApplyLockIcon(cfg.Icon);

                if (s.GraceRemaining > 0)
                {
                    _overTitle.Text = "缓 冲 中";
                    _overCountdown.Text = s.GraceRemaining.ToString() + " 秒";
                    _overInfo.Text = "即将开始清理非系统程序。现在输入密码可以取消。";
                }
                else
                {
                    _overTitle.Text = "限制进行中";
                    _overCountdown.Text = TimeText.FormatRemaining(TimeSpan.FromSeconds(s.UntilEndSeconds));
                    _overInfo.Text = "开始 " + TimeText.FormatLocal(s.StartUtc) +
                                     "    结束 " + TimeText.FormatLocal(s.EndUtc) +
                                     "    已结束 " + s.Killed + " 个程序";
                }
                Text = s.Title();
            }
            else
            {
                if (_overlay.Visible)
                {
                    _overlay.Visible = false;
                    SetContentVisible(true);
                    _pw.Clear();
                    _overError.Text = "";
                }
                TopMost = false;
                ApplyLockIcon(false);

                if (s.Scheduled)
                {
                    _statusBig.Text = "计划中";
                    _statusBig.ForeColor = Ui.Yellow;
                    _statusDetail1.Text = "开始 " + TimeText.FormatLocal(s.StartUtc);
                    _statusDetail2.Text = "结束 " + TimeText.FormatLocal(s.EndUtc);
                    _statusDetail3.Text = "距开始 " + TimeText.FormatRemaining(TimeSpan.FromSeconds(s.UntilStartSeconds));
                }
                else
                {
                    _statusBig.Text = "空闲";
                    _statusBig.ForeColor = Ui.Text;
                    _statusDetail1.Text = "没有生效的限制。";
                    _statusDetail2.Text = "开机自启：" + (cfg.Autostart ? "ON" : "OFF") +
                                          "    看门狗：" + (cfg.Watchdog ? "ON" : "OFF") +
                                          "    进程清理：" + (cfg.Enforce ? "ON" : "OFF");
                    _statusDetail3.Text = "壁纸/图标：" + ((cfg.Wallpaper || cfg.Icon) ? "ON" : "OFF");
                }
                Text = s.Title();
            }
        }

        private void SetContentVisible(bool visible)
        {
            foreach (Control c in _content)
            {
                if (!c.IsDisposed)
                {
                    c.Visible = visible;
                }
            }
        }
        private void ApplyLockIcon(bool locked)
        {
            if (locked == _iconApplied)
            {
                return;
            }
            _iconApplied = locked;
            if (locked)
            {
                string path = Assets.FindIcon();
                Icon icon = Assets.LoadIcon(path);
                if (icon != null)
                {
                    Icon = icon;
                }
            }
            else
            {
                Icon = _normalIcon;
            }
        }

        private void AppendLog(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message + "\r\n";
            _log.AppendText(line);
            try
            {
                if (_log.TextLength > 20000)
                {
                    _log.Text = _log.Text.Substring(_log.TextLength - 12000);
                }
            }
            catch
            {
            }
        }

        // ---------------- 动作 ----------------

        private void SubmitPassword()
        {
            string text = (_pw.Text ?? "").Trim();
            if (text.Length == 0)
            {
                _overError.Text = "请输入密码。";
                return;
            }
            if (string.Equals(text, "help", StringComparison.OrdinalIgnoreCase))
            {
                using (HelpForm hf = new HelpForm())
                {
                    hf.ShowDialog(this);
                }
                _pw.Clear();
                _pw.Focus();
                return;
            }
            if (Secrets.VerifyCanEnd(Locker.State, text))
            {
                if (Locker.EndRestriction("密码验证通过，提前结束限制"))
                {
                    _pw.Clear();
                    _overError.Text = "";
                    UpdateUi();
                }
                else
                {
                    _overError.Text = "无法保存状态，限制继续。";
                }
            }
            else
            {
                _overError.Text = "密码错误，限制继续。";
                _pw.Clear();
                _pw.Focus();
            }
        }

        private void EndNow()
        {
            StatusSnapshot s = Locker.Snapshot();
            if (!s.Active)
            {
                MessageBox.Show(this, "当前没有生效的计划或限制。", AppInfo.Product,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string pw;
            if (!AskPassword("输入密码结束当前限制", out pw))
            {
                return;
            }
            if (!Secrets.VerifyCanEnd(Locker.State, pw))
            {
                MessageBox.Show(this, "密码错误，限制继续。", AppInfo.Product,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (Locker.EndRestriction("用户结束限制"))
            {
                AppendLog("已手动结束限制。");
            }
            else
            {
                MessageBox.Show(this, "无法保存状态，限制仍然有效。", AppInfo.Product,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenSchedule(bool immediate)
        {
            if (Locker.Snapshot().Active)
            {
                MessageBox.Show(this, "已有计划或限制，请先结束它。", AppInfo.Product,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (ScheduleForm sf = new ScheduleForm(immediate))
            {
                if (sf.ShowDialog(this) == DialogResult.OK)
                {
                    AppendLog(sf.ResultMessage);
                    UpdateUi();
                }
            }
        }

        private void OpenSettings()
        {
            using (SettingsForm sf = new SettingsForm())
            {
                sf.ShowDialog(this);
            }
            UpdateUi();
        }

        private void OpenHelp()
        {
            using (HelpForm hf = new HelpForm())
            {
                hf.ShowDialog(this);
            }
        }

        private void HideToTray()
        {
            if (!Locker.Settings.AllowHide)
            {
                MessageBox.Show(this, "设置中已禁止隐藏窗口。", AppInfo.Product,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Hide();
        }

        public void RestoreFromTray()
        {
            if (IsDisposed)
            {
                return;
            }
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void Uninstall()
        {
            if (Locker.Snapshot().Active)
            {
                MessageBox.Show(this, "请先结束当前限制，再卸载。", AppInfo.Product,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string pw;
            if (!AskPassword("输入主密码确认卸载", out pw))
            {
                return;
            }
            if (!Secrets.VerifyAdmin(pw))
            {
                MessageBox.Show(this, "密码错误，未卸载。", AppInfo.Product,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(this, "将移除开机自启并退出 FocusLock。确定继续？", AppInfo.Product,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }
            Locker.ReleaseAll("卸载");
            Close();
        }

        private void Quit()
        {
            if (Locker.Snapshot().Active)
            {
                MessageBox.Show(this, "限制生效期间不能退出；请先结束限制，或选择“隐藏到后台”。",
                    AppInfo.Product, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Close();
        }

        private bool AskPassword(string caption, out string password)
        {
            password = null;
            using (PasswordForm pf = new PasswordForm(caption))
            {
                if (pf.ShowDialog(this) != DialogResult.OK)
                {
                    return false;
                }
                password = pf.Value;
                return true;
            }
        }

        // ---------------- 预览 ----------------

        public void ShowOffscreen()
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-4000, -4000);
            ShowInTaskbar = false;
            Show();
            UpdateUi();
            Application.DoEvents();
        }

        public void ShowLockedPreview()
        {
            _overlay.Visible = true;
            _overlay.BringToFront();
            SetContentVisible(false);
            _overTitle.Text = "限制进行中";
            _overCountdown.Text = "01:23:45";
            _overInfo.Text = "开始 2026-10-24 20:30:00    结束 2026-10-24 22:00:00    已结束 12 个程序";
            Text = AppInfo.Product + " | 锁定中 | 剩余 01:23:45";
            Application.DoEvents();
        }
    }

    // ==================================================================
    // 启动提示
    // ==================================================================
    internal sealed class NoticeForm : Form
    {
        public NoticeForm(bool preview)
        {
            Text = "使用提示";
            ClientSize = new Size(720, 430);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Bg;
            ForeColor = Ui.Text;
            AutoScaleMode = AutoScaleMode.None;
            Font = Ui.Font(9.5f);
            if (preview)
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-4000, -4000);
                ShowInTaskbar = false;
            }

            Label title = Ui.MakeLabel("使用前请阅读", 15f, Ui.Yellow, true);
            title.Location = new Point(28, 24);
            Controls.Add(title);

            Panel line = new Panel();
            line.Bounds = new Rectangle(28, 60, 664, 1);
            line.BackColor = Ui.Line;
            Controls.Add(line);

            Label body = new Label();
            body.Text = AppInfo.NoticeText;
            body.Bounds = new Rectangle(28, 78, 664, 240);
            body.ForeColor = Ui.Text;
            body.Font = Ui.Font(11.5f);
            body.BackColor = Color.Transparent;
            Controls.Add(body);

            Label tip = Ui.MakeLabel(
                "本程序会强制结束当前会话中的普通程序，可能造成未保存的数据丢失。", 9f, Ui.Muted, false);
            tip.Location = new Point(28, 330);
            Controls.Add(tip);

            Button ok = new Button();
            ok.Text = "我已阅读并同意";
            ok.Bounds = new Rectangle(474, 368, 218, 38);
            ok.FlatStyle = FlatStyle.Flat;
            ok.FlatAppearance.BorderColor = Ui.Red;
            ok.BackColor = Ui.Red;
            ok.ForeColor = Color.White;
            ok.Font = Ui.FontBold(10f);
            ok.DialogResult = DialogResult.OK;
            ok.Cursor = Cursors.Hand;
            Controls.Add(ok);

            Button no = new Button();
            no.Text = "不同意并退出";
            no.Bounds = new Rectangle(330, 368, 134, 38);
            no.FlatStyle = FlatStyle.Flat;
            no.FlatAppearance.BorderColor = Ui.Line;
            no.BackColor = Ui.Card;
            no.ForeColor = Ui.Muted;
            no.DialogResult = DialogResult.Cancel;
            no.Cursor = Cursors.Hand;
            Controls.Add(no);

            AcceptButton = ok;
            CancelButton = no;
        }

        public void ShowOffscreen()
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-4000, -4000);
            ShowInTaskbar = false;
            Show();
            Application.DoEvents();
        }
    }

    // ==================================================================
    // HELP
    // ==================================================================
    internal sealed class HelpForm : Form
    {
        public HelpForm()
        {
            Text = "HELP";
            ClientSize = new Size(660, 330);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Bg;
            ForeColor = Ui.Text;
            AutoScaleMode = AutoScaleMode.None;
            Font = Ui.Font(9.5f);

            Label title = Ui.MakeLabel("需要帮助？", 15f, Ui.Yellow, true);
            title.Location = new Point(28, 22);
            Controls.Add(title);

            Label body = new Label();
            body.Text = AppInfo.HelpText;
            body.Bounds = new Rectangle(28, 70, 604, 150);
            body.ForeColor = Ui.Text;
            body.Font = Ui.Font(10.5f);
            body.BackColor = Color.Transparent;
            Controls.Add(body);

            Label mail = Ui.MakeLabel("email: huangwenbo@hgt46.wecom.work", 10.5f, Ui.Yellow, true);
            mail.Location = new Point(28, 226);
            Controls.Add(mail);

            Button ok = new Button();
            ok.Text = "关闭";
            ok.Bounds = new Rectangle(534, 268, 100, 34);
            ok.FlatStyle = FlatStyle.Flat;
            ok.FlatAppearance.BorderColor = Ui.Line;
            ok.BackColor = Ui.Card;
            ok.ForeColor = Ui.Text;
            ok.DialogResult = DialogResult.OK;
            ok.Cursor = Cursors.Hand;
            Controls.Add(ok);
            AcceptButton = ok;
        }

        public void ShowOffscreen()
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-4000, -4000);
            ShowInTaskbar = false;
            Show();
            Application.DoEvents();
        }
    }

    // ==================================================================
    // 输入密码
    // ==================================================================
    internal sealed class PasswordForm : Form
    {
        private readonly TextBox _box;
        public string Value;

        public PasswordForm(string caption)
        {
            Text = AppInfo.Product;
            ClientSize = new Size(420, 170);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Bg;
            ForeColor = Ui.Text;
            AutoScaleMode = AutoScaleMode.None;
            Font = Ui.Font(9.5f);

            Label label = Ui.MakeLabel(caption, 10f, Ui.Text, false);
            label.Location = new Point(24, 22);
            Controls.Add(label);

            _box = new TextBox();
            _box.Bounds = new Rectangle(24, 52, 372, 30);
            _box.PasswordChar = '●';
            _box.Font = Ui.Font(11f);
            _box.BackColor = Ui.Card;
            _box.ForeColor = Ui.Text;
            _box.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(_box);

            Button ok = new Button();
            ok.Text = "确定";
            ok.Bounds = new Rectangle(296, 108, 100, 34);
            ok.FlatStyle = FlatStyle.Flat;
            ok.FlatAppearance.BorderColor = Ui.Red;
            ok.BackColor = Ui.Red;
            ok.ForeColor = Color.White;
            ok.Cursor = Cursors.Hand;
            ok.Click += delegate
            {
                string text = (_box.Text ?? "").Trim();
                if (string.Equals(text, "help", StringComparison.OrdinalIgnoreCase))
                {
                    using (HelpForm hf = new HelpForm())
                    {
                        hf.ShowDialog(this);
                    }
                    _box.Clear();
                    _box.Focus();
                    return;
                }
                Value = _box.Text;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Bounds = new Rectangle(186, 108, 100, 34);
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.FlatAppearance.BorderColor = Ui.Line;
            cancel.BackColor = Ui.Card;
            cancel.ForeColor = Ui.Text;
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Cursor = Cursors.Hand;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
            Shown += delegate { _box.Focus(); };
        }
    }

    // ==================================================================
    // 设置
    // ==================================================================
    internal sealed class SettingsForm : Form
    {
        private readonly Button[] _buttons = new Button[7];
        private readonly Label _hint;

        public SettingsForm()
        {
            Text = "设置";
            ClientSize = new Size(700, 520);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Bg;
            ForeColor = Ui.Text;
            AutoScaleMode = AutoScaleMode.None;
            Font = Ui.Font(9.5f);

            Label title = Ui.MakeLabel("设置", 15f, Ui.Text, true);
            title.Location = new Point(26, 20);
            Controls.Add(title);

            Label sub = Ui.MakeLabel("打开本页无需密码；除“允许隐藏”外，修改其他开关需要输入主密码。",
                9f, Ui.Muted, false);
            sub.Location = new Point(28, 52);
            Controls.Add(sub);

            string[] names =
            {
                "开机自启动", "看门狗守护", "结束非系统程序", "窗口置顶",
                "锁定更换壁纸", "锁定替换桌面图标", "允许隐藏窗口"
            };
            string[] notes =
            {
                "登录 Windows 后自动打开", "被结束后自动拉起", "锁定期间清理普通程序",
                "锁定窗口保持最前", "使用同目录 （1）.jpg/png", "桌面所有快捷方式换成 （2）",
                "按 Esc / 隐藏按钮后继续后台运行"
            };

            for (int i = 0; i < names.Length; i++)
            {
                int top = 86 + i * 54;
                Label l = Ui.MakeLabel(names[i], 10f, Ui.Text, false);
                l.Location = new Point(28, top + 10);
                Controls.Add(l);

                Label n = Ui.MakeLabel(notes[i], 8.5f, Ui.Muted, false);
                n.Location = new Point(170, top + 12);
                Controls.Add(n);

                Button b = new Button();
                b.Bounds = new Rectangle(552, top + 6, 116, 34);
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = Ui.Line;
                b.Font = Ui.FontBold(10f);
                b.Cursor = Cursors.Hand;
                int index = i;
                b.Click += delegate { Toggle(index); };
                Controls.Add(b);
                _buttons[i] = b;
            }

            _hint = Ui.MakeLabel("", 9f, Ui.Muted, false);
            _hint.Location = new Point(28, 470);
            _hint.AutoSize = false;
            _hint.Bounds = new Rectangle(28, 466, 500, 24);
            Controls.Add(_hint);

            Button close = new Button();
            close.Text = "关闭";
            close.Bounds = new Rectangle(568, 462, 100, 34);
            close.FlatStyle = FlatStyle.Flat;
            close.FlatAppearance.BorderColor = Ui.Line;
            close.BackColor = Ui.Card;
            close.ForeColor = Ui.Text;
            close.DialogResult = DialogResult.OK;
            close.Cursor = Cursors.Hand;
            Controls.Add(close);

            RefreshButtons();
        }

        private void RefreshButtons()
        {
            AppSettings s = Locker.Settings;
            bool[] values =
            {
                s.Autostart, s.Watchdog, s.Enforce, s.TopMost,
                s.Wallpaper, s.Icon, s.AllowHide
            };
            for (int i = 0; i < _buttons.Length; i++)
            {
                Button b = _buttons[i];
                b.Text = values[i] ? "ON" : "OFF";
                b.BackColor = values[i] ? Ui.Red : Ui.Green;
                b.ForeColor = Color.White;
            }
            _hint.Text = "提示：红色 ON 表示开启，绿色 OFF 表示关闭。";
        }

        private void Toggle(int index)
        {
            if (Locker.Snapshot().Active)
            {
                _hint.Text = "限制生效期间不能修改设置。";
                return;
            }

            AppSettings next = Locker.Settings;
            switch (index)
            {
                case 0: next.Autostart = !next.Autostart; break;
                case 1: next.Watchdog = !next.Watchdog; break;
                case 2: next.Enforce = !next.Enforce; break;
                case 3: next.TopMost = !next.TopMost; break;
                case 4: next.Wallpaper = !next.Wallpaper; break;
                case 5: next.Icon = !next.Icon; break;
                default: next.AllowHide = !next.AllowHide; break;
            }

            if (index != 6)
            {
                using (PasswordForm pf = new PasswordForm("请输入主密码以应用修改"))
                {
                    if (pf.ShowDialog(this) != DialogResult.OK)
                    {
                        return;
                    }
                    if (!Secrets.VerifyAdmin(pf.Value))
                    {
                        _hint.Text = "密码错误，修改未生效。";
                        return;
                    }
                }
            }

            string error;
            if (!Locker.ApplySettings(next, out error))
            {
                _hint.Text = "保存失败：" + (error ?? "未知错误");
            }
            else
            {
                _hint.Text = "设置已更新。";
            }
            RefreshButtons();
        }

        public void ShowOffscreen()
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-4000, -4000);
            ShowInTaskbar = false;
            Show();
            Application.DoEvents();
        }
    }

    // ==================================================================
    // 设置限制
    // ==================================================================
    internal sealed class ScheduleForm : Form
    {
        private readonly TextBox _start;
        private readonly TextBox _duration;
        private readonly TextBox _pw1;
        private readonly TextBox _pw2;
        private readonly Label _error;
        private readonly Label _startLabel;
        public string ResultMessage;

        public ScheduleForm(bool immediate)
        {
            Text = immediate ? "立即开始限制" : "设置限制";
            ClientSize = new Size(560, 470);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Ui.Bg;
            ForeColor = Ui.Text;
            AutoScaleMode = AutoScaleMode.None;
            Font = Ui.Font(9.5f);

            Label title = Ui.MakeLabel(immediate ? "立即开始限制" : "设置限制", 14f, Ui.Text, true);
            title.Location = new Point(26, 20);
            Controls.Add(title);

            _startLabel = Ui.MakeLabel("开始时间（HH:mm 或 yyyy-MM-dd HH:mm，回车 = 立即）", 9.5f, Ui.Muted, false);
            _startLabel.Location = new Point(28, 66);
            Controls.Add(_startLabel);

            _start = new TextBox();
            _start.Bounds = new Rectangle(28, 90, 500, 30);
            _start.BackColor = Ui.Card;
            _start.ForeColor = Ui.Text;
            _start.BorderStyle = BorderStyle.FixedSingle;
            _start.Font = Ui.Font(10.5f);
            _start.Text = "now";
            _start.Enabled = !immediate;
            Controls.Add(_start);

            Label dl = Ui.MakeLabel("持续时间（分钟，也可写 1h30m / 01:30）", 9.5f, Ui.Muted, false);
            dl.Location = new Point(28, 136);
            Controls.Add(dl);

            _duration = new TextBox();
            _duration.Bounds = new Rectangle(28, 160, 500, 30);
            _duration.BackColor = Ui.Card;
            _duration.ForeColor = Ui.Text;
            _duration.BorderStyle = BorderStyle.FixedSingle;
            _duration.Font = Ui.Font(10.5f);
            _duration.Text = "90";
            Controls.Add(_duration);

            Label pl = Ui.MakeLabel("本次限制密码（至少 4 位）", 9.5f, Ui.Muted, false);
            pl.Location = new Point(28, 206);
            Controls.Add(pl);

            _pw1 = new TextBox();
            _pw1.Bounds = new Rectangle(28, 230, 240, 30);
            _pw1.BackColor = Ui.Card;
            _pw1.ForeColor = Ui.Text;
            _pw1.BorderStyle = BorderStyle.FixedSingle;
            _pw1.PasswordChar = '●';
            _pw1.Font = Ui.Font(10.5f);
            Controls.Add(_pw1);

            _pw2 = new TextBox();
            _pw2.Bounds = new Rectangle(288, 230, 240, 30);
            _pw2.BackColor = Ui.Card;
            _pw2.ForeColor = Ui.Text;
            _pw2.BorderStyle = BorderStyle.FixedSingle;
            _pw2.PasswordChar = '●';
            _pw2.Font = Ui.Font(10.5f);
            Controls.Add(_pw2);

            Label pl2 = Ui.MakeLabel("再次输入密码", 9f, Ui.Muted, false);
            pl2.Location = new Point(288, 206);
            Controls.Add(pl2);

            _error = Ui.MakeLabel("", 9.5f, Ui.Red, false);
            _error.AutoSize = false;
            _error.Bounds = new Rectangle(28, 274, 500, 60);
            Controls.Add(_error);

            Button ok = new Button();
            ok.Text = immediate ? "开始" : "保存";
            ok.Bounds = new Rectangle(388, 400, 140, 38);
            ok.FlatStyle = FlatStyle.Flat;
            ok.FlatAppearance.BorderColor = Ui.Red;
            ok.BackColor = Ui.Red;
            ok.ForeColor = Color.White;
            ok.Font = Ui.FontBold(10f);
            ok.Cursor = Cursors.Hand;
            ok.Click += delegate { Submit(); };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Bounds = new Rectangle(268, 400, 110, 38);
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.FlatAppearance.BorderColor = Ui.Line;
            cancel.BackColor = Ui.Card;
            cancel.ForeColor = Ui.Text;
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Cursor = Cursors.Hand;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void Submit()
        {
            DateTime start = DateTime.Now;
            if (_start.Enabled)
            {
                string note;
                if (!TimeText.TryParseStart(_start.Text, out start, out note))
                {
                    _error.Text = "开始时间无效，请重新填写。";
                    return;
                }
            }

            int minutes;
            if (!TimeText.TryParseDuration(_duration.Text, out minutes))
            {
                _error.Text = "持续时间无效，请输入 1 到 10080 分钟。";
                return;
            }

            string error;
            if (!Locker.SetSchedule(start, minutes, _pw1.Text, _pw2.Text, out error))
            {
                _error.Text = error;
                return;
            }

            LockState st = Locker.State;
            ResultMessage = "已保存限制：" + TimeText.FormatLocal(st.StartUtc) + " → " +
                            TimeText.FormatLocal(st.EndUtc) + "（" + TimeText.FormatDuration(st.EndUtc - st.StartUtc) + "）";
            DialogResult = DialogResult.OK;
            Close();
        }

        public void ShowOffscreen()
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-4000, -4000);
            ShowInTaskbar = false;
            Show();
            Application.DoEvents();
        }
    }
}
