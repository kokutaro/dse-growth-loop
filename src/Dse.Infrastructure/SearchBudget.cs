using Dse.Domain;

namespace Dse.Infrastructure;

public sealed record SearchCharge(string Operation, double RequestCharge);

// Covers projection queries and the primary point reads used to validate their evidence.
public sealed class SearchBudget : IDisposable
{
    private static readonly AsyncLocal<SearchBudget?> Current = new();
    private readonly SearchBudget? previous;
    private double ru;
    private readonly List<SearchCharge> charges = [];
    public double RequestUnits => ru;
    public IReadOnlyList<SearchCharge> Charges => charges.AsReadOnly();
    public SearchBudget() { previous = Current.Value; Current.Value = this; }
    public static void Charge(double requestCharge, string operation = "primary_read")
    {
        if (!double.IsFinite(requestCharge) || requestCharge < 0 || operation is not ("projection_page" or "primary_read"))
            throw new ArgumentException("Invalid Cosmos search charge");
        if (Current.Value is not { } budget) return;
        ChargeParent(budget, new(operation, requestCharge));
    }
    private static void ChargeParent(SearchBudget parent, SearchCharge charge)
    {
        parent.ru += charge.RequestCharge;
        parent.charges.Add(charge);
        ProjectionRules.CheckBudget(0, parent.ru);
        if (parent.previous is not null) ChargeParent(parent.previous, charge);
    }
    public void Dispose() => Current.Value = previous;
}
