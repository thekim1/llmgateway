namespace Ume.LlmGateway.FakeLlm;

/// <summary>
/// Optional timing that makes the fake LLM behave like a real provider: a wait before the first token, a pace per
/// token, jitter, longer answers and the odd mid-stream stall. Everything is 0 (off) by default, so tests and the
/// instant-answer benchmarks are unaffected. Configured in the <c>FakeLlm:Latency</c> section, e.g. the environment
/// variables <c>FakeLlm__Latency__FirstTokenMs=400</c> and <c>FakeLlm__Latency__TokenIntervalMs=25</c>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394", Justification = "Timing jitter for a test double, not security.")]
public sealed class FakeLatency
{
    public const string SectionName = "FakeLlm:Latency";

    /// <summary>Time to first token: wait after the request before the first content event (or the whole answer).</summary>
    public int FirstTokenMs { get; set; }

    /// <summary>
    /// Extra time to first token per 1 000 input tokens, like a real model reading a long prompt (prefill). 60 ms per
    /// 1 000 makes a 9 000-token conversation wait about half a second longer than a one-line question.
    /// </summary>
    public int PrefillMsPer1kInputTokens { get; set; }

    /// <summary>Wait between content events (one word each). 25 ms is about 40 words per second.</summary>
    public int TokenIntervalMs { get; set; }

    /// <summary>Random ± variation applied to every wait, in percent of it (0–100).</summary>
    public int JitterPercent { get; set; }

    /// <summary>Pad answers to at least this many words (0 = the short echo answer), so streams are as long as real ones.</summary>
    public int AnswerWords { get; set; }

    /// <summary>Chance per content event (0–1) of an extra pause, like a provider's content filter releasing a batch.</summary>
    public double StallProbability { get; set; }

    /// <summary>Length of such a pause.</summary>
    public int StallMs { get; set; }

    public bool Enabled => FirstTokenMs > 0 || PrefillMsPer1kInputTokens > 0 || TokenIntervalMs > 0 || StallProbability > 0;

    /// <summary>Before the first content event: the base wait plus the prefill time for <paramref name="inputTokens"/>.</summary>
    public Task FirstTokenAsync(long inputTokens, CancellationToken ct) => DelayAsync(FirstTokenWaitMs(inputTokens), ct);

    /// <summary>Before each content event after the first: the token pace plus, now and then, a stall.</summary>
    public Task NextTokenAsync(CancellationToken ct)
    {
        var ms = Vary(TokenIntervalMs);
        if (StallProbability > 0 && Random.Shared.NextDouble() < StallProbability)
        {
            ms += StallMs;
        }

        return ms > 0 ? Task.Delay(ms, ct) : Task.CompletedTask;
    }

    /// <summary>Non-streamed answers: the time a real provider would take to generate <paramref name="words"/> words.</summary>
    public Task WholeAnswerAsync(int words, long inputTokens, CancellationToken ct) =>
        DelayAsync(FirstTokenWaitMs(inputTokens) + (Math.Max(0, words - 1) * TokenIntervalMs), ct);

    private int FirstTokenWaitMs(long inputTokens) =>
        FirstTokenMs + (int)Math.Min(int.MaxValue / 2, inputTokens * PrefillMsPer1kInputTokens / 1000);

    /// <summary>The answer padded to <see cref="AnswerWords"/> words with filler text.</summary>
    public string Pad(string answer)
    {
        var have = answer.Count(c => c == ' ') + 1;
        if (AnswerWords <= have)
        {
            return answer;
        }

        var filler = new System.Text.StringBuilder(answer);
        string[] words = ["Handläggningstiden", "är", "normalt", "upp", "till", "tio", "veckor", "från", "komplett", "ansökan."];
        for (var i = have; i < AnswerWords; i++)
        {
            filler.Append(' ').Append(words[i % words.Length]);
        }

        return filler.ToString();
    }

    private Task DelayAsync(int ms, CancellationToken ct)
    {
        ms = Vary(ms);
        return ms > 0 ? Task.Delay(ms, ct) : Task.CompletedTask;
    }

    private int Vary(int ms)
    {
        if (ms <= 0 || JitterPercent <= 0)
        {
            return ms;
        }

        var spread = ms * Math.Clamp(JitterPercent, 0, 100) / 100.0;
        return (int)Math.Max(0, ms + (((Random.Shared.NextDouble() * 2) - 1) * spread));
    }
}
