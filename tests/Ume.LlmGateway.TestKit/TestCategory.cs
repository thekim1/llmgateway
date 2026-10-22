namespace Ume.LlmGateway.TestKit;

/// <summary>
/// Test traits. <c>[Trait(TestCategory.Name, TestCategory.Flaky)]</c> marks a test that depends on timing or on races
/// between the shared fixture's parallel test classes and has failed intermittently. Such tests run locally
/// (<c>dotnet test</c> runs everything) and in the weekly <i>Flake hunt</i> workflow, but not in the pull request
/// pipeline, which runs with <c>--filter-not-trait "Category=Flaky"</c>. A flaky test is a bug to fix, not a category to
/// grow: say why in a comment next to the trait, and remove the trait once it is fixed.
/// </summary>
public static class TestCategory
{
    public const string Name = "Category";
    public const string Flaky = "Flaky";
}
