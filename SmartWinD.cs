/*
 * SmartWinD — умный Win+D только для основного монитора
 *
 * Компиляция (без Visual Studio):
 *   csc SmartWinD.cs /target:winexe /out:SmartWinD.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll
 *
 * Или просто создай проект в Visual Studio:
 *   - Новый проект → Windows Forms App (.NET Framework)
 *   - Замени содержимое Program.cs этим файлом
 *   - В свойствах проекта: Output type = Windows Application
 *   - Build → Build Solution
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

class SmartWinD : ApplicationContext
{
    // ── WinAPI ────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool   UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short  GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern bool   EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool   IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool   IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool   IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool   PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool   GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern int    GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string lpModuleName);

    // Мониторы
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO
    {
        public uint   cbSize;
        public RECT   rcMonitor;
        public RECT   rcWork;
        public uint   dwFlags;
    }
    const uint MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    delegate bool   EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    delegate bool   MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    const int  WH_KEYBOARD_LL = 13;
    const int  WM_KEYDOWN     = 0x0100;
    const int  WM_SYSKEYDOWN  = 0x0104;
    const int  VK_D           = 0x44;
    const int  VK_LWIN        = 0x5B;
    const int  VK_RWIN        = 0x5C;
    const uint WM_SYSCOMMAND  = 0x0112;
    const int  SC_MINIMIZE    = 0xF020;
    const int  SC_RESTORE     = 0xF120;
    const int  GWL_EXSTYLE    = -20;
    const int  WS_EX_TOOLWINDOW = 0x00000080;

    // ── Состояние ─────────────────────────────────────────────────────────────

    static readonly string[] IGNORE_KEYWORDS  = { "nvidia", "overlay", "program manager", "start", "geforce", "steam hook" };
    static readonly string[] DISCORD_KEYWORDS = { "discord" };

    static bool         _hidden = false;
    static List<IntPtr> _minimized = new List<IntPtr>();
    static object       _stateLock = new object();
    static bool         _busy = false;

    static IntPtr              _hookId = IntPtr.Zero;
    static LowLevelKeyboardProc _hookProc;   // держим ссылку — иначе GC соберёт

    // ── Точка входа ───────────────────────────────────────────────────────────

    [STAThread]
    static void Main()
    {
        // Проверка на дублирующий запуск
        bool created;
        using (new Mutex(true, "SmartWinD_SingleInstance", out created))
        {
            if (!created)
            {
                MessageBox.Show("SmartWinD уже запущен.", "SmartWinD", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            AddToStartup();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var app = new SmartWinD();
            app.InstallHook();

            Application.Run(app);

            UnhookWindowsHookEx(_hookId);
        }
    }

    // ── Трей ──────────────────────────────────────────────────────────────────

    NotifyIcon _tray;

    public SmartWinD()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("SmartWinD").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (s, e) => { Application.Exit(); });

        _tray = new NotifyIcon
        {
            Icon    = System.Drawing.SystemIcons.Application,
            Text    = "SmartWinD — Win+D для основного монитора",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tray?.Dispose();
        base.Dispose(disposing);
    }

    // ── Хук клавиатуры ────────────────────────────────────────────────────────

    void InstallHook()
    {
        _hookProc = HookCallback;
        using (var cur = Process.GetCurrentProcess())
        using (var mod = cur.MainModule)
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(mod.ModuleName), 0);
    }

    static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && ((int)wParam == WM_KEYDOWN || (int)wParam == WM_SYSKEYDOWN))
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (kb.vkCode == VK_D)
            {
                bool winDown = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0
                            || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
                if (winDown)
                {
                    // Запускаем в отдельном потоке, чтобы не тормозить хук
                    ThreadPool.QueueUserWorkItem(_ => ToggleDesktop());
                    return (IntPtr)1; // блокируем Win+D — Windows его не получит
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // ── Логика сворачивания ────────────────────────────────────────────────────

    static void ToggleDesktop()
    {
        lock (_stateLock)
        {
            if (_busy) return;
            _busy = true;
        }

        try
        {
            var (x1, y1, x2, y2) = GetPrimaryMonitorBounds();

            if (!_hidden)
            {
                var toMinimize = new List<IntPtr>();

                EnumWindows((hWnd, _) =>
                {
                    string title = GetTitle(hWnd);
                    bool isZen = title.IndexOf("zen", StringComparison.OrdinalIgnoreCase) >= 0
                              && !IsDiscord(title);

                    if (IsRealWindow(hWnd, title) || isZen)
                    {
                        if (GetWindowRect(hWnd, out RECT r))
                        {
                            int centerX = (r.Left + r.Right) / 2;
                            if (centerX >= x1 && centerX <= x2)
                            {
                                PostMessage(hWnd, WM_SYSCOMMAND, (IntPtr)SC_MINIMIZE, IntPtr.Zero);
                                toMinimize.Add(hWnd);
                            }
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                if (toMinimize.Count > 0)
                {
                    _minimized = toMinimize;
                    _hidden    = true;
                }
            }
            else
            {
                Thread.Sleep(100); // даём PostMessage отработать

                foreach (var hWnd in _minimized)
                {
                    if (IsWindow(hWnd) && IsIconic(hWnd))
                        PostMessage(hWnd, WM_SYSCOMMAND, (IntPtr)SC_RESTORE, IntPtr.Zero);
                }

                _minimized.Clear();
                _hidden = false;
            }
        }
        finally
        {
            lock (_stateLock) _busy = false;
        }
    }

    // ── Вспомогательные ───────────────────────────────────────────────────────

    static string GetTitle(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static bool IsDiscord(string title)
        => Array.Exists(DISCORD_KEYWORDS, k => title.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

    static bool IsRealWindow(IntPtr hWnd, string title)
    {
        if (string.IsNullOrEmpty(title))                      return false;
        if (IsDiscord(title))                                 return false;
        if (Array.Exists(IGNORE_KEYWORDS, k =>
            title.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
        if (!IsWindowVisible(hWnd) || IsIconic(hWnd))        return false;
        if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
        return true;
    }

    static (int x1, int y1, int x2, int y2) GetPrimaryMonitorBounds()
    {
        int rx1 = 0, ry1 = 0, rx2 = 1920, ry2 = 1080;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, hdc, ref lprc, data) =>
        {
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi) && (mi.dwFlags & MONITORINFOF_PRIMARY) != 0)
            {
                rx1 = mi.rcMonitor.Left;
                ry1 = mi.rcMonitor.Top;
                rx2 = mi.rcMonitor.Right;
                ry2 = mi.rcMonitor.Bottom;
                return false; // нашли, выходим
            }
            return true;
        }, IntPtr.Zero);

        return (rx1, ry1, rx2, ry2);
    }

    static void AddToStartup()
    {
        try
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.SetValue("SmartWinD", $"\"{exe}\"");
        }
        catch { /* молча игнорируем */ }
    }
}
