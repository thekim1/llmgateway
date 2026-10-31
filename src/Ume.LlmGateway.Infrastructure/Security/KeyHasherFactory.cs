using System.Text;
using Ume.LlmGateway.Domain.Keys;

namespace Ume.LlmGateway.Infrastructure.Security;

public static class KeyHasherFactory
{
    public static VirtualKeyHasher Create(GatewaySecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new VirtualKeyHasher(Encoding.UTF8.GetBytes(options.KeyPepper));
    }
}
