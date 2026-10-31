using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

public sealed record WorkbookDate(string? Value, string Precision, string? DisplayValue);
public sealed record WorkbookPeriodResult(ExperiencePeriod Period, string EndState, string[] Warnings);

// Reads explicit value/format syntax only. Never guesses dates from a cell address,
// a first-of-month serial, prose keywords, the clock, or another project's dates.
public static class WorkbookDates
{
    public const string Version = "workbook-period-v1";
    public static WorkbookDate Read(string text, CellSource? cell)
    {
        if(cell is not null && (cell.ValueState!="available" || cell.ValueType=="e"))return new(null,"unknown",null);
        var raw=text.Trim();
        if(cell is {ValueType: "n" or "d"} && DateTime.TryParseExact(raw,["yyyy-MM-dd","yyyy-MM-dd'T'HH:mm:ss"],CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))
        {
            var formatPrecision=FormatPrecision(cell.NumberFormat);
            var precision=formatPrecision!="unknown"?formatPrecision:cell.ValueType=="d"&&string.IsNullOrEmpty(cell.NumberFormat)?"day":"unknown";
            return precision switch {
                "month"=>new(date.ToString("yyyy-MM",CultureInfo.InvariantCulture),precision,date.ToString("yyyy年M月",CultureInfo.InvariantCulture)),
                "day"=>new(date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),precision,date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)),
                _=>new(null,"unknown",null)
            };
        }
        // A text date needs an explicit four-digit year and unambiguous component order.
        if(Regex.IsMatch(raw,@"^\d{4}(?:-\d{1,2}|年\d{1,2}月)$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))
        {
            var m=InternalResumeSheet.Month(raw);return m is null?new(null,"unknown",null):new(m,"month",raw);
        }
        if(DateTime.TryParseExact(raw,["yyyy-MM-dd","yyyy-M-d","yyyy年M月d日"],CultureInfo.InvariantCulture,DateTimeStyles.None,out var day))
            return new(day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),"day",raw);
        return new(null,"unknown",null);
    }
    public static string FormatPrecision(string? format)
    {
        if(format is null || format.Length>256)return "unknown";
        if(int.TryParse(format,out var id))return id switch { 17=>"month",14 or 15 or 22=>"day",_=>"unknown" };
        // Formatting grammar, not natural-language interpretation. Literals/escaped/bracket
        // sections do not become date tokens (e.g. yyyy\"days\" is still year-only).
        var tokens=Regex.Replace(format,"\"[^\"]*\"|\\\\.|\\[[^]]*\\]","",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)).ToLowerInvariant();
        if(tokens.Contains(';') || !tokens.Contains('y') || !tokens.Contains('m'))return "unknown";
        if(tokens.Contains('d'))return "day";
        return tokens.IndexOfAny(['h','s'])<0?"month":"unknown";
    }
    public static WorkbookPeriodResult Resolve(WorkbookSection section,IEnumerable<StoredRecord> cells)
    {
        var rows=cells.ToDictionary(x=>x.Value.Text("id"),x=>x.Value);
        WorkbookDate Read(string? id) => id is not null && rows.TryGetValue(id,out var row)
            ? WorkbookDates.Read(row.Text("text"),row["sourceCell"]?.Deserialize<CellSource>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))
            : new(null,"unknown",null);
        var start=Read(section.StartEvidenceId);var end=Read(section.EndEvidenceId);List<string>warnings=[];
        var endState=section.EndState??(end.Value is not null?"dated":"unknown");
        if(endState=="ongoing" && (section.EndEvidenceId is null || end.Value is not null)){warnings.Add("WORKBOOK_END_STATE_CONFLICT");endState="unknown";end=new(null,"unknown",null);}
        if(endState=="unknown" && section.EndState is not null)end=new(null,"unknown",null);
        if(endState=="dated" && end.Value is null){warnings.Add("WORKBOOK_END_UNRESOLVED");endState="unknown";}
        if(section.StartEvidenceId is not null&&start.Value is null)warnings.Add("WORKBOOK_START_UNRESOLVED");
        if(endState!="dated")end=new(null,"unknown",null);
        var known=new[]{start,end}.Where(d=>d.Value is not null).ToArray();
        if(known.Length==0)return new(new(null,null,"unknown",null),endState,warnings.Distinct().ToArray());
        if(known.Select(d=>d.Precision).Distinct().Count()!=1)return new(new(null,null,"unknown",null),endState,[..warnings,"WORKBOOK_MIXED_DATE_PRECISION"]);
        var precision=known[0].Precision;
        if(precision!=section.Precision)warnings.Add("WORKBOOK_PRECISION_FROM_SOURCE");
        if(start.Value is not null && end.Value is not null && string.CompareOrdinal(start.Value,end.Value)>0)
            return new(new(null,null,"unknown",null),"unknown",[..warnings,"WORKBOOK_REVERSED_PERIOD"]);
        int? months=start.Value is null||end.Value is null?null:precision=="month"
            ? (int.Parse(end.Value[..4],CultureInfo.InvariantCulture)-int.Parse(start.Value[..4],CultureInfo.InvariantCulture))*12+int.Parse(end.Value[5..7],CultureInfo.InvariantCulture)-int.Parse(start.Value[5..7],CultureInfo.InvariantCulture)+1
            : ProfileExtraction.Period(start.Value+"〜"+end.Value).Months;
        var period=new ExperiencePeriod(start.Value,end.Value,precision,months);period.Validate();return new(period,endState,warnings.Distinct().ToArray());
    }
}
