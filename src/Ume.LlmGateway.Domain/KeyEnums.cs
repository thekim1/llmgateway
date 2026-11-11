namespace Ume.LlmGateway.Domain;

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

/// <summary>A key's part in an <see cref="Entities.Integration"/> (docs/integrations.md).</summary>
public enum IntegrationRole
{
    /// <summary>An ordinary key, not part of an integration.</summary>
    None = 0,

    /// <summary>What a platform service authenticates with; a gateway admin creates it in the integration's owner team.</summary>
    Credential = 1,

    /// <summary>
    /// A key made by its team's managers for one of the platform's objects (a chatbot, a knowledge base). It cannot
    /// authenticate by itself: the platform uses it by reference, through a credential key of the same integration.
    /// </summary>
    Assigned = 2,
}
