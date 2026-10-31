namespace Ume.LlmGateway.AdminApi.Common;

public sealed record PageDto<T>(IReadOnlyList<T> Items, int Total);
