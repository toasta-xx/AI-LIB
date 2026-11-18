using System;
using System.Text;
using Newtonsoft.Json.Linq;

namespace NullAgentLib
{
    internal static class Music
    {
        static readonly System.Net.Http.HttpClient Web = Build();

        static System.Net.Http.HttpClient Build()
        {
            var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NullAgent/1.0");
            return client;
        }

        internal static string Play(string query)
        {
            query = Clean(query);
            if (query.Length == 0)
                return "say the song name";
            try
            {
                string id = Find(query, out string title);
                if (id != null && Player.Play(id))
                    return "playing " + (string.IsNullOrEmpty(title) ? query : title);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[NullAgent] play " + e.Message);
            }
            return "couldn't start " + query;
        }

        internal static string Control(string command)
        {
            command = (command ?? "").Trim().ToLowerInvariant();
            string[] allowed = { "pause", "resume", "next", "prev", "mute", "unmute", "louder", "quieter", "restart" };
            bool ok = false;
            for (int i = 0; i < allowed.Length; i++)
            {
                if (allowed[i] == command)
                    ok = true;
            }
            if (!ok)
                return "unknown command";
            return Player.Control(command) ? command : "nothing is playing";
        }

        static string Clean(string query)
        {
            query = (query ?? "").Trim();
            string[] leads = { "me ", "the song ", "song ", "the " };
            bool again = true;
            while (again)
            {
                again = false;
                for (int i = 0; i < leads.Length; i++)
                {
                    if (!query.StartsWith(leads[i], StringComparison.Ordinal))
                        continue;
                    query = query.Substring(leads[i].Length).Trim();
                    again = true;
                }
            }
            string[] tails = { " on youtube music", " on youtube", " on spotify", " please" };
            for (int i = 0; i < tails.Length; i++)
            {
                if (query.EndsWith(tails[i], StringComparison.Ordinal))
                    query = query.Substring(0, query.Length - tails[i].Length).Trim();
            }
            return query;
        }

        static string Find(string query, out string title)
        {
            title = null;
            Split(query, out string song, out string artist);
            bool remix = song.Contains("remix");
            bool cover = song.Contains("cover");
            bool live = song.Contains("live");
            string search = string.IsNullOrEmpty(artist) ? song : song + " " + artist;
            if (!remix && !cover && !live && string.IsNullOrEmpty(artist))
                search += " official";
            string id = Pick(Search(search), song, artist, remix, cover, live, out title);
            if (id != null || string.IsNullOrEmpty(artist))
                return id;
            return Pick(Search(artist + " " + song), song, artist, remix, cover, live, out title);
        }

        static void Split(string query, out string song, out string artist)
        {
            int by = query.LastIndexOf(" by ", StringComparison.Ordinal);
            if (by <= 0)
            {
                song = query;
                artist = "";
                return;
            }
            song = query.Substring(0, by).Trim();
            artist = query.Substring(by + 4).Trim();
        }

        static string Search(string query)
        {
            var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query));
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            var response = Web.SendAsync(request).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }

        static string Pick(string body, string song, string artist, bool remix, bool cover, bool live, out string title)
        {
            title = null;
            if (string.IsNullOrEmpty(body))
                return null;
            string[] words = Words(song);
            const string mark = "\"videoRenderer\":";
            string bestId = null;
            string bestTitle = null;
            int best = 0;
            int seen = 0;
            int at = 0;
            while (at < body.Length && seen < 12)
            {
                int found = body.IndexOf(mark, at, StringComparison.Ordinal);
                if (found < 0)
                    break;
                int idAt = body.IndexOf("\"videoId\":\"", found, StringComparison.Ordinal);
                if (idAt < 0 || idAt - found > 500)
                {
                    at = found + mark.Length;
                    continue;
                }
                int start = idAt + 11;
                if (!IsId(body, start))
                {
                    at = start;
                    continue;
                }
                string id = body.Substring(start, 11);
                int end = Math.Min(body.Length, start + 2200);
                string slice = body.Substring(start, end - start);
                string name = Quoted(slice, "\"text\":\"");
                string channel = Channel(slice);
                int score = Score(name, channel, words, artist, remix, cover, live);
                if (score > best)
                {
                    best = score;
                    bestId = id;
                    bestTitle = name;
                }
                seen++;
                at = start + 11;
            }
            if (bestId == null || best < 8)
                return null;
            title = bestTitle;
            return bestId;
        }

        static string Channel(string slice)
        {
            string[] marks =
            {
                "\"ownerText\":{\"runs\":[{\"text\":\"",
                "\"shortBylineText\":{\"runs\":[{\"text\":\"",
                "\"longBylineText\":{\"runs\":[{\"text\":\""
            };
            for (int i = 0; i < marks.Length; i++)
            {
                string name = Quoted(slice, marks[i]);
                if (name.Length > 0)
                    return name;
            }
            return "";
        }

        static string[] Words(string query)
        {
            string[] parts = (query ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var words = new System.Collections.Generic.List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "by" || parts[i] == "the" || parts[i] == "a" || parts[i] == "an" || parts[i] == "of" || parts[i] == "song")
                    continue;
                if (parts[i].Length >= 3)
                    words.Add(parts[i]);
            }
            return words.ToArray();
        }

        static int Score(string title, string channel, string[] words, string artist, bool remix, bool cover, bool live)
        {
            string text = (title ?? "").ToLowerInvariant();
            int hits = 0;
            for (int i = 0; i < words.Length; i++)
            {
                if (text.Contains(words[i]))
                    hits++;
            }
            int artistScore = ArtistScore(channel, title, artist);
            if (hits == 0 && artistScore < 12)
                return 0;
            int score = hits * 4 + artistScore;
            if (remix && text.Contains("remix"))
                score += 4;
            if (!remix && text.Contains("remix"))
                score -= 3;
            if (cover && text.Contains("cover"))
                score += 3;
            if (live && text.Contains("live"))
                score += 3;
            if (text.Contains("official music video") || text.Contains("official audio") || (channel ?? "").ToLowerInvariant().Contains("topic"))
                score += 2;
            string[] junk = { "nightcore", "slowed", "reverb", "brainrot", "8d", "tiktok", "sped up", "amv" };
            for (int i = 0; i < junk.Length; i++)
            {
                if (text.Contains(junk[i]))
                    score -= 6;
            }
            if (!string.IsNullOrEmpty(artist) && artistScore >= 12 && hits >= 1)
                return Math.Max(score, 12);
            if (!string.IsNullOrEmpty(artist) && artistScore < 12 && hits < words.Length)
                return score < 8 ? 0 : score;
            return score;
        }

        static int ArtistScore(string channel, string title, string artist)
        {
            if (string.IsNullOrEmpty(artist))
                return 0;
            if (Match(channel, artist))
                return 20;
            if (Match(title, artist))
                return 8;
            return 0;
        }

        static bool Match(string candidate, string artist)
        {
            string left = Menu.Flat(candidate);
            string right = Menu.Flat(artist);
            if (left.Length < 3 || right.Length < 3)
                return false;
            if (left.Contains(right) || right.Contains(left))
                return true;
            int limit = Math.Max(left.Length, right.Length) >= 8 ? 3 : 1;
            return Distance(left, right) <= limit && Chunk(left, right, 4);
        }

        static bool Chunk(string left, string right, int size)
        {
            if (left.Length < size || right.Length < size)
                return false;
            for (int i = 0; i <= left.Length - size; i++)
            {
                if (right.Contains(left.Substring(i, size)))
                    return true;
            }
            return false;
        }

        static int Distance(string left, string right)
        {
            int n = left.Length;
            int m = right.Length;
            var row = new int[m + 1];
            for (int j = 0; j <= m; j++)
                row[j] = j;
            for (int i = 1; i <= n; i++)
            {
                int prev = row[0];
                row[0] = i;
                for (int j = 1; j <= m; j++)
                {
                    int cur = row[j];
                    int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                    int best = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), prev + cost);
                    row[j] = best;
                    prev = cur;
                }
            }
            return row[m];
        }

        static string Quoted(string slice, string mark)
        {
            int at = slice.IndexOf(mark, StringComparison.Ordinal);
            if (at < 0)
                return "";
            var built = new StringBuilder();
            for (int i = at + mark.Length; i < slice.Length; i++)
            {
                char c = slice[i];
                if (c == '\\' && i + 1 < slice.Length)
                {
                    char next = slice[i + 1];
                    if (next == 'u' && i + 5 < slice.Length)
                    {
                        i += 5;
                        continue;
                    }
                    if (next == '"' || next == '\\' || next == '/')
                        built.Append(next);
                    else if (next == 'n')
                        built.Append(' ');
                    i++;
                    continue;
                }
                if (c == '"')
                    break;
                built.Append(c);
            }
            string text = built.ToString().Trim();
            return text.Length > 80 ? text.Substring(0, 80).Trim() : text;
        }

        static bool IsId(string body, int start)
        {
            if (start + 11 > body.Length)
                return false;
            for (int i = 0; i < 11; i++)
            {
                char c = body[start + i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok)
                    return false;
            }
            if (start + 11 < body.Length)
            {
                char after = body[start + 11];
                if ((after >= 'a' && after <= 'z') || (after >= 'A' && after <= 'Z') || (after >= '0' && after <= '9') || after == '_' || after == '-')
                    return false;
            }
            return true;
        }
    }
}
