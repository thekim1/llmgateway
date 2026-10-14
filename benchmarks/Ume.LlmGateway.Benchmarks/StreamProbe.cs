using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Benchmarks;

/// <summary>
/// What a streaming client experiences, which k6 cannot show (it buffers the whole body): time to the response headers,
/// time to the first token, the gaps between the events that follow, and the whole stream. <c>VUS</c> clients send
/// streamed chat messages back to back for the given time against the fake LLM directly, the Ume gateway or eneo
/// (the same requests as <c>benchmarks/compare/k6/chat.js</c>), and the summary is written as JSON.
/// <code>stream-probe &lt;fake|ume|eneo&gt; &lt;base url&gt; &lt;vus&gt; &lt;seconds&gt; &lt;out.json&gt; [ume key]</code>
/// <c>PROBE_PAYLOAD=document</c> sends a realistic long conversation instead of one question (see <see cref="Document"/>).
/// </summary>
public static class StreamProbe
{
    private const string Question = "Vilka handlingar behöver jag skicka in för att ansöka om bygglov för ett uterum, och hur lång är handläggningstiden?";
    private const string FirstWord = "Hej"; // the fake LLM's answer starts with it: the first token
    private const string AnswerMarker = "testsvar";

    public static async Task<int> RunAsync(string target, string baseUrl, int vus, int seconds, string outPath, string? key)
    {
        var document = Environment.GetEnvironmentVariable("PROBE_PAYLOAD") == "document";
        using var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10), MaxConnectionsPerServer = int.MaxValue })
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(120),
        };
        var (path, body, auth) = await PrepareAsync(http, target, key, document);

        var results = new List<Sample>();
        var gate = new Lock();
        var stop = Stopwatch.GetTimestamp() + (long)(seconds * (double)Stopwatch.Frequency);
        var started = Stopwatch.GetTimestamp();
        await Task.WhenAll(Enumerable.Range(0, vus).Select(_ => Task.Run(async () =>
        {
            while (Stopwatch.GetTimestamp() < stop)
            {
                var sample = await OneAsync(http, path, body, auth);
                lock (gate)
                {
                    results.Add(sample);
                }
            }
        })));
        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;

        var ok = results.Where(r => r.Ok).ToList();
        var gaps = ok.SelectMany(r => r.Gaps).ToList();
        var summary = new JsonObject
        {
            ["target"] = target,
            ["payload"] = document ? "document" : "question",
            ["request_bytes"] = Encoding.UTF8.GetByteCount(body),
            ["vus"] = vus,
            ["seconds"] = seconds,
            ["requests"] = results.Count,
            ["errors"] = results.Count - ok.Count,
            ["rps"] = Math.Round(results.Count / elapsed, 2),
            ["ttfb_ms"] = Stats(ok.Select(r => r.TtfbMs)),
            ["ttft_ms"] = Stats(ok.Select(r => r.TtftMs)),
            ["total_ms"] = Stats(ok.Select(r => r.TotalMs)),
            ["events_per_stream"] = Stats(ok.Select(r => (double)r.Events)),
            ["gap_ms"] = Stats(gaps),
            ["max_gap_per_stream_ms"] = Stats(ok.Select(r => r.Gaps.Count == 0 ? 0 : r.Gaps.Max())),
            ["gaps_over_250ms"] = gaps.Count(g => g >= 250),
            ["gaps_over_1s"] = gaps.Count(g => g >= 1000),
            ["first_error"] = results.FirstOrDefault(r => !r.Ok)?.Error,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath, summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{target} vus={vus}: {results.Count} streams ({summary["errors"]} errors), {summary["rps"]} /s, TTFT p50 {summary["ttft_ms"]!["p50"]} ms, gap p99 {summary["gap_ms"]!["p99"]} ms, total p50 {summary["total_ms"]!["p50"]} ms"));
        return 0;
    }

    private static async Task<(string Path, string Body, AuthenticationHeaderValue? Auth)> PrepareAsync(HttpClient http, string target, string? key,
        bool document)
    {
        switch (target)
        {
            case "fake" or "ume":
                var chat = new JsonObject
                {
                    ["model"] = target == "ume" ? "ume/chat" : "fake-chat",
                    ["messages"] = document ? Document.Messages() : new JsonArray(new JsonObject { ["role"] = "user", ["content"] = Question }),
                    ["max_tokens"] = document ? 1500 : 400,
                    ["stream"] = true,
                };
                return ("v1/chat/completions", chat.ToJsonString(), target == "ume" ? new AuthenticationHeaderValue("Bearer", key) : null);
            case "eneo":
                // As k6/chat.js: one password login, then a new conversation with the personal assistant per message.
                using (var login = await http.PostAsync("users/login/token/", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = "compare@example.com", ["password"] = "ComparePassword123!",
                })))
                {
                    login.EnsureSuccessStatusCode();
                    var token = (await login.Content.ReadFromJsonAsync<JsonObject>())!["access_token"]!.GetValue<string>();
                    var auth = new AuthenticationHeaderValue("Bearer", token);
                    using var request = new HttpRequestMessage(HttpMethod.Get, "spaces/type/personal/") { Headers = { Authorization = auth } };
                    using var space = await http.SendAsync(request);
                    space.EnsureSuccessStatusCode();
                    var assistant = (await space.Content.ReadFromJsonAsync<JsonObject>())!["default_assistant"]!["id"]!.GetValue<string>();
                    // eneo takes one question per call (its history lives in its own database), so the document case sends
                    // the same text as one question: the same tokens reach the model.
                    var question = document ? Document.AsOneQuestion() : Question;
                    return ("conversations/", new JsonObject { ["assistant_id"] = assistant, ["question"] = question, ["stream"] = true }.ToJsonString(), auth);
                }

            default:
                throw new ArgumentException($"Unknown target '{target}'", nameof(target));
        }
    }

    private static async Task<Sample> OneAsync(HttpClient http, string path, string body, AuthenticationHeaderValue? auth)
    {
        var sample = new Sample();
        var start = Stopwatch.GetTimestamp();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = auth;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            sample.TtfbMs = Ms(start);
            if (!response.IsSuccessStatusCode)
            {
                sample.Error = $"HTTP {(int)response.StatusCode}";
                return sample;
            }

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            var text = new StringBuilder();
            var eventText = new StringBuilder();
            long last = 0;
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length > 0)
                {
                    eventText.Append(line);
                    continue;
                }

                if (eventText.Length == 0)
                {
                    continue;
                }

                // One complete SSE event arrived now.
                var now = Stopwatch.GetTimestamp();
                var data = eventText.ToString();
                eventText.Clear();
                text.Append(data);
                if (sample.TtftMs == 0)
                {
                    if (!data.Contains(FirstWord, StringComparison.Ordinal))
                    {
                        continue; // metadata before the answer (eneo's session/question events, the role chunk)
                    }

                    sample.TtftMs = Ms(start, now);
                }
                else
                {
                    sample.Gaps.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
                }

                sample.Events++;
                last = now;
            }

            sample.TotalMs = Ms(start);
            sample.Ok = sample.TtftMs > 0 && text.ToString().Contains(AnswerMarker, StringComparison.Ordinal);
            if (!sample.Ok)
            {
                sample.Error = "answer missing: " + (text.Length > 300 ? text.ToString(0, 300) : text.ToString());
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            sample.Error = ex.GetType().Name + ": " + ex.Message;
        }

        return sample;
    }

    private static double Ms(long start, long? end = null) => Stopwatch.GetElapsedTime(start, end ?? Stopwatch.GetTimestamp()).TotalMilliseconds;

    private static JsonObject Stats(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        double P(double q) => sorted.Length == 0 ? 0 : Math.Round(sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)], 2);
        return new JsonObject
        {
            ["count"] = sorted.Length,
            ["avg"] = sorted.Length == 0 ? 0 : Math.Round(sorted.Average(), 2),
            ["p50"] = P(0.5),
            ["p95"] = P(0.95),
            ["p99"] = P(0.99),
            ["max"] = sorted.Length == 0 ? 0 : Math.Round(sorted[^1], 2),
        };
    }

    /// <summary>
    /// A realistic long request: a system prompt and five messages. The first user message holds a ten-page planning
    /// document (about 5 000 words, 500 per page), then come two answers and two follow-up questions. That is about
    /// 9 000 tokens, like a user asking about an attached document. Deterministic, and free of anything the PII
    /// scanner would flag, so every platform does the same work.
    /// </summary>
    internal static class Document
    {
        public const string System = "Du är en hjälpsam assistent för Umeå kommuns handläggare. Svara på svenska, sakligt och med hänvisning till dokumentet.";
        private const int Pages = 10;
        private const int WordsPerPage = 500;

        private static readonly string[] Turns =
        [
            "Här är detaljplaneunderlaget för kvarteret Björken. Sammanfatta de viktigaste villkoren för bygglov.",
            "Planen anger högst tre våningar, en byggnadsarea om högst 40 procent av fastigheten och krav på dagvattenhantering inom tomten. Parkering ska ordnas enligt kommunens parkeringsnorm och bullervärden vid fasad får inte överskridas.",
            "Vilka av villkoren påverkar ett uterum på 18 kvadratmeter?",
            "Uterummet räknas in i byggnadsarean, så den sammanlagda arean får inte gå över 40 procent. Det behöver placeras minst 4,5 meter från tomtgräns mot gata och får inte försämra dagvattenhanteringen.",
            "Vilka handlingar behöver jag skicka in för att ansöka om bygglov för ett uterum, och hur lång är handläggningstiden?",
        ];

        public static JsonArray Messages()
        {
            var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = System });
            for (var i = 0; i < Turns.Length; i++)
            {
                var content = i == 0 ? Turns[0] + "\n\n" + Text() : Turns[i];
                messages.Add(new JsonObject { ["role"] = i % 2 == 0 ? "user" : "assistant", ["content"] = content });
            }

            return messages;
        }

        public static string AsOneQuestion()
        {
            var sb = new StringBuilder(System).Append("\n\n");
            for (var i = 0; i < Turns.Length - 1; i++)
            {
                sb.Append(i % 2 == 0 ? "Användare: " : "Assistent: ").Append(i == 0 ? Turns[0] + "\n\n" + Text() : Turns[i]).Append("\n\n");
            }

            return sb.Append(Turns[^1]).ToString();
        }

        /// <summary>Ten pages of plan text built from a fixed vocabulary, one numbered section per page.</summary>
        private static string Text()
        {
            string[] words =
            [
                "detaljplanen", "fastigheten", "byggnadsarea", "kvarteret", "bestämmelse", "dagvatten", "gatumark", "prickmark",
                "korsmark", "takvinkel", "nockhöjd", "fasadmaterial", "bullerutredning", "parkeringsnorm", "allmän", "plats",
                "kvartersmark", "markreservat", "ledningar", "utformning", "skyddsbestämmelse", "kulturmiljö", "grönyta",
                "lekplats", "infart", "tomtgräns", "huvudbyggnad", "komplementbyggnad", "uterum", "carport", "ska", "får",
                "inte", "högst", "minst", "meter", "procent", "enligt", "inom", "mot", "och", "eller", "samt", "med", "för",
                "den", "det", "som", "är", "vid", "till", "av", "på", "i",
            ];
            var sb = new StringBuilder();
            var n = 0;
            for (var page = 1; page <= Pages; page++)
            {
                sb.Append("\n\n").Append(page).Append(". Planbestämmelser, avsnitt ").Append(page).Append("\n\n");
                for (var w = 0; w < WordsPerPage; w++)
                {
                    sb.Append(words[(n * 7 + page * 3) % words.Length]);
                    n++;
                    sb.Append(w % 18 == 17 ? ". " : " ");
                }
            }

            return sb.ToString();
        }
    }

    private sealed class Sample
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public double TtfbMs { get; set; }
        public double TtftMs { get; set; }
        public double TotalMs { get; set; }
        public int Events { get; set; }
        public List<double> Gaps { get; } = [];
    }
}
