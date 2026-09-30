using System.Text;
using System.Text.Json.Serialization;

namespace Dse.Domain;

public sealed record ExperiencePeriod(string? Start, string? End, string Precision, int? Months)
{
    public void Validate()
    {
        if (Precision == "unknown")
        {
            if (Start is not null || End is not null || Months is not null) throw new ArgumentException("Invalid period");
            return;
        }
        if (Precision is not ("day" or "month" or "year") || !DateOnly.TryParseExact(Start, "yyyy-MM-dd", out var start))
            throw new ArgumentException("Invalid period");
        if ((Precision == "month" && start.Day != 1) || (Precision == "year" && (start.Month != 1 || start.Day != 1)))
            throw new ArgumentException("Invalid precision");
        if (End is null)
        {
            if (Months is not null) throw new ArgumentException("Invalid period");
            return;
        }
        if (!DateOnly.TryParseExact(End, "yyyy-MM-dd", out var end) || end < start ||
            (Precision == "month" && end.Day != 1) || (Precision == "year" && (end.Month != 1 || end.Day != 1)))
            throw new ArgumentException("Invalid period");
        var months = (end.Year - start.Year) * 12 + end.Month - start.Month - (end.Day < start.Day ? 1 : 0);
        if (Months != months) throw new ArgumentException("Invalid months");
    }
}
public sealed record ExperienceEdit(string ExperienceId, string Description, ExperiencePeriod Period);
public sealed record ProfileUpdate([property: JsonRequired] string DisplayName, [property: JsonRequired] bool Available, [property: JsonRequired] string Action, [property: JsonRequired] ExperienceEdit[] ExperienceEdits, string? PublicationState = null)
{
    public void Validate()
    {
        if (PublicationState is not (null or "published" or "withdrawn") || string.IsNullOrWhiteSpace(DisplayName) || DisplayName.EnumerateRunes().Count() > 100 ||
            Action is not ("correct" or "confirm") || ExperienceEdits is null || ExperienceEdits.Length > 20 ||
            ExperienceEdits.Any(e => e is null || (e.ExperienceId is null || !RecordJson.IsId(e.ExperienceId)) || string.IsNullOrWhiteSpace(e.Description) ||
                e.Description.EnumerateRunes().Count() > 8000 || e.Period is null) ||
            ExperienceEdits.Select(e => e.ExperienceId).Distinct().Count() != ExperienceEdits.Length)
            throw new ArgumentException("Invalid profile update");
        foreach (var edit in ExperienceEdits) edit.Period.Validate();
    }
}
