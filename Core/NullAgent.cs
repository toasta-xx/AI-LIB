using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace NullAgentLib
{
    internal static class Options
    {
        internal static string Name = "NullAgent";
        internal static string System = "You are a voice assistant inside a game menu. Use a tool when the player asks for an action, then answer in one or two short sentences. If no tool fits, say so plainly. Never claim you did something unless a tool returned success.";
        internal static string Greeting = "You are an assistant inside a game. The player just spawned. Reply with this exact line and nothing else: hello {name}, your agent is here to assist";
        internal static float MinConfidence = 0.2f;
        internal static int MaxSteps = 5;
        internal static bool Spoken = true;
    }

    public static class NullAgent
    {
        static readonly System.Collections.Concurrent.ConcurrentQueue<Action> queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        static Root root;
        static bool refused;
        static bool greeted;
        static float readyAt;
        internal static int MainThread;

        public static string Version { get { return "1.0.0"; } }
        public static string Author { get { return Credit.Author ?? "unknown"; } }
        public static string Notice { get { return Credit.Notice ?? ""; } }
        public static bool Running { get { return Host.Ready; } }
        public static bool VoiceReady { get { return Options.Spoken && Voice.Listening; } }
        public static string Fault { get { return Host.Fault; } }
        public static string Name { get { return Options.Name; } }
        public static string Directory { get { return Pack.Root; } }

        public static bool Attach(MonoBehaviour owner)
        {
            if (refused)
                return false;
            if (root != null)
                return true;
            var host = new GameObject("NullAgent");
            if (owner != null)
                host.transform.SetParent(owner.transform, false);
            else
                UnityEngine.Object.DontDestroyOnLoad(host);
            root = host.AddComponent<Root>();
            Debug.Log("[NullAgent] " + Version + " by " + Credit.Author);
            return true;
        }

        public static void Configure(Action<Config> setup)
        {
            if (setup == null)
                return;
            var config = new Config();
            try { setup(config); }
            catch (Exception e) { Debug.LogError("[NullAgent] config " + e.Message); }
        }

        internal static void Begin()
        {
            if (refused)
                return;
            Tools.Bootstrap();
            Menu.AutoWire();
            Pack.Extract();
            Host.Boot();
        }

        public static void AddTool(string name, string description, string[] args, Func<IDictionary<string, string>, string> run)
        {
            if (run == null)
                return;
            Tools.Add(name, description, args, a =>
            {
                var map = new Dictionary<string, string>();
                if (a != null)
                {
                    foreach (JProperty property in a.Properties())
                        map[property.Name] = property.Value?.ToString();
                }
                return run(map);
            });
        }

        public static bool RemoveTool(string name) { return Tools.Remove(name); }
        public static string[] ToolNames() { return Tools.Names(); }
        public static void Say(string line) { Notify.Say(line); }
        public static void Ask(string text) { Brain.Ask(text); }
        public static void AddWakeWord(string name) { Voice.Add(name); }
        public static void HookNotifications(Action<string> sink) { Notify.Hook(sink); }
        public static string ListMenu() { return Menu.List(); }
        public static string SetMenu(string name, bool? on) { return Menu.Set(name, on); }
        public static void AllowHostControl(bool allow) { Tools.AllowHostControl = allow; }

        public static void UseMenu(Func<IEnumerable<MenuEntry>> provider, Func<string, bool?, string> mapper)
        {
            Menu.Provider = provider;
            if (mapper != null)
                Menu.Mapper = mapper;
            Probe.Clear();
        }

        public static void PermitMenu(Func<string, bool, bool> gate)
        {
            Menu.Permit = gate;
        }

        internal static Func<string, IDictionary<string, string>, string> DenyHook;

        public static void DenyTools(Func<string, IDictionary<string, string>, string> gate)
        {
            DenyHook = gate;
        }

        internal static string Deny(string name, JObject args)
        {
            Func<string, IDictionary<string, string>, string> hook = DenyHook;
            if (hook == null)
                return null;
            var map = new Dictionary<string, string>();
            if (args != null)
            {
                foreach (JProperty property in args.Properties())
                    map[property.Name] = property.Value?.ToString();
            }
            try { return hook(name, map); }
            catch { return "blocked"; }
        }

        public static void Post(Action action)
        {
            if (action != null)
                queue.Enqueue(action);
        }

        internal static bool OnMain
        {
            get { return MainThread != 0 && Thread.CurrentThread.ManagedThreadId == MainThread; }
        }

        internal static T Run<T>(Func<T> work, int timeout)
        {
            if (work == null)
                return default(T);
            if (OnMain)
                return work();
            T result = default(T);
            using (var done = new ManualResetEventSlim(false))
            {
                queue.Enqueue(() =>
                {
                    try { result = work(); }
                    finally { done.Set(); }
                });
                if (!done.Wait(timeout))
                    return default(T);
            }
            return result;
        }

        internal static void Sound(bool listening)
        {
            Sfx.Play(listening);
        }

        internal static void Tick()
        {
            while (queue.TryDequeue(out Action action))
            {
                try { action(); } catch (Exception e) { Debug.LogError("[NullAgent] " + e); }
            }
        }

        internal static void Greet()
        {
            if (greeted || !Host.Ready)
                return;
            if (readyAt <= 0f)
                readyAt = Time.unscaledTime;
            string name = Who();
            if (name == null && Time.unscaledTime - readyAt < 20f)
                return;
            greeted = true;
            Brain.Greet(name ?? "player");
        }

        internal static Func<string> PlayerName;

        static string Who()
        {
            try
            {
                Func<string> custom = PlayerName;
                if (custom != null)
                    return Clean(custom());
                return Clean(PhotonName());
            }
            catch { return null; }
        }

        static string PhotonName()
        {
            try
            {
                Type pun = Type.GetType("Photon.Pun.PhotonNetwork, PhotonUnityNetworking");
                if (pun == null)
                    return null;
                object local = pun.GetProperty("LocalPlayer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null, null);
                if (local == null)
                    return null;
                return local.GetType().GetProperty("NickName")?.GetValue(local, null) as string;
            }
            catch { return null; }
        }

        static string Clean(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            var buffer = new char[Math.Min(raw.Length, 24)];
            int count = 0;
            for (int i = 0; i < raw.Length && count < buffer.Length; i++)
            {
                char c = raw[i];
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-')
                    buffer[count++] = c;
            }
            string text = new string(buffer, 0, count).Trim();
            return text.Length == 0 ? null : text;
        }
    }

    internal class Root : MonoBehaviour
    {
        static Root live;

        void Awake()
        {
            if (live != null && live != this)
            {
                Destroy(this);
                return;
            }
            live = this;
            NullAgent.MainThread = Thread.CurrentThread.ManagedThreadId;
        }

        void Start()
        {
            gameObject.AddComponent<Notify>();
            gameObject.AddComponent<Sfx>();
            if (Options.Spoken)
                gameObject.AddComponent<Voice>();
            NullAgent.Begin();
        }

        void Update()
        {
            if (live != this)
                return;
            NullAgent.Tick();
            NullAgent.Greet();
        }

        void OnApplicationQuit()
        {
            if (live == this)
                Host.Stop();
        }

        void OnDestroy()
        {
            if (live == this)
                live = null;
        }
    }

    public sealed class Config
    {
        public string AgentName { set { if (!string.IsNullOrWhiteSpace(value)) Options.Name = value.Trim(); } }
        public string SystemPrompt { set { if (!string.IsNullOrWhiteSpace(value)) Options.System = value.Trim(); } }
        public string GreetingPrompt { set { if (!string.IsNullOrWhiteSpace(value)) Options.Greeting = value; } }
        public Func<string> PlayerName { set { NullAgent.PlayerName = value; } }
        public float MinConfidence { set { Options.MinConfidence = Mathf.Clamp01(value); } }
        public int MaxToolSteps { set { Options.MaxSteps = Math.Max(1, value); } }
        public bool VoiceInput { set { Options.Spoken = value; } }
        public bool HostControl { set { Tools.AllowHostControl = value; } }
        public Action<string> Notification { set { Notify.Hook(value); } }
        public Func<IEnumerable<MenuEntry>> MenuProvider { set { Menu.Provider = value; } }
        public Func<string, bool, bool> MenuAllow { set { Menu.Permit = value; } }
        public Func<string, IDictionary<string, string>, string> ToolFilter { set { NullAgent.DenyHook = value; } }
    }

    internal class Sfx : MonoBehaviour
    {
        static Sfx live;
        AudioSource source;
        AudioClip boot;
        AudioClip listen;
        bool wantBoot;
        bool wantListen;

        internal static void Play(bool listening)
        {
            if (live == null)
                return;
            if (!NullAgent.OnMain)
            {
                NullAgent.Post(() => Play(listening));
                return;
            }
            if (live.source == null)
                return;
            if (listening)
            {
                if (live.listen == null)
                {
                    live.wantListen = true;
                    return;
                }
                live.source.PlayOneShot(live.listen);
                return;
            }
            if (live.boot == null)
            {
                live.wantBoot = true;
                return;
            }
            live.source.PlayOneShot(live.boot);
        }

        void Awake()
        {
            if (live != null && live != this)
            {
                Destroy(this);
                return;
            }
            live = this;
            if (NullAgent.MainThread == 0)
                NullAgent.MainThread = Thread.CurrentThread.ManagedThreadId;
            source = gameObject.AddComponent<AudioSource>();
            source.spatialBlend = 0f;
            source.playOnAwake = false;
            source.loop = false;
        }

        void Start()
        {
            string bootPath = Pack.Find("boot.mp3");
            string listenPath = Pack.Find("listening.mp3");
            if (bootPath != null)
            {
                StartCoroutine(Load(bootPath, clip =>
                {
                    boot = clip;
                    if (!wantBoot || live != this)
                        return;
                    wantBoot = false;
                    source.PlayOneShot(clip);
                }));
            }
            if (listenPath != null)
            {
                StartCoroutine(Load(listenPath, clip =>
                {
                    listen = clip;
                    if (!wantListen || live != this)
                        return;
                    wantListen = false;
                    source.PlayOneShot(clip);
                }));
            }
        }

        System.Collections.IEnumerator Load(string path, Action<AudioClip> done)
        {
            using (var request = UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip("file:///" + path.Replace("\\", "/"), AudioType.MPEG))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                    yield break;
                AudioClip clip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(request);
                if (clip != null)
                    done(clip);
            }
        }

        void OnDestroy()
        {
            if (live == this)
                live = null;
        }
    }
}
