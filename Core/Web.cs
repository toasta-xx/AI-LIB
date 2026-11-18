using System;
using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace NullAgentLib
{
    internal static class Web
    {
        static readonly HttpClient Client = Build();

        static HttpClient Build()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NullAgent/1.0");
            return client;
        }

        internal static string Answer(string query)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0)
                return "say what to search";
            try
            {
                string url = "https://api.duckduckgo.com/?format=json&no_html=1&skip_disambig=1&q=" + Uri.EscapeDataString(query);
                JObject root = JObject.Parse(Client.GetStringAsync(url).GetAwaiter().GetResult());
                string answer = Text(root["Answer"]) ?? Text(root["AbstractText"]);
                if (string.IsNullOrWhiteSpace(answer) && root["RelatedTopics"] is JArray topics)
                {
                    foreach (JToken topic in topics)
                    {
                        answer = Text(topic["Text"]);
                        if (!string.IsNullOrWhiteSpace(answer))
                            break;
                    }
                }
                if (string.IsNullOrWhiteSpace(answer))
                    answer = Wiki(query);
                if (string.IsNullOrWhiteSpace(answer))
                    return "no short answer for " + query;
                return Trim(answer);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[NullAgent] search " + e.Message);
                return "search failed";
            }
        }

        static string Wiki(string query)
        {
            try
            {
                string url = "https://en.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(query.Replace(' ', '_'));
                return JObject.Parse(Client.GetStringAsync(url).GetAwaiter().GetResult())["extract"]?.ToString();
            }
            catch { return null; }
        }

        static string Text(JToken token)
        {
            string value = token?.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        static string Trim(string text)
        {
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 180 ? text.Substring(0, 180).Trim() : text;
        }
    }
}
