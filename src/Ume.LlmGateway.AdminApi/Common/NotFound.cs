namespace Ume.LlmGateway.AdminApi.Common;

/// <summary>
/// The 404 answers of the admin API, one per kind of thing, so each message is written once (English; the admin UI shows
/// it). Each call makes a new exception: <c>?? throw NotFound.Key()</c>. Something the user may not see is answered with
/// the same 404 as something that does not exist (see <see cref="OwnerScopeResolver.VisibleOrNotFoundAsync"/>).
/// </summary>
public static class NotFound
{
    public static ApiFaultException Alert() => Of("alert");

    public static ApiFaultException Budget() => Of("budget");

    public static ApiFaultException BudgetOwner() => Of("budget's owner");

    public static ApiFaultException Department() => Of("department");

    public static ApiFaultException Key() => Of("key");

    public static ApiFaultException Model() => Of("model");

    public static ApiFaultException Provider() => Of("provider");

    public static ApiFaultException Request() => Of("request");

    public static ApiFaultException Route() => Of("route");

    public static ApiFaultException Rule() => Of("rule");

    public static ApiFaultException Team() => Of("team");

    private static ApiFaultException Of(string what) => new(StatusCodes.Status404NotFound, $"The {what} does not exist.");
}
