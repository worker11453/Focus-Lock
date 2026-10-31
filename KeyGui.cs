using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("FocusLock Key v2.0.1（图形版）")]
[assembly: AssemblyDescription("FocusLock 解除工具，图形界面")]
[assembly: AssemblyCompany("FocusLock")]
[assembly: AssemblyProduct("FocusLock")]
[assembly: AssemblyVersion("2.0.1.0")]
[assembly: AssemblyFileVersion("2.0.1.0")]

namespace FocusLock
{
    internal static class KeyGuiMain
    {
        [STAThread]
        private static int Main(string[] args)
        {
            AppPaths.Init();

            if (HasArg(args, "--preview"))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (KeyForm f = new KeyForm(true))
                {
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(-4000, -4000);
                    f.ShowInTaskbar = false;
                    f.Show();
                    Application.DoEvents();
                    Bitmap bmp = new Bitmap(f.Width, f.Height);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(f.BackColor);
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    }
                    string dir = args.Length > 1 ? args[1] : "preview";
                    Directory.CreateDirectory(dir);
                    bmp.Save(Path.Combine(dir, "key.png"), System.Drawing.Imaging.ImageFormat.Png);
                    bmp.Dispose();
                }
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new KeyForm(false));
            return 0;
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

    internal sealed class KeyForm : Form
    {
        private readonly Label _status;
        private readonly Label _result;
        private readonly Button _run;

        public KeyForm(bool preview)
        {
            Text = "FocusLock Key " + AppInfo.VersionTag;
            ClientSize = new Size(560, 400);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = preview ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
            BackColor = Ui.Bg;
            ForeColor = Ui.Text;
            AutoScaleMode = AutoScaleMode.None;
            Font = Ui.Font(9.5f);
            try
            {
                Icon = Icon.ExtractAssociatedIcon(AppPaths.ExePath);
            }
            catch
            {
            }

            Label title = Ui.MakeLabel("FocusLock Key", 18f, Ui.Red, true);
            title.Location = new Point(28, 26);
            Controls.Add(title);

            Label sub = Ui.MakeLabel(AppInfo.VersionTag + "  ·  解除工具 [图形版]", 9f, Ui.Muted, false);
            sub.Location = new Point(32, 62);
            Controls.Add(sub);

            Panel line = new Panel();
            line.Bounds = new Rectangle(28, 94, 504, 1);
            line.BackColor = Ui.Line;
            Controls.Add(line);

            Label desc = new Label();
            desc.Text = "运行本工具会立即结束当前限制、恢复桌面壁纸、移除开机自启，" +
                        "并结束所有 FocusLock 进程。\r\n\r\n" +
                        "这是一把“钥匙”：任何拿到它的人都可以解除 FocusLock，" +
                        "请把它放在只有你自己能访问的地方。";
            desc.Bounds = new Rectangle(28, 112, 504, 120);
            desc.ForeColor = Ui.Text;
            desc.Font = Ui.Font(10f);
            desc.BackColor = Color.Transparent;
            Controls.Add(desc);

            _status = Ui.MakeLabel("", 9.5f, Ui.Muted, false);
            _status.Location = new Point(28, 244);
            Controls.Add(_status);

            _run = new Button();
            _run.Text = "解除并退出 FocusLock";
            _run.Bounds = new Rectangle(28, 278, 260, 46);
            _run.FlatStyle = FlatStyle.Flat;
            _run.FlatAppearance.BorderColor = Ui.Red;
            _run.BackColor = Ui.Red;
            _run.ForeColor = Color.White;
            _run.Font = Ui.FontBold(11f);
            _run.Cursor = Cursors.Hand;
            _run.Click += delegate { Run(); };
            Controls.Add(_run);

            Button close = new Button();
            close.Text = "关闭";
            close.Bounds = new Rectangle(302, 278, 110, 46);
            close.FlatStyle = FlatStyle.Flat;
            close.FlatAppearance.BorderColor = Ui.Line;
            close.BackColor = Ui.Card;
            close.ForeColor = Ui.Text;
            close.Cursor = Cursors.Hand;
            close.Click += delegate { Close(); };
            Controls.Add(close);

            _result = Ui.MakeLabel("", 9.5f, Ui.Green, false);
            _result.Location = new Point(28, 344);
            Controls.Add(_result);

            UpdateStatus();
        }

        private void UpdateStatus()
        {
            LockState st = KeyTool.ReadDiskState();
            DateTime now = DateTime.UtcNow;
            if (st.IsLocked(now))
            {
                _status.Text = "当前状态：锁定中，剩余 " +
                    TimeText.FormatRemaining(TimeSpan.FromSeconds(Math.Max(0, (st.EndUtc - now).TotalSeconds)));
                _status.ForeColor = Ui.Red;
            }
            else if (st.IsScheduled(now))
            {
                _status.Text = "当前状态：有一个计划中的限制。";
                _status.ForeColor = Ui.Yellow;
            }
            else
            {
                _status.Text = "当前状态：没有生效的限制。";
                _status.ForeColor = Ui.Muted;
            }
        }

        private void Run()
        {
            if (MessageBox.Show(this,
                    "确定要解除 FocusLock 的全部功能并退出它吗？", AppInfo.Product,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            _run.Enabled = false;
            _result.Text = "正在解除...";
            Application.DoEvents();

            try
            {
                Locker.ReleaseAll("Key");
                int killed = KeyTool.StopFocusLock();
                _result.ForeColor = Ui.Green;
                _result.Text = "完成：限制已结束，开机自启已移除，结束进程 " + killed + " 个。";
                UpdateStatus();
            }
            catch (Exception ex)
            {
                _result.ForeColor = Ui.Red;
                _result.Text = "出错：" + ex.Message;
            }
            _run.Enabled = true;
        }
    }
}
