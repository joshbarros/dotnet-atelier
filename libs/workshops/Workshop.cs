namespace Atelier.Workshops;

public class Workshop
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Track { get; set; } = "";
    public string Instructor { get; set; } = "";
    public DateTimeOffset WorkshopDate { get; set; }
    public long PriceCents { get; set; }
    public int SeatsTotal { get; set; }
    public int SeatsLeft { get; set; }
    /// <summary>Laptop kits to lend, one per seat that asks for one. Taken together with the seats or not at all.</summary>
    public int KitsTotal { get; set; }
    public int KitsLeft { get; set; }
    public long KitPriceCents { get; set; }
    /// <summary>The most seats one attendee may hold in this workshop at any time.</summary>
    public int MaxPerAttendee { get; set; } = 2;
    public DateTimeOffset CreatedAt { get; set; }
}

public enum HoldStatus { Held, Released }

public class SeatHold
{
    public Guid OrderId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid WorkshopId { get; set; }
    public int Seats { get; set; }
    public int Kits { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}

/// <summary>How many seats one attendee holds in one workshop. Written only by the Workshops handler.</summary>
public class WorkshopAttendee
{
    public Guid WorkshopId { get; set; }
    public Guid OwnerId { get; set; }
    public int Seats { get; set; }
}
