using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ume.LlmGateway.Infrastructure.Persistence;

public sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => value.ToUniversalTime(),
    value => value.ToUniversalTime());
