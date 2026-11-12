using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.ServiceDefaults;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>The admin UI shows these messages as they are; changing one is a visible change.</summary>
public class NotFoundTests
{
    private static readonly (string Name, string Message)[] Expected =
    [
        (nameof(NotFound.Alert), "The alert does not exist."),
        (nameof(NotFound.Budget), "The budget does not exist."),
        (nameof(NotFound.BudgetOwner), "The budget's owner does not exist."),
        (nameof(NotFound.Department), "The department does not exist."),
        (nameof(NotFound.Integration), "The integration does not exist."),
        (nameof(NotFound.Key), "The key does not exist."),
        (nameof(NotFound.Model), "The model does not exist."),
        (nameof(NotFound.Provider), "The provider does not exist."),
        (nameof(NotFound.Request), "The request does not exist."),
        (nameof(NotFound.Route), "The route does not exist."),
        (nameof(NotFound.Rule), "The rule does not exist."),
        (nameof(NotFound.Team), "The team does not exist."),
    ];

    public static TheoryData<string, string> Faults()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, message) in Expected)
        {
            data.Add(name, message);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Faults))]
    public void Each_fault_is_a_404_with_its_message(string name, string message)
    {
        var fault = (ApiFaultException)typeof(NotFound).GetMethod(name)!.Invoke(null, null)!;
        fault.Status.ShouldBe(404);
        fault.Message.ShouldBe(message);
    }

    [Fact]
    public void Every_fault_is_listed() =>
        typeof(NotFound).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Select(m => m.Name)
            .ShouldBe(Expected.Select(e => e.Name), ignoreOrder: true);
}
