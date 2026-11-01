using StackExchange.Redis;

namespace Ume.LlmGateway.Infrastructure.Stores;

/// <summary>
/// Redis commands sent explicitly where StackExchange.Redis would choose one the restricted ACL user
/// (<c>deploy/redis/users.acl.example</c>) may not run.
/// </summary>
internal static class RedisCommands
{
    /// <summary>
    /// Deletes keys with <c>DEL</c>. <c>IDatabase.KeyDeleteAsync</c> sends <c>UNLINK</c> to
    /// Redis 4 and later, which the ACL does not allow (deployed ACL files would have to change), so a delete failed with NOPERM.
    /// </summary>
    public static Task DeleteKeysAsync(this IDatabase db, RedisKey[] keys, CommandFlags flags = CommandFlags.None) =>
        db.ExecuteAsync("DEL", keys.Cast<object>().ToArray(), flags);
}
