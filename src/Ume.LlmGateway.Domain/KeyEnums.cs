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
