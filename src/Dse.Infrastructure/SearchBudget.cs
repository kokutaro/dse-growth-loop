using Dse.Domain;

namespace Dse.Infrastructure;

// Covers projection queries and the primary point reads used to validate their evidence.
public sealed class SearchBudget : IDisposable
{
    private static readonly AsyncLocal<SearchBudget?> Current = new();
    private readonly SearchBudget? previous;
    private double ru;
    public double RequestUnits => ru;
    public SearchBudget() { previous = Current.Value; Current.Value = this; }
    public static void Charge(double requestCharge)
    {
        if (Current.Value is not { } budget) return;
        budget.ru += requestCharge;
        if (budget.previous is not null) ChargeParent(budget.previous, requestCharge);
        ProjectionRules.CheckBudget(0, budget.ru);
    }
    private static void ChargeParent(SearchBudget parent, double charge)
    {
        parent.ru += charge;
        ProjectionRules.CheckBudget(0, parent.ru);
        if (parent.previous is not null) ChargeParent(parent.previous, charge);
    }
    public void Dispose() => Current.Value = previous;
}
