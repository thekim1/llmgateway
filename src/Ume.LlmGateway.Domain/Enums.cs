namespace Ume.LlmGateway.Domain;

/// <summary>Where a provider processes data. Drives PII routing and key residency restrictions.</summary>
public enum DataResidency
{
    /// <summary>Runs on Umeå kommun's own infrastructure (e.g. Ollama, vLLM).</summary>
    OnPrem = 0,

    /// <summary>External provider contractually processing data inside the EU/EEA (e.g. Azure Sweden Central).</summary>
    Eu = 1,

    /// <summary>External provider that may process data outside the EU/EEA.</summary>
    External = 2,
}

public enum ProviderType
{
    OpenAI = 0,
    AzureOpenAI = 1,
    AzureAIFoundry = 2,
    Anthropic = 3,
    Ollama = 4,
    OllamaCloud = 5,
    OpenAICompatible = 6,
}

public enum ProviderAuthMode
{
    None = 0,

    /// <summary>Authorization: Bearer &lt;credential&gt;.</summary>
    Bearer = 1,

    /// <summary>api-key: &lt;credential&gt; (Azure OpenAI / Azure AI Foundry).</summary>
    ApiKeyHeader = 2,

    /// <summary>x-api-key: &lt;credential&gt; (Anthropic).</summary>
    XApiKeyHeader = 3,
}

[Flags]
public enum ProviderCapabilities
{
    None = 0,
    ChatCompletions = 1,
    Embeddings = 2,
    Responses = 4,
    AnthropicMessages = 8,
    Streaming = 16,
}

public enum GatewayEndpoint
{
    ChatCompletions = 0,
    Embeddings = 1,
    Responses = 2,
    AnthropicMessages = 3,
}

public enum ModelKind
{
    Chat = 0,
    Embedding = 1,
}

/// <summary>
/// How request parameters must be adapted for a model family. Data-driven so new families
/// (e.g. GPT-6) are configuration, not code.
/// </summary>
public enum ParameterProfile
{
    /// <summary>Pass parameters through unchanged.</summary>
    Standard = 0,

    /// <summary>
    /// Reasoning-style OpenAI models (o-series, GPT-5/6 families): use <c>max_completion_tokens</c>
    /// instead of <c>max_tokens</c> and drop sampling parameters they reject.
    /// </summary>
    OpenAIReasoning = 1,
}

public enum PiiPolicy
{
    /// <summary>No PII scanning for this key.</summary>
    Off = 0,

    /// <summary>Scan and record categories, but forward unchanged.</summary>
    Allow = 1,

    /// <summary>Replace detected values with placeholders before forwarding.</summary>
    Redact = 2,

    /// <summary>Reject requests containing PII.</summary>
    Block = 3,

    /// <summary>If PII is found, only on-prem providers may receive the request.</summary>
    RerouteToOnPrem = 4,
}

public enum PiiCategory
{
    Personnummer = 0,
    Samordningsnummer = 1,
    Email = 2,
    Phone = 3,
    Iban = 4,
}

public enum BudgetScope
{
    Department = 0,
    Team = 1,
    VirtualKey = 2,
}

public enum BudgetPeriod
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2,
    Quarterly = 3,
    Yearly = 4,
}

public enum KeyRotationMode
{
    /// <summary>Old key stops working immediately (default).</summary>
    RevokeImmediately = 0,

    /// <summary>Old key keeps working for 24 hours so clients can switch.</summary>
    Grace24Hours = 1,
}

public enum KeyStatus
{
    Active = 0,
    InGracePeriod = 1,
    Expired = 2,
    Revoked = 3,
    Disabled = 4,
}

public enum RequestOutcome
{
    Success = 0,
    ProviderError = 1,
    BudgetExceeded = 2,
    RateLimited = 3,
    PiiBlocked = 4,
    Rejected = 5,
    ClientCancelled = 6,
}
