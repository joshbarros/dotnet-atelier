namespace Atelier.Orders;

public enum OrderStatus { Requested, Held, Sold, Refunded, Rejected }

public class Order
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid WorkshopId { get; set; }
    public string WorkshopCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Seats { get; set; }
    public long TotalCents { get; set; }
    /// <summary>The attendee also reserved a laptop kit for each seat.</summary>
    public bool Kit { get; set; }
    public OrderStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? VoucherNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? SeatsReleasedAt { get; set; }
}

public class OrderOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the refund race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public record CreateOrderRequest(string? WorkshopCode, string? Reference, long Seats, bool Kit = false);

public record OrderDto(
    Guid Id, Guid OwnerId, string WorkshopCode, string Reference, int Seats, long TotalCents, bool Kit, string Status,
    string? Reason, string? VoucherNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? SeatsReleasedAt)
{
    public static OrderDto From(Order b) => new(
        b.Id, b.OwnerId, b.WorkshopCode, b.Reference, b.Seats, b.TotalCents, b.Kit, b.Status.ToString(),
        b.Reason, b.VoucherNumber, b.CreatedAt, b.UpdatedAt, b.SeatsReleasedAt);
}
