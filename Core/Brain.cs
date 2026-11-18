using System;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace NullAgentLib
{
    internal static class Brain
    {
        static readonly object Gate = new object();

        internal static void Ask(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            var thread = new Thread(() => Run(text.Trim())) { IsBackground = true, Name = "NullAgentAsk" };
            thread.Start();
        }

        internal static void Greet(string player)
        {
            var thread = new Thread(() => Hello(player)) { IsBackground = true, Name = "NullAgentGreet" };
            thread.Start();
        }

        static void Hello(string player)
        {
            string line = "hello " + player + ", your agent is here to assist";
            string prompt = (Options.Greeting ?? "").Replace("{name}", player);
            lock (Gate)
            {
                string reply = Host.Complete(prompt, "hi im " + player, 48, 0f);
                string clean = Line(reply);
                if (clean != null && clean.Length <= 140
                    && clean.IndexOf("hello", StringComparison.OrdinalIgnoreCase) >= 0
                    && clean.IndexOf(player, StringComparison.OrdinalIgnoreCase) >= 0)
                    line = clean;
                Notify.Say(line);
                NullAgent.Sound(false);
            }
            Notice();
        }

        static void Notice()
        {
            string line = Credit.Notice;
            if (string.IsNullOrEmpty(line))
                return;
            Thread.Sleep(2500);
            Notify.Say(line);
        }

        static void Run(string heard)
        {
            lock (Gate)
            {
                if (!Host.Ready)
                {
                    Notify.Say("still starting");
                    return;
                }
                var messages = new JArray { new JObject { ["role"] = "system", ["content"] = Options.System } };
                messages.Add(new JObject { ["role"] = "user", ["content"] = heard });
                JArray schema = Tools.Schema();
                for (int step = 0; step < Options.MaxSteps; step++)
                {
                    JObject reply = Host.Chat(messages, schema, 220, 0.2f);
                    if (reply == null)
                    {
                        Notify.Say("I couldn't answer");
                        return;
                    }
                    JArray calls = reply["tool_calls"] as JArray;
                    if (calls == null || calls.Count == 0)
                    {
                        string said = Spoken(reply["content"]?.ToString());
                        Notify.Say(string.IsNullOrEmpty(said) ? "I didn't catch that" : said);
                        return;
                    }
                    messages.Add(new JObject
                    {
                        ["role"] = "assistant",
                        ["content"] = reply["content"]?.ToString() ?? "",
                        ["tool_calls"] = calls
                    });
                    for (int i = 0; i < calls.Count; i++)
                    {
                        JToken call = calls[i];
                        string name = call["function"]?["name"]?.ToString();
                        JObject args = Parse(call["function"]?["arguments"]?.ToString());
                        string result = Tools.Call(name, args);
                        messages.Add(new JObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = call["id"]?.ToString(),
                            ["content"] = result ?? "done"
                        });
                    }
                }
                Notify.Say("that took too many steps");
            }
        }

        static JObject Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new JObject();
            try { return JObject.Parse(raw); } catch { return new JObject(); }
        }

        internal static string Spoken(string reply)
        {
            string text = Line(reply);
            if (text == null)
                return null;
            int stops = 0;
            int cut = text.Length;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '.' && c != '!' && c != '?')
                    continue;
                stops++;
                if (stops == 2)
                {
                    cut = i + 1;
                    break;
                }
            }
            if (cut > 240)
                cut = 240;
            text = text.Substring(0, cut).Trim();
            return text.Length == 0 ? null : text;
        }

        static string Line(string reply)
        {
            if (string.IsNullOrWhiteSpace(reply))
                return null;
            string text = Strip(reply).Replace("\r", " ").Replace("\n", " ").Trim().Trim('"');
            return text.Length == 0 ? null : text;
        }

        static string Strip(string text)
        {
            while (true)
            {
                int start = text.IndexOf(" thinking", StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                    return text;
                int end = text.IndexOf("</think>", start, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                    return text.Remove(start);
                text = text.Remove(start, end + 8 - start);
            }
        }
    }
}
