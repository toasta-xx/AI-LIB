using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace NullPlay
{
    static class Program
    {
        const int KeyUp = 2;
        const byte Alt = 0x12;

        delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumProc callback, IntPtr lparam);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool BringWindowToTop(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint from, uint to, bool attach);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        static extern void keybd_event(byte key, byte scan, int flags, UIntPtr extra);

        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                string command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                string url = args.Length > 1 ? args[1] : "";
                IntPtr hwnd = Find();
                if (command == "query")
                {
                    Console.WriteLine(hwnd == IntPtr.Zero ? "NONE" : "FOUND");
                    return 0;
                }
                if (command == "play")
                {
                    if (url.Length == 0)
                    {
                        Console.WriteLine("FAIL");
                        return 1;
                    }
                    if (hwnd == IntPtr.Zero)
                    {
                        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                        Console.WriteLine("OPENED");
                        return 0;
                    }
                    if (!Navigate(hwnd, url))
                    {
                        Console.WriteLine("FAIL");
                        return 1;
                    }
                    Console.WriteLine("NAV");
                    return 0;
                }
                if (hwnd == IntPtr.Zero)
                {
                    Console.WriteLine("NONE");
                    return 0;
                }
                if (!Press(hwnd, command))
                {
                    Console.WriteLine("FAIL");
                    return 1;
                }
                Console.WriteLine("OK");
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.ToString());
                Console.Error.Flush();
                Console.WriteLine("FAIL");
                return 1;
            }
        }

        static IntPtr Find()
        {
            List<IntPtr> windows = new List<IntPtr>();
            EnumWindows(delegate (IntPtr hwnd, IntPtr lparam)
            {
                if (IsWindowVisible(hwnd) && IsBrowser(hwnd))
                {
                    string title = Title(hwnd);
                    if (title.Length > 0)
                        windows.Add(hwnd);
                }
                return true;
            }, IntPtr.Zero);
            for (int i = 0; i < windows.Count; i++)
            {
                if (IsMusicTitle(Title(windows[i])))
                    return windows[i];
            }
            for (int i = 0; i < windows.Count; i++)
            {
                if (SelectMusicTab(windows[i]))
                    return windows[i];
            }
            return IntPtr.Zero;
        }

        static bool SelectMusicTab(IntPtr hwnd)
        {
            try
            {
                AutomationElement window = AutomationElement.FromHandle(hwnd);
                if (window == null)
                    return false;
                var tabs = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                for (int i = 0; i < tabs.Count; i++)
                {
                    AutomationElement tab = tabs[i];
                    string name = tab.Current.Name ?? "";
                    if (!IsMusicTitle(name))
                        continue;
                    object pattern;
                    if (!tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
                        continue;
                    ((SelectionItemPattern)pattern).Select();
                    return true;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.Message);
            }
            return false;
        }

        static bool IsMusicTitle(string title)
        {
            if (string.IsNullOrEmpty(title))
                return false;
            if (title.IndexOf("Google Search", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return title.IndexOf("YouTube Music", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsBrowser(IntPtr hwnd)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            try
            {
                string name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
                return name == "chrome" || name == "msedge" || name == "firefox" || name == "brave" || name == "opera" || name == "vivaldi" || name == "chromium";
            }
            catch
            {
                return false;
            }
        }

        static string Title(IntPtr hwnd)
        {
            var text = new StringBuilder(512);
            GetWindowText(hwnd, text, text.Capacity);
            return text.ToString();
        }

        static bool Navigate(IntPtr hwnd, string url)
        {
            if (!Focus(hwnd))
                return false;
            Thread.Sleep(180);
            if (GetForegroundWindow() != hwnd)
                return false;
            string prior = null;
            bool had = false;
            try
            {
                if (Clipboard.ContainsText())
                {
                    prior = Clipboard.GetText();
                    had = true;
                }
            }
            catch
            {
            }
            if (!PutClipboard(url))
                return false;
            try
            {
                SendKeys.SendWait("{ESC}");
                Thread.Sleep(80);
                SendKeys.SendWait("^l");
                Thread.Sleep(220);
                SendKeys.SendWait("^a");
                SendKeys.SendWait("^v");
                Thread.Sleep(280);
                SendKeys.SendWait("{ENTER}");
                Thread.Sleep(350);
                return true;
            }
            finally
            {
                try
                {
                    if (had && prior != null)
                        Clipboard.SetText(prior);
                    else
                        Clipboard.Clear();
                }
                catch
                {
                }
            }
        }

        static bool Press(IntPtr hwnd, string command)
        {
            if (!Focus(hwnd))
                return false;
            Thread.Sleep(160);
            if (GetForegroundWindow() != hwnd)
                return false;
            SendKeys.SendWait("{ESC}");
            Thread.Sleep(50);
            if (command == "pause" || command == "resume")
                SendKeys.SendWait(" ");
            else if (command == "next")
                SendKeys.SendWait("+n");
            else if (command == "prev")
                SendKeys.SendWait("+p");
            else if (command == "mute" || command == "unmute")
                SendKeys.SendWait("m");
            else if (command == "louder")
                SendKeys.SendWait("=");
            else if (command == "quieter")
                SendKeys.SendWait("-");
            else if (command == "restart")
                SendKeys.SendWait("0");
            else
                return false;
            return true;
        }

        static bool Focus(IntPtr hwnd)
        {
            ShowWindow(hwnd, 9);
            keybd_event(Alt, 0, 0, UIntPtr.Zero);
            keybd_event(Alt, 0, KeyUp, UIntPtr.Zero);
            IntPtr current = GetForegroundWindow();
            uint ignored;
            uint currentThread = GetWindowThreadProcessId(current, out ignored);
            uint targetThread = GetWindowThreadProcessId(hwnd, out ignored);
            uint mine = GetCurrentThreadId();
            AttachThreadInput(mine, currentThread, true);
            AttachThreadInput(mine, targetThread, true);
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            AttachThreadInput(mine, targetThread, false);
            AttachThreadInput(mine, currentThread, false);
            Thread.Sleep(120);
            return GetForegroundWindow() == hwnd;
        }

        static bool PutClipboard(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return true;
                }
                catch
                {
                    Thread.Sleep(40);
                }
            }
            return false;
        }

    }
}
