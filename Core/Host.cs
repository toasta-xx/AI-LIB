using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace NullAgentLib
{
    internal static class Host
    {
        internal const int Context = 4096;
        internal const int Threads = 4;
        internal const long ModelSize = 2019377920L;
        internal const string ModelUrl = "https://huggingface.co/mradermacher/Llama-3.2-3B-Instruct-heretic-ablitered-uncensored-GGUF/resolve/main/Llama-3.2-3B-Instruct-heretic-ablitered-uncensored.Q4_K_M.gguf";
        internal const string RuntimeUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b11716/llama-b11716-bin-win-vulkan-x64.zip";

        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        static readonly object Request = new object();
        static readonly CancellationTokenSource Life = new CancellationTokenSource();

        static Process process;
        static Job job;
        static StreamWriter log;
        static string baseUrl;
        static int booted;
        static int stopped;

        internal static volatile bool Ready;
        internal static volatile string Fault;

        internal static void Boot()
        {
            if (Interlocked.Exchange(ref booted, 1) == 1)
                return;
            var thread = new Thread(Run) { IsBackground = true, Name = "NullAgentHost" };
            thread.Start();
        }

        internal static void Stop()
        {
            if (Interlocked.Exchange(ref stopped, 1) == 1)
                return;
            try { Life.Cancel(); } catch { }
            Kill();
            if (job != null) { job.Dispose(); job = null; }
            try { if (log != null) { log.Dispose(); log = null; } } catch { }
            Ready = false;
        }

        static void Kill()
        {
            try { if (process != null && !process.HasExited) process.Kill(); } catch { }
            try { if (log != null) { log.Dispose(); log = null; } } catch { }
            process = null;
        }

        static void Run()
        {
            try
            {
                Pack.Extract();
                string root = Pack.Root;
                string exe = Pack.Find("llama-server.exe");
                if (exe == null || FindModel(root) == null)
                {
                    Notify.Say("preparing the agent files");
                    if (!EnsureFiles())
                    {
                        Fault = "agent files could not be prepared";
                        return;
                    }
                    exe = Pack.Find("llama-server.exe");
                }
                string model = FindModel(root);
                if (exe == null || model == null)
                {
                    Fault = "agent runtime is missing";
                    return;
                }
                int port = FreePort();
                baseUrl = "http://127.0.0.1:" + port;
                bool up = Launch(exe, root, model, port, true) && Await();
                if (!up && stopped == 0)
                {
                    Kill();
                    up = Launch(exe, root, model, port, false) && Await();
                }
                if (!up)
                {
                    Fault = "agent failed to start";
                    return;
                }
                try { Complete(null, "hi", 1, 0f); } catch { }
                Ready = true;
            }
            catch (Exception e)
            {
                Fault = "agent failed to start";
                UnityEngine.Debug.LogError("[NullAgent] host " + e);
            }
        }

        internal static bool EnsureFiles()
        {
            try { Directory.CreateDirectory(Pack.Root); } catch { return false; }
            Pack.Extract();
            string exe = Pack.Find("llama-server.exe");
            string model = Path.Combine(Pack.Root, "Null.gguf");
            if (exe == null)
            {
                Notify.Say("downloading the chat runtime");
                if (!Net.FetchZip(RuntimeUrl, Pack.Root, Keep))
                    return false;
            }
            if (!File.Exists(model) || new FileInfo(model).Length != ModelSize)
            {
                if (!Net.Fetch(ModelUrl, model, ModelSize, (done, total) => Report("chat model", done, total)))
                    return false;
            }
            return true;
        }

        static string shown;

        internal static void Report(string what, long done, long total)
        {
            if (total <= 0)
                return;
            int percent = (int)(done * 100L / total);
            string line = "downloading the " + what + " " + percent + "%";
            if (line == shown)
                return;
            shown = line;
            Notify.Say(line);
        }

        static bool Keep(string name)
        {
            if (name.Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase))
                return true;
            if (name.Equals("libomp.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("mtmd.dll", StringComparison.OrdinalIgnoreCase))
                return true;
            if (name.StartsWith("ggml", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return true;
            return name.StartsWith("llama", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        }

        static string FindModel(string root)
        {
            string named = Path.Combine(root, "Null.gguf");
            if (File.Exists(named))
                return named;
            try
            {
                string[] files = Directory.GetFiles(root, "*.gguf", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    return files[0];
                }
            }
            catch { }
            return null;
        }

        static bool Launch(string exe, string root, string model, int port, bool gpu)
        {
            string layers = gpu ? "-ngl auto --fit on -fitt 1536" : "-ngl 0 --fit off";
            var info = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-m \"" + model + "\" -c " + Context + " -t " + Threads + " " + layers + " --host 127.0.0.1 --port " + port,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                log = new StreamWriter(Path.Combine(root, "agent.log"), false) { AutoFlush = true };
                lock (log)
                {
                    log.WriteLine("agent build " + Marks.Stamp());
                    log.WriteLine("runtime " + Environment.Version);
                }
                Marks.Seal(Path.Combine(root, "agent.log"));
            }
            catch { }
            process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, row) => Write(row.Data);
            process.ErrorDataReceived += (_, row) => Write(row.Data);
            if (!process.Start())
                return false;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            if (job == null)
                job = new Job();
            job.Attach(process);
            return true;
        }

        static void Write(string line)
        {
            if (string.IsNullOrEmpty(line) || log == null)
                return;
            try { lock (log) log.WriteLine(line); } catch { }
        }

        static bool Await()
        {
            DateTime until = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < until && stopped == 0)
            {
                if (process == null || process.HasExited)
                    return false;
                try
                {
                    using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(wait.Token, Life.Token))
                    using (var response = Http.GetAsync(baseUrl + "/health", linked.Token).GetAwaiter().GetResult())
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            if (body.IndexOf("ok", StringComparison.OrdinalIgnoreCase) >= 0)
                                return true;
                        }
                    }
                }
                catch { }
                Thread.Sleep(250);
            }
            return false;
        }

        internal static string Complete(string system, string user, int max, float temp)
        {
            var messages = new JArray();
            if (!string.IsNullOrEmpty(system))
                messages.Add(new JObject { ["role"] = "system", ["content"] = system });
            if (!string.IsNullOrEmpty(user))
                messages.Add(new JObject { ["role"] = "user", ["content"] = user });
            return Send(messages, max, temp);
        }

        internal static string Send(JArray messages, int max, float temp)
        {
            JObject message = Chat(messages, null, max, temp);
            return message?["content"]?.ToString();
        }

        internal static JObject Chat(JArray messages, JArray tools, int max, float temp)
        {
            if (stopped == 1 || string.IsNullOrEmpty(baseUrl))
                return null;
            var payload = new JObject
            {
                ["messages"] = messages,
                ["max_tokens"] = max,
                ["temperature"] = temp,
                ["stream"] = false
            };
            if (tools != null && tools.Count > 0)
            {
                payload["tools"] = tools;
                payload["tool_choice"] = "auto";
            }
            var content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            try
            {
                using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(wait.Token, Life.Token))
                using (var response = Http.PostAsync(baseUrl + "/v1/chat/completions", content, linked.Token).GetAwaiter().GetResult())
                {
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                        return null;
                    return JObject.Parse(body)["choices"]?[0]?["message"] as JObject;
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[NullAgent] chat " + e.Message);
                return null;
            }
        }

        internal static int FreePort()
        {
            for (int port = 28160; port < 28190; port++)
            {
                try
                {
                    var probe = new TcpListener(IPAddress.Loopback, port);
                    probe.Start();
                    probe.Stop();
                    return port;
                }
                catch { }
            }
            return 28160;
        }
    }
}
