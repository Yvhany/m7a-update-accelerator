// M7A 更新加速 · 系统托盘监控器
//
// 为什么单独一个程序：
//   代理本体跑在 SYSTEM 计划任务里（session 0，没有桌面），无法自己显示托盘图标。
//   这个程序运行在登录用户的交互式会话，只做监控与操作，不改代理行为。
//
// 颜色含义：
//   绿 = 进程在、127.0.0.1:8899 在监听
//   黄 = 进程在，但端口连不上（可能正在启动或出错）
//   红 = 进程不存在
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

public class TrayContext : ApplicationContext
{
    private readonly string _installDir;
    private readonly string _logPath;
    private readonly int _port;

    private NotifyIcon _notify;
    private System.Windows.Forms.Timer _timer;
    private Icon _iconOk;
    private Icon _iconWarn;
    private Icon _iconBad;
    private string _state;

    private MenuItem _miStatus;
    private MenuItem _miMirror;
    private MenuItem _miAutostart;

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "M7A Update Proxy Tray";
    private const string TaskName = "M7A Update Proxy";

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    public TrayContext()
    {
        _installDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        _logPath = Path.Combine(_installDir, "mirror_proxy.log");
        _port = 8899;

        _iconOk = MakeIcon(Color.FromArgb(46, 160, 67));
        _iconWarn = MakeIcon(Color.FromArgb(219, 171, 10));
        _iconBad = MakeIcon(Color.FromArgb(218, 54, 51));

        _notify = new NotifyIcon();
        _notify.Icon = _iconBad;
        _notify.Text = "M7A 更新加速";
        _notify.Visible = true;
        _notify.ContextMenu = BuildMenu();
        _notify.DoubleClick += delegate { OpenLog(); };

        Refresh();
        _timer = new System.Windows.Forms.Timer();
        _timer.Interval = 3000;
        _timer.Tick += delegate { Refresh(); };
        _timer.Start();
    }

    private Icon MakeIcon(Color color)
    {
        using (Bitmap bmp = new Bitmap(16, 16))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush b = new SolidBrush(color))
                {
                    g.FillEllipse(b, 1, 1, 14, 14);
                }
            }
            IntPtr handle = bmp.GetHicon();
            try
            {
                return (Icon)Icon.FromHandle(handle).Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
    }

    private ContextMenu BuildMenu()
    {
        _miStatus = new MenuItem("检测中…");
        _miStatus.Enabled = false;
        _miMirror = new MenuItem("镜像: …");
        _miMirror.Enabled = false;

        MenuItem log = new MenuItem("查看日志");
        log.Click += delegate { OpenLog(); };

        MenuItem dir = new MenuItem("打开安装目录");
        dir.Click += delegate { OpenDir(); };

        MenuItem restart = new MenuItem("重启代理");
        restart.Click += delegate { RestartProxy(); };

        _miAutostart = new MenuItem("开机自动启动");
        _miAutostart.Click += delegate
        {
            SetAutostart(!IsAutostart());
            Refresh();
        };

        MenuItem exit = new MenuItem("退出");
        exit.Click += delegate
        {
            _timer.Stop();
            _notify.Visible = false;
            ExitThread();
        };

        ContextMenu menu = new ContextMenu();
        menu.MenuItems.Add(_miStatus);
        menu.MenuItems.Add(_miMirror);
        menu.MenuItems.Add(new MenuItem("-"));
        menu.MenuItems.Add(log);
        menu.MenuItems.Add(dir);
        menu.MenuItems.Add(restart);
        menu.MenuItems.Add(_miAutostart);
        menu.MenuItems.Add(new MenuItem("-"));
        menu.MenuItems.Add(exit);
        return menu;
    }

    private bool ProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName("m7a-mirror-proxy").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private bool PortOpen()
    {
        try
        {
            using (TcpClient client = new TcpClient())
            {
                IAsyncResult ar = client.BeginConnect("127.0.0.1", _port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(500))
                {
                    return false;
                }
                client.EndConnect(ar);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private string ReadTail(int maxBytes)
    {
        try
        {
            using (FileStream fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long length = fs.Length;
                int count = (int)Math.Min(length, maxBytes);
                fs.Seek(length - count, SeekOrigin.Begin);
                byte[] buffer = new byte[count];
                fs.Read(buffer, 0, count);
                return Encoding.UTF8.GetString(buffer);
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private string LastMirror()
    {
        string tail = ReadTail(16384);
        if (tail.Length == 0)
        {
            return "(暂无日志)";
        }
        string[] lines = tail.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            int at = lines[i].IndexOf(" via ", StringComparison.Ordinal);
            if (at > 0)
            {
                return lines[i].Substring(at + 5).Trim();
            }
        }
        return "(尚无下载记录)";
    }

    private static string Shorten(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }
        return text.Substring(0, max - 1) + "…";
    }

    private void Refresh()
    {
        bool running = ProcessRunning();
        bool listening = running && PortOpen();

        string state = listening ? "ok" : (running ? "warn" : "bad");
        if (state != _state)
        {
            _state = state;
            _notify.Icon = listening ? _iconOk : (running ? _iconWarn : _iconBad);
        }

        string summary;
        if (listening)
        {
            summary = "运行中 · 127.0.0.1:" + _port;
        }
        else if (running)
        {
            summary = "进程在但端口不通";
        }
        else
        {
            summary = "未运行";
        }

        _notify.Text = Shorten("M7A 更新加速 · " + summary, 60);
        _miStatus.Text = summary;
        _miMirror.Text = "镜像: " + Shorten(LastMirror(), 64);
        _miAutostart.Checked = IsAutostart();
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                MessageBox.Show("还没有日志文件：\r\n" + _logPath, "M7A 更新加速",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Process.Start("notepad.exe", "\"" + _logPath + "\"");
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "M7A 更新加速", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenDir()
    {
        try
        {
            Process.Start("explorer.exe", "\"" + _installDir + "\"");
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "M7A 更新加速", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RestartProxy()
    {
        try
        {
            ProcessStartInfo info = new ProcessStartInfo("schtasks.exe", "/Run /TN \"" + TaskName + "\"");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            using (Process p = Process.Start(info))
            {
                p.WaitForExit(8000);
                if (p.ExitCode != 0)
                {
                    MessageBox.Show("启动计划任务失败，退出码 " + p.ExitCode +
                        "。\r\n如果提示拒绝访问，请右键以管理员身份运行本程序。",
                        "M7A 更新加速", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            Thread.Sleep(1500);
            Refresh();
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "M7A 更新加速", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool IsAutostart()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                return key != null && key.GetValue(RunName) != null;
            }
        }
        catch
        {
            return false;
        }
    }

    private void SetAutostart(bool enabled)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null)
                {
                    return;
                }
                if (enabled)
                {
                    key.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                }
                else
                {
                    key.DeleteValue(RunName, false);
                }
            }
        }
        catch (Exception e)
        {
            MessageBox.Show("设置开机启动失败：" + e.Message, "M7A 更新加速",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

public static class TrayProgram
{
    [STAThread]
    public static void Main()
    {
        bool createdNew;
        using (Mutex mutex = new Mutex(true, "M7A_UpdateProxy_Tray", out createdNew))
        {
            if (!createdNew)
            {
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext());
        }
    }
}
