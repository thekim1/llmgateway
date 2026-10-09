using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Benchmarks;

/// <summary>Representative request and response bodies. Sizes are chosen to match typical municipal workloads.</summary>
public static class Payloads
{
    /// <summary>A short chat request (~0.5 KB): system prompt plus one user question.</summary>
    public static string ChatRequest(string model, bool stream = false) => new JsonObject
    {
        ["model"] = model,
        ["messages"] = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = "Du är en hjälpsam assistent för Umeå kommun. Svara kort och sakligt på svenska." },
            new JsonObject { ["role"] = "user", ["content"] = "Vilka handlingar behöver jag skicka in för att ansöka om bygglov för ett uterum, och hur lång är handläggningstiden?" }),
        ["max_tokens"] = 400,
        ["temperature"] = 0.2,
        ["stream"] = stream,
    }.ToJsonString();

    /// <summary>A large RAG-style chat request (~64 KB) with retrieved document chunks and conversation history.</summary>
    public static string LargeChatRequest(string model, bool stream = false)
    {
        var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = "Besvara frågor utifrån dokumenten nedan. Hänvisa till källan." });
        var paragraph = string.Concat(Enumerable.Repeat("Kommunfullmäktige beslutade att anta detaljplanen för området med de ändringar som framgår av samrådsredogörelsen. ", 8));
        for (var i = 0; messages.ToJsonString().Length < 64 * 1024; i++)
        {
            messages.Add(new JsonObject { ["role"] = i % 2 == 0 ? "user" : "assistant", ["content"] = $"Dokument {i}: {paragraph}" });
        }

        return new JsonObject { ["model"] = model, ["messages"] = messages, ["max_tokens"] = 1000, ["stream"] = stream }.ToJsonString();
    }

    public static string EmbeddingsRequest(string model, int inputs = 16) => new JsonObject
    {
        ["model"] = model,
        ["input"] = new JsonArray([.. Enumerable.Range(0, inputs).Select(i => (JsonNode?)$"Stycke {i} ur kommunens riktlinjer för ekonomiskt bistånd, uppdaterade efter beslut i socialnämnden.")]),
    }.ToJsonString();

    public static readonly byte[] ChatResponse = Encoding.UTF8.GetBytes("""
        {"id":"chatcmpl-bench","object":"chat.completion","created":1760000000,"model":"bench-chat","system_fingerprint":"fp_bench","choices":[{"index":0,"message":{"role":"assistant","content":"För bygglov för ett uterum behöver du skicka in en ansökan med situationsplan, fasadritningar, planritning och sektionsritning. Handläggningstiden är normalt upp till tio veckor från det att ansökan är komplett.","refusal":null},"logprobs":null,"finish_reason":"stop"}],"usage":{"prompt_tokens":96,"completion_tokens":58,"total_tokens":154,"prompt_tokens_details":{"cached_tokens":0,"audio_tokens":0},"completion_tokens_details":{"reasoning_tokens":0,"audio_tokens":0,"accepted_prediction_tokens":0,"rejected_prediction_tokens":0}}}
        """);

    /// <summary>OpenAI embeddings response: <paramref name="inputs"/> vectors of <paramref name="dimensions"/> floats (~300 KB for 16 × 1536).</summary>
    public static byte[] EmbeddingsResponse(int inputs = 16, int dimensions = 1536)
    {
        var random = new Random(42);
        var sb = new StringBuilder("{\"object\":\"list\",\"data\":[");
        for (var i = 0; i < inputs; i++)
        {
            sb.Append(i == 0 ? "" : ",").Append("{\"object\":\"embedding\",\"index\":").Append(i).Append(",\"embedding\":[");
            for (var d = 0; d < dimensions; d++)
            {
                sb.Append(d == 0 ? "" : ",").Append(((random.NextDouble() * 2) - 1).ToString("0.000000000", CultureInfo.InvariantCulture));
            }

            sb.Append("]}");
        }

        sb.Append("],\"model\":\"bench-embed\",\"usage\":{\"prompt_tokens\":512,\"total_tokens\":512}}");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>An OpenAI chat completions stream: <paramref name="chunks"/> content deltas, a usage chunk and [DONE].</summary>
    public static byte[] OpenAIStream(int chunks = 300)
    {
        var sb = new StringBuilder();
        sb.Append("data: {\"id\":\"chatcmpl-bench\",\"object\":\"chat.completion.chunk\",\"created\":1760000000,\"model\":\"bench-chat\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"logprobs\":null,\"finish_reason\":null}]}\n\n");
        string[] words = ["För ", "bygglov ", "behöver ", "du ", "en ", "situationsplan", ", ", "fasad", "ritningar ", "och ", "planritning", ". "];
        for (var i = 0; i < chunks; i++)
        {
            sb.Append("data: {\"id\":\"chatcmpl-bench\",\"object\":\"chat.completion.chunk\",\"created\":1760000000,\"model\":\"bench-chat\",\"system_fingerprint\":\"fp_bench\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"")
                .Append(words[i % words.Length]).Append("\"},\"logprobs\":null,\"finish_reason\":null}]}\n\n");
        }

        sb.Append("data: {\"id\":\"chatcmpl-bench\",\"object\":\"chat.completion.chunk\",\"created\":1760000000,\"model\":\"bench-chat\",\"choices\":[{\"index\":0,\"delta\":{},\"logprobs\":null,\"finish_reason\":\"stop\"}]}\n\n");
        sb.Append("data: {\"id\":\"chatcmpl-bench\",\"object\":\"chat.completion.chunk\",\"created\":1760000000,\"model\":\"bench-chat\",\"choices\":[],\"usage\":{\"prompt_tokens\":96,\"completion_tokens\":").Append(chunks).Append(",\"total_tokens\":").Append(96 + chunks).Append("}}\n\n");
        sb.Append("data: [DONE]\n\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>An Anthropic Messages stream (passthrough on /v1/messages).</summary>
    public static byte[] AnthropicStream(int chunks = 300)
    {
        var sb = new StringBuilder();
        sb.Append("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_bench\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-bench\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":96,\"output_tokens\":1}}}\n\n");
        sb.Append("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
        for (var i = 0; i < chunks; i++)
        {
            sb.Append("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ord \"}}\n\n");
        }

        sb.Append("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
        sb.Append("event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":").Append(chunks).Append("}}\n\n");
        sb.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
