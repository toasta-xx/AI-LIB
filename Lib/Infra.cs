using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;

namespace NullAgentLib
{
    internal static class Pack
    {
        internal const string Prefix = "NullAgentLib.bundle.";

        internal static string Root
        {
            get { return Path.Combine(Paths.BepInExRootPath, "NullAgentLib"); }
        }

        internal static void Extract()
        {
            try { Directory.CreateDirectory(Root); } catch { return; }
            Assembly asm = typeof(Pack).Assembly;
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith(Prefix, StringComparison.Ordinal))
                    continue;
                string file = name.Substring(Prefix.Length);
                if (file.Length == 0 || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    continue;
                try
                {
                    using (Stream src = asm.GetManifestResourceStream(name))
                    {
                        if (src == null)
                            continue;
                        string dest = Path.Combine(Root, file);
                        if (File.Exists(dest) && new FileInfo(dest).Length == src.Length)
                            continue;
                        using (var target = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1048576))
                            src.CopyTo(target, 1048576);
                    }
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogError("[NullAgent] unpack " + file + ": " + e.Message);
                }
            }
        }

        internal static string Find(string file)
        {
            string path = Path.Combine(Root, file);
            return File.Exists(path) ? path : null;
        }
    }

    internal static class Net
    {
        internal static bool Fetch(string url, string dest, long size)
        {
            return Fetch(url, dest, size, null);
        }

        internal static bool Fetch(string url, string dest, long size, Action<long, long> progress)
        {
            if (size > 0 && File.Exists(dest) && new FileInfo(dest).Length == size)
                return true;
            string part = dest + ".part";
            long have = 0;
            try { if (File.Exists(part)) have = new FileInfo(part).Length; } catch { have = 0; }
            if (size > 0 && have == size)
                return Finish(part, dest);
            if (size > 0 && have > size)
            {
                try { File.Delete(part); } catch { }
                have = 0;
            }
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(120) })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("NullAgent");
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (have > 0)
                            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                        using (var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                        {
                            if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                            {
                                have = 0;
                                try { File.Delete(part); } catch { }
                            }
                            response.EnsureSuccessStatusCode();
                            long total = size > 0 ? size : (response.Content.Headers.ContentLength ?? 0) + have;
                            using (Stream src = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                            using (var target = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1048576))
                            {
                                byte[] buffer = new byte[1048576];
                                long done = have;
                                long mark = 0;
                                while (true)
                                {
                                    int read = src.Read(buffer, 0, buffer.Length);
                                    if (read <= 0)
                                        break;
                                    target.Write(buffer, 0, read);
                                    done += read;
                                    if (progress != null && done - mark >= 8388608)
                                    {
                                        mark = done;
                                        try { progress(done, total); } catch { }
                                    }
                                }
                            }
                        }
                    }
                }
                if (!File.Exists(part))
                    return false;
                if (size > 0 && new FileInfo(part).Length != size)
                {
                    UnityEngine.Debug.LogError("[NullAgent] size mismatch, will retry: " + new FileInfo(part).Length + "/" + size);
                    return false;
                }
                if (progress != null)
                {
                    long final = new FileInfo(part).Length;
                    try { progress(final, final); } catch { }
                }
                return Finish(part, dest);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[NullAgent] fetch failed: " + e.Message);
                return false;
            }
        }

        static bool Finish(string part, string dest)
        {
            try
            {
                if (File.Exists(dest))
                    File.Delete(dest);
                File.Move(part, dest);
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[NullAgent] move failed: " + e.Message);
                return false;
            }
        }

        internal static bool FetchZip(string url, string dir, Func<string, bool> keep)
        {
            try { Directory.CreateDirectory(dir); } catch { return false; }
            string zip = Path.Combine(dir, "download.zip");
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("NullAgent");
                    using (var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                    {
                        response.EnsureSuccessStatusCode();
                        using (Stream src = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                        using (var target = File.Create(zip))
                            src.CopyTo(target, 1048576);
                    }
                }
                using (Stream stream = File.OpenRead(zip))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.Length == 0)
                            continue;
                        string name = Path.GetFileName(entry.FullName);
                        if (string.IsNullOrEmpty(name) || !keep(name))
                            continue;
                        using (Stream src = entry.Open())
                        using (var target = File.Create(Path.Combine(dir, name)))
                            src.CopyTo(target, 1048576);
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[NullAgent] fetch zip failed: " + e.Message);
                return false;
            }
            finally
            {
                try { if (File.Exists(zip)) File.Delete(zip); } catch { }
            }
        }
    }

    internal sealed class Job : IDisposable
    {
        IntPtr handle;

        public bool Attach(Process process)
        {
            try
            {
                if (process == null)
                    return false;
                if (handle == IntPtr.Zero)
                    handle = CreateKillOnClose();
                if (handle == IntPtr.Zero)
                    return false;
                return AssignProcessToJobObject(handle, process.Handle);
            }
            catch { return false; }
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero)
                return;
            CloseHandle(handle);
            handle = IntPtr.Zero;
        }

        static IntPtr CreateKillOnClose()
        {
            IntPtr created = CreateJobObject(IntPtr.Zero, null);
            if (created == IntPtr.Zero)
                return IntPtr.Zero;
            var info = new Extended();
            info.Basic.LimitFlags = 0x2000;
            int size = Marshal.SizeOf(typeof(Extended));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(created, 9, ptr, (uint)size))
                {
                    CloseHandle(created);
                    return IntPtr.Zero;
                }
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return created;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Basic
        {
            public long A, B;
            public uint LimitFlags;
            public UIntPtr C, D;
            public uint E, F, G;
            public uint H;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Io
        {
            public ulong A, B, C, D, E, F, G;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Extended
        {
            public Basic Basic;
            public Io Io;
            public UIntPtr A, B, C, D;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr processHandle);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);
    }

    internal static class Shell
    {
        internal static void Launch(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[NullAgent] launch: " + e.Message);
            }
        }

        internal static string Run(string exe, string args, int timeout)
        {
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var process = new Process { StartInfo = info })
                {
                    process.Start();
                    process.ErrorDataReceived += (_, row) =>
                    {
                        if (!string.IsNullOrEmpty(row.Data))
                            UnityEngine.Debug.LogError("[NullAgent] " + row.Data);
                    };
                    process.BeginErrorReadLine();
                    string output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(timeout))
                    {
                        try { process.Kill(); } catch { }
                        return "FAIL";
                    }
                    string[] lines = output.Replace("\r", "").Split('\n');
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        if (lines[i].Length > 0)
                            return lines[i].Trim();
                    }
                    return "FAIL";
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[NullAgent] run: " + e.Message);
                return "FAIL";
            }
        }
    }

    internal static class Player
    {
        static readonly object Gate = new object();
        static readonly System.Collections.Generic.List<string> Trail = new System.Collections.Generic.List<string>();
        static int spot = -1;

        internal static bool Play(string videoId)
        {
            if (string.IsNullOrEmpty(videoId))
                return false;
            string url = "https://music.youtube.com/watch?v=" + videoId + "&list=RDAMVM" + videoId;
            string result = Send("play \"" + url.Replace("\"", "") + "\"");
            if (result != "OPENED" && result != "NAV")
                return false;
            lock (Gate)
            {
                if (spot >= 0 && spot < Trail.Count - 1)
                    Trail.RemoveRange(spot + 1, Trail.Count - (spot + 1));
                if (Trail.Count == 0 || Trail[Trail.Count - 1] != videoId)
                    Trail.Add(videoId);
                while (Trail.Count > 15)
                    Trail.RemoveAt(0);
                spot = Trail.Count - 1;
            }
            return true;
        }

        internal static bool Control(string command)
        {
            if (command == "prev" && spot > 0)
            {
                spot--;
                return Play(Trail[spot]);
            }
            if (command == "next" && spot >= 0 && spot < Trail.Count - 1)
            {
                spot++;
                return Play(Trail[spot]);
            }
            if (string.IsNullOrEmpty(Pack.Find("NullPlay.exe")))
                return false;
            return Send(command) == "OK";
        }

        static string Send(string args)
        {
            lock (Gate)
            {
                string exe = Pack.Find("NullPlay.exe");
                if (exe == null)
                    return "FAIL";
                return Shell.Run(exe, args, 15000);
            }
        }
    }
}
