using Atelier.Shared;
using Atelier.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Atelier.Vouchers;

public enum VoucherStatus { Issued, Rejected }

public class Voucher
{
    public Guid OrderId { get; set; }
    public Guid OwnerId { get; set; }
    public string? Number { get; set; }
    public VoucherStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class VouchersDbContext(DbContextOptions<VouchersDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "vouchers";

    public DbSet<Voucher> Vouchers => Set<Voucher>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Voucher>(e =>
        {
            e.ToTable("Vouchers");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
    }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the voucher once the seats are held. A poison reference is committed as rejected.</summary>
public class VouchersHandler(VouchersDbContext db) : IMessageHandler
{
    public const string Name = "vouchers";

    public async Task HandleAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.RoutingKey != Topology.HeldKey)
            throw new InvalidOperationException($"Vouchers does not handle '{delivery.RoutingKey}'.");

        var outcome = await IssueAsync(delivery.MessageId, Json.Parse<SeatsHeld>(delivery.Body), ct);
        if (outcome == IssueOutcome.Rejected)
            throw new PoisonMessageException("Reference carries the poison token; rejection is committed.");
    }

    public async Task<IssueOutcome> IssueAsync(Guid messageId, SeatsHeld held, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return IssueOutcome.Duplicate;
        }

        IssueOutcome outcome;
        if (PoisonRule.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Vouchers.Add(new Voucher
            {
                OrderId = held.OrderId,
                OwnerId = held.OwnerId,
                Status = VoucherStatus.Rejected,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("vouchers", Topology.ReleaseKey, held.OrderId, new ReleaseSeats(held.OrderId, "poison"));
            db.Enqueue("vouchers", Topology.RejectedKey, held.OrderId, new VoucherRejected(held.OrderId, reason));
            outcome = IssueOutcome.Rejected;
        }
        else
        {
            var number = $"VC-{held.OrderId.ToString("N")[^12..].ToUpperInvariant()}";
            db.Vouchers.Add(new Voucher
            {
                OrderId = held.OrderId,
                OwnerId = held.OwnerId,
                Number = number,
                Status = VoucherStatus.Issued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("vouchers", Topology.IssuedKey, held.OrderId, new VoucherIssued(held.OrderId, number));
            outcome = IssueOutcome.Issued;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }
}
