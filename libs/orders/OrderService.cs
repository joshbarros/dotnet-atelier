using Atelier.Shared;
using Atelier.Shared.Auth;
using Atelier.Shared.Contracts;
using Atelier.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Atelier.Orders;

/// <summary>HTTP side of the saga: writes the order and its first outbox row in one local transaction.</summary>
public class OrderService(OrdersDbContext db, IWorkshopCatalog workshops, ICurrentUser user)
{
    public async Task<Order> CreateAsync(CreateOrderRequest request, CancellationToken ct)
    {
        var ownerId = user.Id ?? throw new DomainException(401, "Sign in to book.");
        if (string.IsNullOrWhiteSpace(request.WorkshopCode))
            throw new DomainException(400, "Workshop code is required.");
        var error = OrderRules.Validate(request.Reference, request.Seats);
        if (error is not null)
            throw new DomainException(400, error);

        var workshop = await workshops.FindAsync(request.WorkshopCode.Trim(), ct)
            ?? throw new DomainException(404, $"Workshop '{request.WorkshopCode}' does not exist.");

        if (request.Kit && workshop.KitsTotal == 0)
            throw new DomainException(400, $"Workshop '{workshop.Code}' has no kits to lend.");

        var seats = (int)request.Seats;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId,
            WorkshopId = workshop.Id,
            WorkshopCode = workshop.Code,
            Reference = reference,
            Seats = seats,
            TotalCents = OrderRules.TotalCents(workshop.PriceCents + (request.Kit ? workshop.KitPriceCents : 0), seats),
            Kit = request.Kit,
            Status = OrderStatus.Requested,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Orders.Add(order);
        db.Enqueue("orders", Topology.ReserveKey, order.Id, new ReserveSeats(order.Id, ownerId, workshop.Id, seats, reference, request.Kit));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DomainException(409, $"Reference '{reference}' is already booked on {workshop.Code}.");
        }
        await tx.CommitAsync(ct);
        return order;
    }

    /// <summary>The owner or staff only. Anyone else gets a 404, so ids cannot be probed.</summary>
    public async Task<Order> GetAsync(Guid id, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (order is null || !OwnerAccess.CanAccess(user, order.OwnerId))
            throw new DomainException(404, "Order not found.");
        return order;
    }

    public async Task<(List<Order> Items, int Total)> ListAsync(string? workshopCode, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking();
        if (!user.IsStaff)
        {
            var me = user.Id ?? throw new DomainException(401, "Sign in to see orders.");
            query = query.Where(b => b.OwnerId == me);
        }
        if (!string.IsNullOrWhiteSpace(workshopCode))
            query = query.Where(b => b.WorkshopCode == workshopCode);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(b => b.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    /// <summary>Moves Held or Sold to Refunded and asks Workshops to give the seats back. The voucher is not touched.</summary>
    public async Task<Order> RefundAsync(Guid id, CancellationToken ct)
    {
        await GetAsync(id, ct); // 404 unless the caller may see it

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var moved = await db.Orders
            .Where(b => b.Id == id && (b.Status == OrderStatus.Held || b.Status == OrderStatus.Sold))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, OrderStatus.Refunded)
                .SetProperty(b => b.Reason, "Refunded by request.")
                .SetProperty(b => b.UpdatedAt, now), ct);
        if (moved != 1)
        {
            var current = await db.Orders.AsNoTracking().FirstAsync(b => b.Id == id, ct);
            throw new DomainException(409, $"An order in status {current.Status} cannot be refunded.");
        }

        db.Enqueue("orders", Topology.ReleaseKey, id, new ReleaseSeats(id, "refund"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Orders.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}
