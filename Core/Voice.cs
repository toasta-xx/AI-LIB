using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace NullAgentLib
{
    internal class Voice : MonoBehaviour
    {
        internal const string WhisperUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip";
        internal const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en-q5_1.bin";

        static readonly string[] BuiltIn =
        {
            "chatgpt", "chat gpt", "chatgbt", "mister gpt", "mr gpt", "mrgpt", "jarvis", "null"
        };

        static readonly List<string> custom = new List<string>();
        static Voice live;

        Process server;
        Process listener;
        int port;
        Job job;
        float nextLaunch;
        bool booting;
        string last = "";
        float lastAt = -10f;

        internal static void Add(string name)
        {
            name = (name ?? "").Trim();
            if (Flat(name).Length < 2)
                return;
            for (int i = 0; i < custom.Count; i++)
            {
                if (Flat(custom[i]) == Flat(name))
                    return;
            }
            custom.Add(name);
            Save();
        }

        internal static bool Listening
        {
            get { return live != null && live.listener != null && !live.listener.HasExited; }
        }

        void Awake()
        {
            if (live != null && live != this)
            {
                Destroy(this);
                return;
            }
            live = this;
            Load();
        }

        void Start()
        {
            booting = true;
            ThreadPool.QueueUserWorkItem(delegate { Boot(); });
        }

        void Update()
        {
            if (booting || Time.unscaledTime < nextLaunch)
                return;
            if (server == null || server.HasExited)
            {
                nextLaunch = Time.unscaledTime + 3f;
                booting = true;
                ThreadPool.QueueUserWorkItem(delegate { Boot(); });
                return;
            }
            if (listener != null && !listener.HasExited)
                return;
            nextLaunch = Time.unscaledTime + 3f;
            Spawn();
        }

        void Boot()
        {
            try
            {
                if (!Ready())
                {
                    Notify.Say("voice model failed to start");
                    return;
                }
                NullAgent.Post(Spawn);
            }
            catch (Exception e)
            {
                Debug.LogError("[NullAgent] voice " + e.Message);
            }
            finally
            {
                booting = false;
            }
        }

        bool Ready()
        {
            try { Directory.CreateDirectory(Pack.Root); } catch { return false; }
            Pack.Extract();
            string dir = Path.Combine(Pack.Root, "whisper");
            try { Directory.CreateDirectory(dir); } catch { return false; }
            string exe = Path.Combine(dir, "whisper-server.exe");
            string model = Path.Combine(dir, "ggml-small.en-q5_1.bin");
            if (!File.Exists(exe) && !Net.FetchZip(WhisperUrl, dir, Keep))
            {
                Notify.Say("voice runtime failed to download");
                return false;
            }
            if (!File.Exists(model) || new FileInfo(model).Length < 180000000)
            {
                Notify.Say("downloading the voice model");
                if (!Net.Fetch(ModelUrl, model, 0, (done, total) => Host.Report("voice model", done, total)))
                    return false;
            }
            return File.Exists(exe) && File.Exists(model);
        }

        static bool Keep(string name)
        {
            if (name.Equals("whisper-server.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("whisper.dll", StringComparison.OrdinalIgnoreCase))
                return true;
            if (name.Equals("ggml.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("ggml-base.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("ggml-blas.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("libopenblas.dll", StringComparison.OrdinalIgnoreCase))
                return true;
            return name.StartsWith("ggml-cpu-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        }

        bool Server()
        {
            if (server != null && !server.HasExited && port > 0 && Up())
                return true;
            Stop(server);
            server = null;
            string dir = Path.Combine(Pack.Root, "whisper");
            port = Host.FreePort() + 100;
            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(dir, "whisper-server.exe"),
                WorkingDirectory = dir,
                Arguments = "-m ggml-small.en-q5_1.bin -t 4 -l en -nt -ng -sns -bo 1 -nf --host 127.0.0.1 --port " + port + " --prompt \"play a song. turn fly on. search for something.\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            server = new Process { StartInfo = info, EnableRaisingEvents = true };
            server.ErrorDataReceived += (_, row) =>
            {
                if (!string.IsNullOrEmpty(row.Data) && row.Data.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                    Debug.LogError("[NullAgent] " + row.Data);
            };
            server.OutputDataReceived += (_, __) => { };
            if (!server.Start())
                return false;
            try { server.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            Attach(server);
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();
            DateTime until = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < until)
            {
                if (server.HasExited)
                    return false;
                if (Up())
                    return true;
                Thread.Sleep(200);
            }
            return false;
        }

        bool Up()
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/");
                request.Method = "GET";
                request.Timeout = 1500;
                using ((HttpWebResponse)request.GetResponse())
                    return true;
            }
            catch (WebException e)
            {
                return e.Response != null;
            }
            catch { return false; }
        }

        void Spawn()
        {
            try
            {
                if (listener != null && !listener.HasExited)
                    return;
                if (!Server())
                {
                    Notify.Say("voice failed to start");
                    return;
                }
                string exe = Pack.Find("NullListen.exe");
                if (exe == null)
                {
                    Notify.Say("voice listener is missing");
                    return;
                }
                var info = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = port.ToString(),
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                listener = new Process { StartInfo = info, EnableRaisingEvents = true };
                listener.OutputDataReceived += Heard;
                listener.ErrorDataReceived += (_, row) =>
                {
                    if (!string.IsNullOrEmpty(row.Data))
                        Debug.LogError("[NullAgent] " + row.Data);
                };
                if (!listener.Start())
                {
                    Notify.Say("voice listener failed to start");
                    return;
                }
                Attach(listener);
                listener.BeginOutputReadLine();
                listener.BeginErrorReadLine();
            }
            catch (Exception e)
            {
                Debug.LogError("[NullAgent] speech " + e.Message);
                Notify.Say("voice listener failed to start");
            }
        }

        void Attach(Process process)
        {
            if (job == null)
                job = new Job();
            job.Attach(process);
        }

        void Heard(object sender, DataReceivedEventArgs row)
        {
            if (row == null || string.IsNullOrEmpty(row.Data))
                return;
            if (!row.Data.StartsWith("HEARD ", StringComparison.Ordinal))
                return;
            string text = row.Data.Substring(6).Trim();
            int split = text.IndexOf(' ');
            if (split > 0 && float.TryParse(text.Substring(0, split), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float score))
            {
                if (score < Options.MinConfidence)
                    return;
                text = text.Substring(split + 1).Trim();
            }
            NullAgent.Post(() => Result(text));
        }

        void Result(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            string heard = Normalize(Fix(text));
            if (heard.Length == 0 || (heard == last && Time.unscaledTime - lastAt < 1.5f))
                return;
            last = heard;
            lastAt = Time.unscaledTime;
            string rest;
            if (!Wake(heard, out rest))
                return;
            NullAgent.Sound(true);
            if (rest.Length == 0)
            {
                Notify.Say("I'm here");
                return;
            }
            if (Owns(rest))
            {
                Add(Clean(rest));
                Notify.Say("I'll listen for " + rest.Trim());
                return;
            }
            Brain.Ask(rest);
        }

        internal static bool Owns(string rest)
        {
            return rest.Contains("set my own name") || rest.Contains("set your name")
                || rest.Contains("change your name") || rest.Contains("custom name");
        }

        static string Clean(string heard)
        {
            string[] leads = { "my name is ", "call me ", "the name is ", "name is ", "it is ", "it's " };
            string source = heard;
            for (int i = 0; i < leads.Length; i++)
            {
                if (source.StartsWith(leads[i], StringComparison.Ordinal))
                {
                    source = source.Substring(leads[i].Length).Trim();
                    break;
                }
            }
            string[] parts = source.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Length > 3)
                return source;
            var built = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (built.Length > 0)
                    built.Append(' ');
                built.Append(char.ToUpperInvariant(parts[i][0]));
                if (parts[i].Length > 1)
                    built.Append(parts[i].Substring(1));
            }
            return built.ToString();
        }

        static string Fix(string text)
        {
            string heard = text.ToLowerInvariant();
            heard = Swap(heard, "hayden all", "hey null");
            heard = Swap(heard, "h artist", "hey jarvis");
            heard = Swap(heard, "hey artist", "hey jarvis");
            return heard;
        }

        static string Swap(string text, string from, string to)
        {
            if (!text.Contains(from))
                return text;
            int space = from.IndexOf(' ');
            if (space > 0 && Known(from.Substring(space + 1)))
                return text;
            return text.Replace(from, to);
        }

        static bool Wake(string heard, out string rest)
        {
            rest = null;
            string[] tokens = heard.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return false;
            int start = Filler(tokens[0]) ? 1 : 0;
            if (start >= tokens.Length)
                return false;
            string lead = tokens[start];
            string two = start + 1 < tokens.Length ? lead + " " + tokens[start + 1] : "";
            string hit = null;
            int used = 1;
            if (two.Length > 0 && Known(two))
            {
                hit = two;
                used = 2;
            }
            else if (Known(lead))
            {
                hit = lead;
            }
            if (hit == null)
                return false;
            var text = new StringBuilder();
            for (int i = start + used; i < tokens.Length; i++)
            {
                if (text.Length > 0)
                    text.Append(' ');
                text.Append(tokens[i]);
            }
            rest = Trim(text.ToString());
            return true;
        }

        static bool Known(string word)
        {
            if (string.IsNullOrEmpty(word))
                return false;
            string key = Flat(word);
            if (key.Length < 3)
                return false;
            for (int i = 0; i < BuiltIn.Length; i++)
            {
                if (key == Flat(BuiltIn[i]))
                    return true;
            }
            for (int i = 0; i < custom.Count; i++)
            {
                if (key == Flat(custom[i]))
                    return true;
            }
            return key == Flat(Options.Name);
        }

        static bool Filler(string word)
        {
            return word == "hey" || word == "ok" || word == "okay" || word == "yo" || word == "hi" || word == "please";
        }

        static string Trim(string text)
        {
            string[] fillers = { "hey", "ok", "okay", "yo", "hi", "please" };
            bool again = true;
            while (again)
            {
                again = false;
                for (int i = 0; i < fillers.Length; i++)
                {
                    if (text.Equals(fillers[i], StringComparison.Ordinal))
                        return "";
                    string lead = fillers[i] + " ";
                    if (text.StartsWith(lead, StringComparison.Ordinal))
                    {
                        text = text.Substring(lead.Length).Trim();
                        again = true;
                    }
                }
            }
            return text.Trim();
        }

        static string Flat(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            var built = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = char.ToLowerInvariant(text[i]);
                if (char.IsLetterOrDigit(c))
                    built.Append(c);
            }
            return built.ToString();
        }

        static string Normalize(string text)
        {
            var built = new StringBuilder(text.Length);
            bool space = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = char.ToLowerInvariant(text[i]);
                if (char.IsLetterOrDigit(c))
                {
                    built.Append(c);
                    space = false;
                }
                else if (!space && built.Length > 0)
                {
                    built.Append(' ');
                    space = true;
                }
            }
            return built.ToString().Trim();
        }

        static string Path_
        {
            get { return System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "NullAgent-wake.json"); }
        }

        static void Load()
        {
            try
            {
                if (!File.Exists(Path_))
                    return;
                var list = JsonConvert.DeserializeObject<Names>(File.ReadAllText(Path_));
                if (list == null || list.custom == null)
                    return;
                for (int i = 0; i < list.custom.Count; i++)
                {
                    string name = (list.custom[i] ?? "").Trim();
                    if (name.Length >= 2)
                        custom.Add(name);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[NullAgent] wake " + e.Message);
            }
        }

        static void Save()
        {
            try
            {
                var list = new Names();
                list.custom.AddRange(custom);
                list.build = Marks.Stamp();
                File.WriteAllText(Path_, JsonConvert.SerializeObject(list, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogError("[NullAgent] wake " + e.Message);
            }
        }

        void OnDestroy()
        {
            Stop(listener);
            Stop(server);
            listener = null;
            server = null;
            if (job != null)
            {
                job.Dispose();
                job = null;
            }
        }

        static void Stop(Process process)
        {
            try { if (process != null && !process.HasExited) process.Kill(); } catch { }
        }

        class Names
        {
            public List<string> custom = new List<string>();
            public string build;
        }
    }
}
