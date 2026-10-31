namespace Ume.LlmGateway.Domain;

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

/// <summary>
/// Which inline file parts (images, documents, audio, video) a key may send. Files are base64 payloads the PII guard
/// cannot inspect, so keys for very sensitive work can refuse them outright.
/// </summary>
public enum AttachmentPolicy
{
    /// <summary>Images, documents, audio and video are forwarded.</summary>
    Allowed = 0,

    /// <summary>Images are forwarded; documents, audio, video and provider file references are rejected.</summary>
    ImagesOnly = 1,

    /// <summary>Text only: any file part is rejected.</summary>
    None = 2,
}

/// <summary>Kinds of inline file parts found in a request body.</summary>
[Flags]
public enum AttachmentKinds
{
    None = 0,
    Image = 1,

    /// <summary>PDFs and other files, including references to files uploaded to a provider (<c>file_id</c>).</summary>
    Document = 2,
    Audio = 4,
    Video = 8,
}

public enum PiiCategory
{
    Personnummer = 0,
    Samordningsnummer = 1,
    Email = 2,
    Phone = 3,
    Iban = 4,
}

/// <summary>What the gateway does when the content guard flags a request or answer (per key).</summary>
public enum ContentGuardPolicy
{
    /// <summary>No checks (default).</summary>
    Off = 0,

    /// <summary>Checked and recorded (categories on the usage record, a security event, <c>x-ume-guard</c>); sent on unchanged.</summary>
    Record = 1,

    /// <summary>A flagged request is refused with <c>content_blocked</c>; a flagged answer is withheld.</summary>
    Block = 2,
}
