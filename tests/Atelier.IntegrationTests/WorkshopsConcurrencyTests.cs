using System.Collections.Concurrent;
using Atelier.Workshops;
using Atelier.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Atelier.IntegrationTests;

public class WorkshopsConcurrencyTests
{
    [Fact]
    public async Task LastSeat_TwoConcurrentOrders_OneHeldOneShortNoHoldForTheLoser()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(seatsTotal: 1);

        var outcomes = new ConcurrentBag<(Guid Order, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var orderId = Guid.CreateVersion7();
            await using var db = TestDatabase.Workshops();
            var outcome = await new WorkshopsHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveSeats(orderId, Guid.NewGuid(), workshop.Id, 1, $"ATT-{n}"));
            outcomes.Add((orderId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Workshops();
        var remaining = await check.Workshops.Where(s => s.Id == workshop.Id).Select(s => s.SeatsLeft).FirstAsync();
        var holds = await check.Holds.ToListAsync();
        var loser = outcomes.Single(o => o.Outcome == ReserveOutcome.Short).Order;

        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Held);
        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Short);
        Assert.Equal(0, remaining);
        Assert.Single(holds);
        Assert.DoesNotContain(holds, h => h.OrderId == loser);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey));
    }

    [Fact]
    public async Task Release_GivesTheUnitsBackExactlyOnce()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(seatsTotal: 4);
        var orderId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Workshops())
            await new WorkshopsHandler(db).ReserveAsync(Guid.CreateVersion7(), new ReserveSeats(orderId, Guid.NewGuid(), workshop.Id, 3, "ATT-1"));

        // Two different commands (poison compensation and a refund) both ask for the same seats back.
        await using var first = TestDatabase.Workshops();
        await using var second = TestDatabase.Workshops();
        var one = await new WorkshopsHandler(first).ReleaseAsync(Guid.CreateVersion7(), new ReleaseSeats(orderId, "poison"));
        var two = await new WorkshopsHandler(second).ReleaseAsync(Guid.CreateVersion7(), new ReleaseSeats(orderId, "refund"));

        await using var check = TestDatabase.Workshops();
        Assert.True(one);
        Assert.False(two);
        Assert.Equal(4, await check.Workshops.Where(s => s.Id == workshop.Id).Select(s => s.SeatsLeft).FirstAsync());
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(seatsTotal: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveSeats(Guid.CreateVersion7(), Guid.NewGuid(), workshop.Id, 2, "ATT-1");

        await using var a = TestDatabase.Workshops();
        await using var b = TestDatabase.Workshops();
        var first = await new WorkshopsHandler(a).ReserveAsync(messageId, command);
        var second = await new WorkshopsHandler(b).ReserveAsync(messageId, command);

        await using var check = TestDatabase.Workshops();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, await check.Workshops.Where(s => s.Id == workshop.Id).Select(s => s.SeatsLeft).FirstAsync());
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesANegativeRemainder()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(seatsTotal: 2);
        await using var db = TestDatabase.Workshops();

        // A bug that skipped the WHERE clause would hit the check constraint instead of overselling.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Workshops.Where(s => s.Id == workshop.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SeatsLeft, x => x.SeatsLeft - 3)));
    }

    [Fact]
    public async Task Cap_ConcurrentOrdersByTheSameAttendee_NeverPassTheCap()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(seatsTotal: 20, maxPerAttendee: 2);
        var attendee = Guid.NewGuid();

        var outcomes = new ConcurrentBag<ReserveOutcome>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 6).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = TestDatabase.Workshops();
            outcomes.Add(await new WorkshopsHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveSeats(Guid.CreateVersion7(), attendee, workshop.Id, 1, $"ATT-{n}")));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Workshops();
        Assert.Equal(2, outcomes.Count(o => o == ReserveOutcome.Held));
        Assert.Equal(4, outcomes.Count(o => o == ReserveOutcome.CapReached));
        Assert.Equal(2, await check.Attendees.Where(c => c.OwnerId == attendee).Select(c => c.Seats).SingleAsync());
        Assert.Equal(18, await check.Workshops.Where(s => s.Id == workshop.Id).Select(s => s.SeatsLeft).FirstAsync());
        Assert.Equal(2, await check.Holds.CountAsync());
    }

    [Fact]
    public async Task Cap_AtWholeRequestLevel_RefusesTheOrderThatWouldCrossIt()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(seatsTotal: 10, maxPerAttendee: 2);
        var attendee = Guid.NewGuid();
        await using var db = TestDatabase.Workshops();
        var handler = new WorkshopsHandler(db);

        Assert.Equal(ReserveOutcome.Held, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveSeats(Guid.CreateVersion7(), attendee, workshop.Id, 1, "ATT-1")));
        // 1 held + 2 asked = 3 > 2: nothing moves, neither the stock nor the attendee's count
        Assert.Equal(ReserveOutcome.CapReached, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveSeats(Guid.CreateVersion7(), attendee, workshop.Id, 2, "ATT-2")));
        // another attendee is unaffected
        Assert.Equal(ReserveOutcome.Held, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveSeats(Guid.CreateVersion7(), Guid.NewGuid(), workshop.Id, 2, "ATT-3")));

        await using var check = TestDatabase.Workshops();
        Assert.Equal(1, await check.Attendees.Where(c => c.OwnerId == attendee).Select(c => c.Seats).SingleAsync());
        Assert.Equal(7, await check.Workshops.Where(s => s.Id == workshop.Id).Select(s => s.SeatsLeft).FirstAsync());
        var refusal = await check.Outbox.Where(o => o.RoutingKey == Topology.ShortKey).ToListAsync();
        Assert.Single(refusal);
        Assert.Contains("\"cap\"", refusal[0].Payload);
    }

    [Fact]
    public async Task Cap_StockShortAfterTheCapPassed_RollsTheCapBack()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(seatsTotal: 1, maxPerAttendee: 2);
        var attendee = Guid.NewGuid();
        await using var db = TestDatabase.Workshops();

        var outcome = await new WorkshopsHandler(db).ReserveAsync(
            Guid.CreateVersion7(), new ReserveSeats(Guid.CreateVersion7(), attendee, workshop.Id, 2, "ATT-1"));

        await using var check = TestDatabase.Workshops();
        Assert.Equal(ReserveOutcome.Short, outcome);
        Assert.False(await check.Attendees.AnyAsync(c => c.OwnerId == attendee));
        Assert.Equal(1, await check.Workshops.Where(s => s.Id == workshop.Id).Select(s => s.SeatsLeft).FirstAsync());
    }

    [Fact]
    public async Task Cap_ReleaseGivesTheAttendeeTheirAllowanceBack()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(seatsTotal: 10, maxPerAttendee: 2);
        var attendee = Guid.NewGuid();
        var first = Guid.CreateVersion7();
        await using var db = TestDatabase.Workshops();
        var handler = new WorkshopsHandler(db);
        await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveSeats(first, attendee, workshop.Id, 2, "ATT-1"));
        Assert.Equal(ReserveOutcome.CapReached, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveSeats(Guid.CreateVersion7(), attendee, workshop.Id, 1, "ATT-2")));

        Assert.True(await handler.ReleaseAsync(Guid.CreateVersion7(), new ReleaseSeats(first, "refund")));
        Assert.False(await handler.ReleaseAsync(Guid.CreateVersion7(), new ReleaseSeats(first, "refund again")));

        Assert.Equal(ReserveOutcome.Held, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveSeats(Guid.CreateVersion7(), attendee, workshop.Id, 2, "ATT-3")));
        await using var check = TestDatabase.Workshops();
        Assert.Equal(2, await check.Attendees.Where(c => c.OwnerId == attendee).Select(c => c.Seats).SingleAsync());
    }
}
