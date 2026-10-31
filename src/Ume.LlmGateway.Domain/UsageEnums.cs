namespace Ume.LlmGateway.Domain;

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

/// <summary>Why the gateway refused a request's credentials.</summary>
public enum AuthFailureReason
{
    /// <summary>No key in Authorization, x-api-key or api-key.</summary>
    MissingKey = 0,

    /// <summary>Something was sent, but it is not a known key (malformed, made up, or a key from another system).</summary>
    InvalidKey = 1,

    KeyRevoked = 2,
    KeyExpired = 3,
    KeyDisabled = 4,

    /// <summary>The key is valid but its team or department is deactivated.</summary>
    OwnerInactive = 5,

    /// <summary>The key is valid but restricted to other client addresses (<see cref="Entities.VirtualKey.AllowedIpRanges"/>).</summary>
    AddressNotAllowed = 6,
}
