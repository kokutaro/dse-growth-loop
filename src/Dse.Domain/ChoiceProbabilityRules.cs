namespace Dse.Domain;

public sealed record ChoiceProbabilityValidation(bool Accepted,string Reason,double Sum,double AllowedError,int? RoundingDecimals,bool Normalized=false);

// SDK describes approximately-unit sums but specifies no numeric tolerance.
// Local bounded interoperability policy: at most 2 percentage points, only for
// values on an observed 2–6 decimal grid and a feasible rounded probability distribution.
public static class ChoiceProbabilityRules
{
    public const string Version="choice-probabilities-v2";
    public const double ExactTolerance=0.000001;
    public const double RoundingCap=0.02;
    private const double FloatingSlack=0.000000000001;
    public static ChoiceProbabilityValidation Validate(IEnumerable<double> values)
    {
        var p=values.ToArray();
        if(p.Length is <2 or >255||p.Any(x=>!double.IsFinite(x)||x is <0 or >1))return new(false,"invalid_probability",0,0,null);
        var sum=p.Sum();
        if(Math.Abs(sum-1)<=ExactTolerance)return new(true,"unit_sum",sum,ExactTolerance,null);
        for(var digits=2;digits<=6;digits++)
        {
            if(!p.All(x=>Math.Abs(x-Math.Round(x,digits,MidpointRounding.AwayFromZero))<=FloatingSlack))continue;
            var halfStep=0.5*Math.Pow(10,-digits);var allowed=Math.Min(RoundingCap,p.Length*halfStep);
            var feasible=p.Sum(x=>Math.Max(0,x-halfStep))<=1+FloatingSlack&&p.Sum(x=>Math.Min(1,x+halfStep))>=1-FloatingSlack;
            var accepted=feasible&&Math.Abs(sum-1)<=allowed+FloatingSlack;
            return new(accepted,accepted?$"rounded_{digits}dp":"invalid_sum",sum,allowed,digits);
        }
        return new(false,"unknown_rounding_precision",sum,ExactTolerance,null);
    }
}
