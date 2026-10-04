using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;
public class AiWeeklyBudgetTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-04T03:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static AiLimits Limits => new(false, true, 4500000, 240, 200, 162, "price", "user-fixed-162", 16000, 3000, 1, 1, 60)
        { Weekly = true, WeeklyCalls = 1680, WeeklyTotalJpy = 2000, InfrastructureReserveJpy = 1000, ExternalAiReserveJpy = 200 };
    private static AiModel Model => new("Jev", "jev-1.13.0", "jev-1.13.0", "typesafe", "input-token", new Uri("https://api.typesafe.ai/v1/systemone"), "", .042m, 0, 3000);
    [Theory]
    [InlineData("2026-10-04T14:59:59Z", "ai-week-2026-09-28")]
    [InlineData("2026-10-04T15:00:00Z", "ai-week-2026-10-05")]
    public void Week_boundary_is_Monday_midnight_Japan_time(string utc, string id) => Assert.Equal(id, AiLedger.WeekId(DateTimeOffset.Parse(utc)));

    [Fact]
    public async Task Migration_preserves_previous_spend_and_uncertain_reservations_without_resetting_daily_tokens()
    {
        var clock = new Clock(); var store = new MemoryRecordStore(); var ledger = new AiLedger(store, clock);
        var legacy = Limits with { Weekly = false, UsdToJpy = 200 };
        var previous = await ledger.ReserveAsync("demo", "old", Model, legacy, 1000, 100, default);
        await ledger.SettleAsync(previous, null, Model, legacy, default);
        var imported = await ledger.InitializeWeekAsync("demo", Limits with { Stopped = true }, "approved", true, default);
        Assert.Equal(1, imported["calls"]!.GetValue<long>());
        Assert.Equal(.000042m, imported["usd"]!.GetValue<decimal>());
        Assert.Equal(1200m, imported["reservedNonRuntimeJpy"]!.GetValue<decimal>());
        var next = await ledger.ReserveAsync("demo", "new", Model, Limits, 1000, 100, default);
        await ledger.SettleAsync(next, new(500, 50, false), Model, Limits, default);
        var week = (await store.ReadAsync(RecordPartition.Catalog("demo"), AiLedger.WeekId(clock.Now), default))!.Value;
        Assert.Equal(.000063m, week["usd"]!.GetValue<decimal>());
        Assert.Equal(2, week["calls"]!.GetValue<long>());
        Assert.Equal(1650L, (await store.ReadAsync(RecordPartition.Catalog("demo"), next.DayId, default))!.Value["tokens"]!.GetValue<long>());
        var again = await ledger.InitializeWeekAsync("demo", Limits with { Stopped = true }, "approved", true, default);
        Assert.Equal(.000063m, again["usd"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task Weekly_mode_does_not_erase_prior_daily_call_cap_usage_and_keeps_weekly_money_guard()
    {
        var clock = new Clock(); var store = new MemoryRecordStore(); var ledger = new AiLedger(store, clock);
        for (var i = 0; i < 240; i++)
        {
            var old = await ledger.ReserveAsync("demo", "old-" + i, Model, Limits with { Weekly = false, UsdToJpy = 200 }, 1000, 100, default);
            await ledger.SettleAsync(old, new(1000, 100, false), Model, Limits with { Weekly = false, UsdToJpy = 200 }, default);
        }
        await ledger.InitializeWeekAsync("demo", Limits with { Stopped = true }, "approved", true, default);
        var r = await ledger.ReserveAsync("demo", "new", Model, Limits, 1000, 100, default);
        await ledger.SettleAsync(r, new(1000, 100, false), Model, Limits, default);
        Assert.Equal(241, (await store.ReadAsync(RecordPartition.Catalog("demo"), r.DayId, default))!.Value["calls"]!.GetValue<long>());
        Assert.Equal(AiFailure.Budget, (await Assert.ThrowsAsync<AiException>(() => ledger.ReserveAsync("demo", "blocked", Model,
            Limits with { WeeklyTotalJpy = 1200 }, 1000, 100, default))).Failure);
        Assert.Equal(AiFailure.Budget, (await Assert.ThrowsAsync<AiException>(() => ledger.ReserveAsync("demo", "tokens", Model,
            Limits with { DailyTokens = 1100 }, 1000, 100, default))).Failure);
    }

    [Fact]
    public async Task Week_requires_stopped_initialization_and_parallel_reservations_share_weekly_limit()
    {
        var clock = new Clock(); var store = new MemoryRecordStore(); var ledger = new AiLedger(store, clock);
        await Assert.ThrowsAsync<AiException>(() => ledger.ReserveAsync("demo", "uninitialized", Model, Limits, 1000, 100, default));
        await Assert.ThrowsAsync<ArgumentException>(() => ledger.InitializeWeekAsync("demo", Limits, "approved", false, default));
        await ledger.InitializeWeekAsync("demo", Limits with { Stopped = true }, "approved", true, default);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i => {
            try { await ledger.ReserveAsync("demo", "race-" + i, Model, Limits with { WeeklyCalls = 1, Concurrency = 8 }, 1000, 100, default); return true; }
            catch (AiException e) { Assert.Equal(AiFailure.Budget, e.Failure); return false; }
        }));
        Assert.Single(outcomes, x => x);
    }
    [Fact]
    public async Task Approved_fx_cannot_be_lowered_and_settlement_keeps_the_reserved_week_across_rollover()
    {
        var clock = new Clock(); var store = new MemoryRecordStore(); var ledger = new AiLedger(store, clock);
        await ledger.InitializeWeekAsync("demo", Limits with { Stopped = true }, "approved", true, default);
        await Assert.ThrowsAsync<AiException>(() => ledger.ReserveAsync("demo", "fx-change", Model, Limits with { UsdToJpy = 1 }, 1000, 100, default));
        var r = await ledger.ReserveAsync("demo", "rollover", Model, Limits, 1000, 100, default);
        clock.Now = DateTimeOffset.Parse("2026-10-04T15:00:00Z");
        await ledger.SettleAsync(r, new(500, 50, false), Model, Limits with { UsdToJpy = 1 }, default);
        var old = (await store.ReadAsync(RecordPartition.Catalog("demo"), r.WeekId!, default))!.Value;
        Assert.Equal(.000021m, old["usd"]!.GetValue<decimal>());
        await Assert.ThrowsAsync<AiException>(() => ledger.ReserveAsync("demo", "new-week", Model, Limits, 1000, 100, default));
        await ledger.InitializeWeekAsync("demo", Limits with { Stopped = true }, "approved-next-week", true, default);
        Assert.Equal(0m, (await store.ReadAsync(RecordPartition.Catalog("demo"), AiLedger.WeekId(clock.Now), default))!.Value["usd"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task Weekly_policy_cannot_be_bypassed_by_redeploying_legacy_daily_configuration()
    {
        var clock = new Clock(); var ledger = new AiLedger(new MemoryRecordStore(), clock);
        await ledger.InitializeWeekAsync("demo", Limits with { Stopped = true }, "approved", true, default);
        await Assert.ThrowsAsync<AiException>(() => ledger.ReserveAsync("demo", "legacy", Model, Limits with { Weekly = false }, 1000, 100, default));
        clock.Now = DateTimeOffset.Parse("2026-10-04T15:00:00Z");
        await Assert.ThrowsAsync<AiException>(() => ledger.ReserveAsync("demo", "legacy-next-week", Model, Limits with { Weekly = false }, 1000, 100, default));
    }

}
