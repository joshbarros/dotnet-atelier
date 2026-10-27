using Atelier.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Atelier.Workshops;

public enum ReserveOutcome { Held, Short, CapReached, Duplicate }

/// <summary>
/// The only writer of Workshop.SeatsLeft and of seat holds.
/// Two atomic steps in one transaction: a per-attendee cap upsert that only moves while the new total fits, then one
/// conditional UPDATE over seats and kits that matches only while both still cover the request.
/// </summary>
public class WorkshopsHandler(WorkshopsDbContext db) : IMessageHandler
{
    public const string Name = "workshops";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => ReserveAsync(delivery.MessageId, Json.Parse<ReserveSeats>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleaseSeats>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Workshops does not handle '{delivery.RoutingKey}'."),
    };

    public async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReserveSeats command, CancellationToken ct = default)
    {
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (!await db.ClaimAsync(Name, messageId, ct))
            {
                await tx.CommitAsync(ct);
                return ReserveOutcome.Duplicate;
            }

            // 1. the per-attendee cap: one atomic upsert that only moves while the new total still fits
            var withinCap = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO workshops."WorkshopAttendees" ("WorkshopId", "OwnerId", "Seats")
                SELECT d."Id", {command.OwnerId}, {command.Seats} FROM workshops."Workshops" d
                WHERE d."Id" = {command.WorkshopId} AND d."MaxPerAttendee" >= {command.Seats}
                ON CONFLICT ("WorkshopId", "OwnerId") DO UPDATE SET "Seats" = workshops."WorkshopAttendees"."Seats" + {command.Seats}
                WHERE workshops."WorkshopAttendees"."Seats" + {command.Seats} <=
                      (SELECT "MaxPerAttendee" FROM workshops."Workshops" WHERE "Id" = {command.WorkshopId})
                """, ct);

            // 2. the seats and, when asked, one kit per seat: ONE conditional UPDATE over both counters, so the bundle is
            //    taken whole or not at all. If the kits are gone the seats are not touched either.
            var kits = command.Kit ? command.Seats : 0;
            var updated = withinCap == 1
                ? await db.Workshops
                    .Where(s => s.Id == command.WorkshopId && s.SeatsLeft >= command.Seats && s.KitsLeft >= kits)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.SeatsLeft, x => x.SeatsLeft - command.Seats)
                        .SetProperty(x => x.KitsLeft, x => x.KitsLeft - kits), ct)
                : 0;

            if (withinCap == 1 && updated == 1)
            {
                db.Holds.Add(new SeatHold
                {
                    OrderId = command.OrderId,
                    OwnerId = command.OwnerId,
                    WorkshopId = command.WorkshopId,
                    Seats = command.Seats,
                    Kits = kits,
                    Status = HoldStatus.Held,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                db.Enqueue("workshops", Topology.HeldKey, command.OrderId,
                    new SeatsHeld(command.OrderId, command.OwnerId, command.WorkshopId, command.Seats, command.Reference));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return ReserveOutcome.Held;
            }

            // Either the cap or the stock said no. Roll back, so a cap increment never outlives a failed reserve.
            await tx.RollbackAsync(ct);
        }

        // The refusal is recorded in a fresh transaction (the claim was rolled back with the attempt).
        db.ChangeTracker.Clear();
        await using var refusal = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await refusal.CommitAsync(ct);
            return ReserveOutcome.Duplicate;
        }

        var workshop = await db.Workshops.AsNoTracking().Where(s => s.Id == command.WorkshopId)
            .Select(s => new { s.SeatsLeft, s.KitsLeft, s.MaxPerAttendee }).FirstOrDefaultAsync(ct);
        var held = await db.Attendees.AsNoTracking()
            .Where(c => c.WorkshopId == command.WorkshopId && c.OwnerId == command.OwnerId)
            .Select(c => c.Seats).FirstOrDefaultAsync(ct);
        var overCap = workshop is not null && held + command.Seats > workshop.MaxPerAttendee;
        var kitsShort = !overCap && command.Kit && workshop is not null && workshop.KitsLeft < command.Seats;
        db.Enqueue("workshops", Topology.ShortKey, command.OrderId,
            new SeatsShort(command.OrderId, command.Reference, command.Seats, workshop?.SeatsLeft ?? 0,
                overCap ? "cap" : kitsShort ? "kits" : "stock", workshop?.MaxPerAttendee ?? 0, workshop?.KitsLeft ?? 0));
        await db.SaveChangesAsync(ct);
        await refusal.CommitAsync(ct);
        return overCap ? ReserveOutcome.CapReached : ReserveOutcome.Short;
    }

    /// <summary>Gives held seats back exactly once. A second release for the same order changes nothing.</summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleaseSeats command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.OrderId == command.OrderId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.OrderId == command.OrderId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                await db.Workshops
                    .Where(s => s.Id == hold.WorkshopId && s.SeatsLeft + hold.Seats <= s.SeatsTotal && s.KitsLeft + hold.Kits <= s.KitsTotal)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.SeatsLeft, x => x.SeatsLeft + hold.Seats)
                        .SetProperty(x => x.KitsLeft, x => x.KitsLeft + hold.Kits), ct);
                await db.Attendees
                    .Where(c => c.WorkshopId == hold.WorkshopId && c.OwnerId == hold.OwnerId && c.Seats >= hold.Seats)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Seats, x => x.Seats - hold.Seats), ct);
                db.Enqueue("workshops", Topology.ReleasedKey, command.OrderId,
                    new SeatsReleased(command.OrderId, hold.Seats, command.Reason));
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }
}
