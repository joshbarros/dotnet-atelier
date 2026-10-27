using Atelier.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Atelier.Orders;

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move an order forward. Each step runs only from the status it expects.</summary>
public class OrdersHandler(OrdersDbContext db, IOptions<OrderOptions> options) : IMessageHandler
{
    public const string Name = "orders";

    public Task HandleAsync(Delivery d, CancellationToken ct) => d.RoutingKey switch
    {
        Topology.HeldKey => MarkHeldAsync(d.MessageId, Json.Parse<SeatsHeld>(d.Body).OrderId, ct),
        Topology.ShortKey => MarkShortAsync(d.MessageId, Json.Parse<SeatsShort>(d.Body), ct),
        Topology.IssuedKey => MarkSoldAsync(d.MessageId, Json.Parse<VoucherIssued>(d.Body), ct),
        Topology.RejectedKey => MarkRejectedAsync(d.MessageId, Json.Parse<VoucherRejected>(d.Body), ct),
        Topology.ReleasedKey => MarkReleasedAsync(d.MessageId, Json.Parse<SeatsReleased>(d.Body), ct),
        _ => throw new InvalidOperationException($"Orders does not handle '{d.RoutingKey}'."),
    };

    public Task<MarkOutcome> MarkHeldAsync(Guid messageId, Guid orderId, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == orderId && b.Status == OrderStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Held)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkShortAsync(Guid messageId, SeatsShort m, CancellationToken ct = default)
    {
        var reason = m.Reason switch
        {
            "cap" => $"Cap: at most {m.PerAttendeeLimit} seats per attendee in this workshop.",
            "kits" => $"Kits: {m.RequestedSeats} kits wanted, {m.KitsLeft} left. No seat was taken.",
            _ => $"Short: {m.RequestedSeats} seats wanted, {m.SeatsLeft} left.",
        };
        return StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == m.OrderId && b.Status == OrderStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Rejected)
                .SetProperty(b => b.Reason, reason)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));
    }

    /// <summary>The final mark. If a refund got there first the row is no longer Held and nothing changes.</summary>
    public async Task<MarkOutcome> MarkSoldAsync(Guid messageId, VoucherIssued m, CancellationToken ct = default)
    {
        if (options.Value.FinalMarkDelayMs > 0)
            await Task.Delay(options.Value.FinalMarkDelayMs, ct);

        return await StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == m.OrderId && b.Status == OrderStatus.Held)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Sold)
                .SetProperty(b => b.VoucherNumber, m.Number)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct),
            orderIdForOrderCheck: m.OrderId);
    }

    public Task<MarkOutcome> MarkRejectedAsync(Guid messageId, VoucherRejected m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == m.OrderId && (b.Status == OrderStatus.Held || b.Status == OrderStatus.Requested))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Rejected)
                .SetProperty(b => b.Reason, m.Reason)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkReleasedAsync(Guid messageId, SeatsReleased m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Orders
            .Where(b => b.Id == m.OrderId && b.SeatsReleasedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.SeatsReleasedAt, DateTimeOffset.UtcNow), ct));

    private async Task<MarkOutcome> StepAsync(Guid messageId, CancellationToken ct, Func<Task<int>> update, Guid? orderIdForOrderCheck = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return MarkOutcome.Duplicate;
        }

        var changed = await update();
        if (changed == 0 && orderIdForOrderCheck is { } id)
        {
            // Skipped for a reason we can name? Requested means the "Held" mark has not landed yet: retry, do not workshop.
            var status = await db.Orders.AsNoTracking().Where(b => b.Id == id).Select(b => b.Status).FirstOrDefaultAsync(ct);
            if (status == OrderStatus.Requested)
                throw new OutOfOrderException("Final mark arrived before the Held mark.");
        }

        await tx.CommitAsync(ct);
        return changed == 1 ? MarkOutcome.Marked : MarkOutcome.Skipped;
    }
}
