using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace NullAgentLib
{
    public sealed class Tool
    {
        public string Name;
        public string Description;
        public string[] Args;
        public Func<JObject, string> Run;
    }

    internal static class Tools
    {
        internal static bool AllowHostControl;

        static readonly List<Tool> registry = new List<Tool>();
        static readonly HashSet<string> blockedSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "file", "javascript", "data", "vbscript", "ms-msdt", "search-ms", "shell", "cmd", "powershell"
        };

        internal static void Add(string name, string description, string[] args, Func<JObject, string> run)
        {
            if (string.IsNullOrWhiteSpace(name) || run == null)
                return;
            Remove(name);
            registry.Add(new Tool { Name = name.Trim(), Description = description ?? "", Args = args ?? new string[0], Run = run });
        }

        internal static bool Remove(string name)
        {
            for (int i = registry.Count - 1; i >= 0; i--)
            {
                if (string.Equals(registry[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    registry.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        internal static string[] Names()
        {
            var names = new string[registry.Count];
            for (int i = 0; i < registry.Count; i++)
                names[i] = registry[i].Name;
            return names;
        }

        internal static JArray Schema()
        {
            var tools = new JArray();
            for (int i = 0; i < registry.Count; i++)
            {
                Tool tool = registry[i];
                var properties = new JObject();
                var required = new JArray();
                for (int a = 0; a < tool.Args.Length; a++)
                {
                    properties[tool.Args[a]] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = tool.Args[a]
                    };
                    required.Add(tool.Args[a]);
                }
                tools.Add(new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = properties,
                            ["required"] = required
                        }
                    }
                });
            }
            return tools;
        }

        internal static Tool Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            for (int i = 0; i < registry.Count; i++)
            {
                if (string.Equals(registry[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return registry[i];
            }
            return null;
        }

        internal static string Call(string name, JObject args)
        {
            Tool tool = Find(name);
            if (tool == null)
                return "unknown tool " + name;
            string refusal = Guard(name, args);
            if (refusal != null)
                return refusal;
            string external = NullAgent.Deny(name, args);
            if (external != null)
                return external;
            try
            {
                string result = tool.Run(args ?? new JObject());
                return string.IsNullOrWhiteSpace(result) ? "done" : Trim(result);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[NullAgent] tool " + name + ": " + e.Message);
                return "failed";
            }
        }

        static string Trim(string text)
        {
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 300 ? text.Substring(0, 300).Trim() : text;
        }

        internal static string Guard(string name, JObject args)
        {
            if (args != null)
            {
                foreach (JProperty property in args.Properties())
                {
                    string value = property.Value?.ToString();
                    if (value == null)
                        continue;
                    if (value.IndexOf('\0') >= 0)
                        return "bad input";
                    if (value.Length > 512)
                        return "input too long";
                }
            }
            if (string.Equals(name, "open_url", StringComparison.OrdinalIgnoreCase))
                return CheckUrl(args?["url"]?.ToString());
            if (string.Equals(name, "search", StringComparison.OrdinalIgnoreCase))
                return CheckUrl("https://duckduckgo.com/?q=" + Uri.EscapeDataString(args?["query"]?.ToString() ?? ""));
            if (string.Equals(name, "run_command", StringComparison.OrdinalIgnoreCase) && !AllowHostControl)
                return "host control is disabled";
            return null;
        }

        static string CheckUrl(string raw)
        {
            if (!Uri.TryCreate((raw ?? "").Trim(), UriKind.Absolute, out Uri uri))
                return "that isn't a web address";
            if (blockedSchemes.Contains(uri.Scheme))
                return "that scheme isn't allowed";
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return "that isn't a web address";
            return null;
        }

        internal static string Url(string raw)
        {
            if (!Uri.TryCreate((raw ?? "").Trim(), UriKind.Absolute, out Uri uri))
                return null;
            if (blockedSchemes.Contains(uri.Scheme))
                return null;
            return uri.AbsoluteUri;
        }

        internal static void Bootstrap()
        {
            Add("enable_mod", "Turn a menu entry on by its name.", new[] { "name" }, a => Menu.Set(a["name"]?.ToString(), true));
            Add("disable_mod", "Turn a menu entry off by its name.", new[] { "name" }, a => Menu.Set(a["name"]?.ToString(), false));
            Add("toggle_mod", "Flip a menu entry on or off by its name.", new[] { "name" }, a => Menu.Set(a["name"]?.ToString(), null));
            Add("list_mods", "List the menu entries this game exposes.", new string[0], a => Menu.List() ?? "nothing found");
            Add("play_song", "Play a song on YouTube Music in the browser.", new[] { "query" }, a => Music.Play(a["query"]?.ToString()));
            Add("media_control", "Control the current song. Use one of: pause, resume, next, prev, mute, unmute, louder, quieter, restart.", new[] { "command" }, a => Music.Control(a["command"]?.ToString()));
            Add("search", "Look up a short factual answer on the web.", new[] { "query" }, a => Web.Answer(a["query"]?.ToString()));
            Add("open_url", "Open a web page in the default browser.", new[] { "url" }, a =>
            {
                string url = Url(a["url"]?.ToString());
                if (url == null)
                    return "that isn't a web address";
                Shell.Launch(url);
                return "opened";
            });
            Add("say", "Speak a line without taking an action.", new[] { "text" }, a => a["text"]?.ToString());
            Add("run_command", "Launch an allowlisted program installed on this machine.", new[] { "program" }, a => Commands.Launch(a["program"]?.ToString()));
        }
    }
}
