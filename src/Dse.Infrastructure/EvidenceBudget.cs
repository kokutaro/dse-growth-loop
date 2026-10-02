using System.Text;
using Dse.Domain;

namespace Dse.Infrastructure;

public static class EvidenceBudget
{
    // UTF-8 bytes conservatively bound tokens. Never split a Unicode scalar.
    public static SourceRef[] Trim(SourceRef[] sources, int? limit, string candidateId, List<EvidenceCut> cuts)
    {
        if (limit is < 64 or > 8000) throw new ArgumentException("Invalid evidence budget");
        var remaining = limit ?? int.MaxValue;
        List<SourceRef> result = [];
        foreach (var source in sources)
        {
            var builder = new StringBuilder();
            foreach (var rune in source.Text.EnumerateRunes())
            {
                if (rune.Utf8SequenceLength > remaining) break;
                builder.Append(rune); remaining -= rune.Utf8SequenceLength;
            }
            var text = builder.ToString();
            cuts.Add(new(candidateId, source.Id, Encoding.UTF8.GetByteCount(source.Text), Encoding.UTF8.GetByteCount(text)));
            if (!string.IsNullOrWhiteSpace(text)) result.Add(source with { Text = text });
        }
        return result.ToArray();
    }
}
