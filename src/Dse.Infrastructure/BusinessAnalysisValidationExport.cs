using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

public sealed record BusinessValidationExportOptions(string PersonId, string RunId, string Directory, string FileName);

// Export an already completed synthetic result. This type cannot start a job or resolve an AI transport.
public sealed class BusinessAnalysisValidationExport(BusinessAnalysisService analysis, AuthorityProvider authority)
{
    public static BusinessValidationExportOptions Validate(IConfiguration config, bool development)
    {
        if (!development || config["ValidationExport:Enabled"] != "true" || config["ValidationExport:Synthetic"] != "true" ||
            config["Data:Mode"] != "Demo" || config["Ai:Mode"] != "Mock" ||
            config["AiUsage:Stopped"] != "true" || config["AiUsage:LiveOptIn"] != "false")
            throw new DomainException(403, "VALIDATION_EXPORT_DISABLED");
        var person = config["ValidationExport:PersonId"] ?? "";
        var run = config["ValidationExport:RunId"] ?? "";
        var directory = config["ValidationExport:Directory"] ?? "";
        var file = config["ValidationExport:FileName"] ?? "";
        var data = config["Data:LocalPath"] ?? "";
        if (!RecordJson.IsId(person) || !RecordJson.IsId(run) || !Path.IsPathFullyQualified(data) ||
            !Path.IsPathFullyQualified(directory) || file != Path.GetFileName(file) ||
            !file.EndsWith(".json", StringComparison.Ordinal) || !RecordJson.IsId(file[..^5]))
            throw new DomainException(422, "VALIDATION_EXPORT_CONFIGURATION_INVALID");
        directory = Path.GetFullPath(directory);
        data = Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar);
        var pathComparison = OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (directory.Equals(data, pathComparison) || directory.StartsWith(data + Path.DirectorySeparatorChar, pathComparison) ||
            directory.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), pathComparison))
            throw new DomainException(422, "VALIDATION_EXPORT_DIRECTORY_INVALID");
        RequirePrivateDirectory(directory);
        RequireNoLinks(data);
        // No implicit store/directory creation, bootstrap, seeding or cloud fallback.
        var records = new FileInfo(Path.Combine(data, "records.json"));
        if (!records.Exists || records.LinkTarget is not null) throw new DomainException(422, "VALIDATION_EXPORT_STORE_REQUIRED");
        return new(person, run, directory, file);
    }

    private static void RequirePrivateDirectory(string path)
    {
        RequireNoLinks(path);
        var directory = new DirectoryInfo(path);
        if (!directory.Exists || directory.LinkTarget is not null || OperatingSystem.IsWindows() ||
            (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new DomainException(422, "VALIDATION_EXPORT_PRIVATE_DIRECTORY_REQUIRED");
    }

    private static void RequireNoLinks(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (!current.Exists || current.LinkTarget is not null)
                throw new DomainException(422, "VALIDATION_EXPORT_DIRECTORY_INVALID");
    }

    public async Task ExportAsync(IConfiguration config, bool development, ActorAccess actor, CancellationToken ct)
    {
        var options = Validate(config, development);
        if (actor.PersonId != options.PersonId || actor.TenantId != "demo") throw new DomainException(403, "FORBIDDEN");
        actor = await authority.RequireAsync(actor, ct);
        RequireOperator(actor);
        var output = Path.Combine(options.Directory, options.FileName);
        var claim = Path.Combine(options.Directory, ".validation-" + ConversationService.Hash(actor.TenantId + ":" + actor.PrincipalId + ":" + options.RunId) + ".claim");
        if (File.Exists(output) || File.Exists(claim)) throw new DomainException(409, "VALIDATION_EXPORT_ALREADY_ATTEMPTED");
        await analysis.ConsumeAsync(actor, options.RunId, async (result, token) =>
        {
            // Canonical operator authority is checked again inside the same source/consent fence.
            RequireOperator(await authority.RequireAsync(actor, token));
            if (result.Text("status") != "completed" || result.Text("processingMode") != "fixture" ||
                result["recoveryRequired"]?.GetValue<bool>() == true)
                throw new DomainException(409, "VALIDATION_EXPORT_RESULT_NOT_READY");
            var report = Redact(result);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(report);
            RequirePrivateDirectory(options.Directory);
            token.ThrowIfCancellationRequested();
            // A durable CreateNew claim guards duplicate attempts, including an unknown file-write outcome.
            // It is never removed automatically and does not claim exactly-once external inference.
            try
            {
                using var intent = NewPrivateFile(claim);
                intent.Write(Encoding.UTF8.GetBytes(new JsonObject { ["kind"] = "synthetic-validation-export-intent-v1",
                    ["reportSha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) }.ToJsonString()));
                intent.Flush(true);
            }
            catch (IOException) when (File.Exists(claim)) { throw new DomainException(409, "VALIDATION_EXPORT_ALREADY_ATTEMPTED"); }
            using (var file = NewPrivateFile(output)) { file.Write(bytes); file.Flush(true); }
            var readback = await File.ReadAllBytesAsync(output, token);
            if (!readback.AsSpan().SequenceEqual(bytes)) throw new IOException("VALIDATION_EXPORT_READBACK_FAILED");
            return true;
        }, ct);
    }

    private static void RequireOperator(ActorAccess actor)
    {
        if (!actor.HasRole("operator") || !actor.HasRole("analyst") || !actor.HasRole("member"))
            throw new DomainException(403, "FORBIDDEN");
    }

    private static FileStream NewPrivateFile(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }

    private static JsonObject Redact(JsonObject result)
    {
        var groups = result["groups"]?.AsArray() ?? throw new DomainException(422, "VALIDATION_EXPORT_SCHEMA_INVALID");
        if (groups.Count > 100) throw new DomainException(422, "VALIDATION_EXPORT_SCHEMA_INVALID");
        var summaries = new JsonArray();
        foreach (var group in groups)
        {
            var evidence = group?["evidenceIds"]?.Deserialize<string[]>() ?? [];
            var cited = group?["hypothesis"]?["evidenceIds"]?.Deserialize<string[]>() ?? [];
            if (evidence.Length is < 1 or > 100 || cited.Length is < 1 or > 100 ||
                evidence.Distinct().Count() != evidence.Length || cited.Distinct().Count() != cited.Length || cited.Except(evidence).Any())
                throw new DomainException(422, "VALIDATION_EXPORT_SCHEMA_INVALID");
            var metrics = group?["metrics"]?.Deserialize<BusinessAnalysisMetrics>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new DomainException(422, "VALIDATION_EXPORT_SCHEMA_INVALID");
            // Only code-derived counts leave the process, never arbitrary strings, bodies, identities or audit payloads.
            if (metrics.IndependentChallenges < 0 || metrics.DistinctDepartments < 0 || metrics.ReportedHours < 0)
                throw new DomainException(422, "VALIDATION_EXPORT_SCHEMA_INVALID");
            summaries.Add(new JsonObject { ["independentChallenges"] = metrics.IndependentChallenges,
                ["distinctDepartments"] = metrics.DistinctDepartments, ["reportedHours"] = metrics.ReportedHours,
                ["evidenceCount"] = evidence.Length, ["citedEvidenceCount"] = cited.Length });
        }
        return new JsonObject { ["kind"] = "synthetic-business-validation-v1", ["synthetic"] = true,
            ["status"] = "completed", ["modelDispatchedByExport"] = false, ["businessRecordsWrittenByExport"] = false,
            ["groups"] = summaries };
    }
}
