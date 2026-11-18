using System;
using System.Collections.Generic;

namespace NullAgentLib
{
    internal static class Commands
    {
        static readonly HashSet<string> banned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe",
            "rundll32.exe", "regsvr32.exe", "certutil.exe", "bitsadmin.exe", "msbuild.exe",
            "installutil.exe", "reg.exe", "regedit.exe", "schtasks.exe", "sc.exe", "wmic.exe",
            "curl.exe", "certreq.exe", "forfiles.exe", "at.exe", "net.exe", "net1.exe", "psexec.exe",
            "bash.exe", "wsl.exe", "sh.exe", "python.exe", "pythonw.exe", "node.exe", "java.exe"
        };

        static readonly string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };

        static readonly List<string> allowed = new List<string>
        {
            "spotify.exe", "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe",
            "discord.exe", "steam.exe", "notepad.exe", "explorer.exe", "calc.exe", "mspaint.exe",
            "taskmgr.exe", "obs64.exe", "code.exe", "vlc.exe", "wmplayer.exe"
        };

        internal static void Permit(string exe)
        {
            exe = (exe ?? "").Trim();
            if (exe.Length == 0)
                return;
            for (int i = 0; i < allowed.Count; i++)
            {
                if (string.Equals(allowed[i], exe, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            allowed.Add(exe);
        }

        internal static string Launch(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "say which program";
            string program = raw.Trim().Trim('"');
            if (program.IndexOfAny(new[] { '&', '|', '>', '<', ';', '\n', '\r', '\0' }) >= 0)
                return "refused";
            string name = System.IO.Path.GetFileName(program);
            if (string.IsNullOrEmpty(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return "only executables";
            if (banned.Contains(name))
                return "that program isn't allowed";
            bool listed = false;
            for (int i = 0; i < allowed.Count; i++)
            {
                if (string.Equals(allowed[i], name, StringComparison.OrdinalIgnoreCase))
                    listed = true;
            }
            if (!listed)
                return name + " isn't on the allowlist";
            if (System.IO.Path.IsPathRooted(program))
            {
                if (!System.IO.File.Exists(program))
                    return "that path doesn't exist";
                if (!Inside(program))
                    return "only programs in standard install folders";
            }
            Shell.Launch(program);
            return "launched " + name;
        }

        static bool Inside(string path)
        {
            string full;
            try { full = System.IO.Path.GetFullPath(path); } catch { return false; }
            for (int i = 0; i < roots.Length; i++)
            {
                if (string.IsNullOrEmpty(roots[i]))
                    continue;
                if (full.StartsWith(roots[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
