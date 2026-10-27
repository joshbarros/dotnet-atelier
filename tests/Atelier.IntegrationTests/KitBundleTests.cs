using System.Collections.Concurrent;
using Atelier.Shared.Messaging;
using Atelier.Workshops;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Atelier.IntegrationTests;

public class KitBundleTests
{
    private static async Task<ReserveOutcome> ReserveAsync(Workshop workshop, int seats, bool kit, Guid? owner = null, Guid? order = null)
    {
        await using var db = TestDatabase.Workshops();
        return await new WorkshopsHandler(db).ReserveAsync(Guid.CreateVersion7(),
            new ReserveSeats(order ?? Guid.CreateVersion7(), owner ?? Guid.NewGuid(), workshop.Id, seats, "ATT-KIT", kit));
    }

    private static async Task<(int Seats, int Kits)> CountersAsync(Workshop workshop)
    {
        await using var db = TestDatabase.Workshops();
        var row = await db.Workshops.Where(w => w.Id == workshop.Id).Select(w => new { w.SeatsLeft, w.KitsLeft }).SingleAsync();
        return (row.SeatsLeft, row.KitsLeft);
    }

    [Fact]
    public async Task ABundle_TakesSeatsAndKitsTogether()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(seatsTotal: 10, kitsTotal: 4, kitPriceCents: 1_500);

        Assert.Equal(ReserveOutcome.Held, await ReserveAsync(workshop, 3, kit: true));
        Assert.Equal((7, 1), await CountersAsync(workshop));
        Assert.Equal(ReserveOutcome.Held, await ReserveAsync(workshop, 2, kit: false));
        Assert.Equal((5, 1), await CountersAsync(workshop)); // a seat without a kit leaves the kits alone

        await using var check = TestDatabase.Workshops();
        Assert.Equal(new[] { 0, 3 }, (await check.Holds.Select(h => h.Kits).ToListAsync()).OrderBy(k => k).ToArray());
    }

    [Fact]
    public async Task NoKitsLeft_RefusesTheWholeBundle_AndTakesNoSeat()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(seatsTotal: 10, kitsTotal: 2, kitPriceCents: 1_500);
        Assert.Equal(ReserveOutcome.Held, await ReserveAsync(workshop, 2, kit: true));

        // Seats are plentiful (8) but kits are gone: the order is refused for the kits, and the seats stay on the shelf.
        Assert.Equal(ReserveOutcome.Short, await ReserveAsync(workshop, 1, kit: true));
        Assert.Equal((8, 0), await CountersAsync(workshop));

        await using var check = TestDatabase.Workshops();
        var refusal = await check.Outbox.Where(o => o.RoutingKey == Topology.ShortKey).SingleAsync();
        Assert.Contains("\"kits\"", refusal.Payload);
        Assert.Equal(1, await check.Holds.CountAsync());
    }

    [Fact]
    public async Task NoSeatsLeft_RefusesTheBundle_AndTakesNoKit()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(seatsTotal: 2, kitsTotal: 5, kitPriceCents: 1_500);
        Assert.Equal(ReserveOutcome.Held, await ReserveAsync(workshop, 2, kit: false));

        Assert.Equal(ReserveOutcome.Short, await ReserveAsync(workshop, 1, kit: true));
        Assert.Equal((0, 5), await CountersAsync(workshop));
    }

    [Fact]
    public async Task ConcurrentBundles_OnlyAsManyAsThereAreKitsSucceed_AndTheRefusedTakeNoSeats()
    {
        for (var round = 0; round < 3; round++)
        {
            await TestDatabase.OpenAsync();
            var workshop = await TestDatabase.AddWorkshopAsync(seatsTotal: 20, kitsTotal: 3, kitPriceCents: 1_500);

            var outcomes = new ConcurrentBag<ReserveOutcome>();
            var gate = new TaskCompletionSource();
            var tasks = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                outcomes.Add(await ReserveAsync(workshop, 1, kit: true));
            })).ToArray();
            gate.SetResult();
            await Task.WhenAll(tasks);

            Assert.Equal(3, outcomes.Count(o => o == ReserveOutcome.Held));
            Assert.Equal(9, outcomes.Count(o => o == ReserveOutcome.Short));
            Assert.Equal((17, 0), await CountersAsync(workshop)); // 3 seats went with the 3 kits, the 9 refused took none
        }
    }

    [Fact]
    public async Task MixedConcurrentOrders_NeverOversellEitherCounter()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(seatsTotal: 10, kitsTotal: 4, kitPriceCents: 1_500, maxPerAttendee: 10);

        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 24).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            await ReserveAsync(workshop, 1, kit: n % 2 == 0);
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Workshops();
        var held = await check.Holds.ToListAsync();
        var (seats, kits) = await CountersAsync(workshop);
        Assert.Equal(10, held.Sum(h => h.Seats)); // 24 asked for 10 seats: all sold
        Assert.Equal(0, seats);
        Assert.Equal(4 - held.Sum(h => h.Kits), kits);
        Assert.InRange(held.Sum(h => h.Kits), 0, 4);
    }

    [Fact]
    public async Task Release_GivesSeatsAndKitsBackTogether_Once()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(seatsTotal: 6, kitsTotal: 3, kitPriceCents: 1_500);
        var order = Guid.CreateVersion7();
        await ReserveAsync(workshop, 3, kit: true, order: order);
        Assert.Equal((3, 0), await CountersAsync(workshop));

        await using var a = TestDatabase.Workshops();
        await using var b = TestDatabase.Workshops();
        Assert.True(await new WorkshopsHandler(a).ReleaseAsync(Guid.CreateVersion7(), new ReleaseSeats(order, "refund")));
        Assert.False(await new WorkshopsHandler(b).ReleaseAsync(Guid.CreateVersion7(), new ReleaseSeats(order, "refund")));
        Assert.Equal((6, 3), await CountersAsync(workshop));
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesANegativeKitCount()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(seatsTotal: 4, kitsTotal: 1, kitPriceCents: 1_500);
        await using var db = TestDatabase.Workshops();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Workshops.Where(w => w.Id == workshop.Id)
            .ExecuteUpdateAsync(w => w.SetProperty(x => x.KitsLeft, x => x.KitsLeft - 2)));
    }
}
