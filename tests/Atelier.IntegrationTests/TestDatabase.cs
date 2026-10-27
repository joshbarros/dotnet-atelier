using Atelier.Orders;
using Atelier.Workshops;
using Atelier.Vouchers;
using Atelier.Shared.Auth;
using Atelier.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Atelier.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("ATELIER_TEST_DB")
        ?? "Host=localhost;Port=5486;Database=atelier_test;Username=atelier;Password=atelier";

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static OrdersDbContext Orders() => new(Options<OrdersDbContext>(OrdersDbContext.SchemaName));
    public static WorkshopsDbContext Workshops() => new(Options<WorkshopsDbContext>(WorkshopsDbContext.SchemaName));
    public static VouchersDbContext Vouchers() => new(Options<VouchersDbContext>(VouchersDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var orders = Orders();
        await using var seats = Workshops();
        await using var vouchers = Vouchers();
        await orders.Database.MigrateAsync();
        await seats.Database.MigrateAsync();
        await vouchers.Database.MigrateAsync();
        await orders.Database.ExecuteSqlRawAsync(
            "TRUNCATE orders.\"InboxMessages\", orders.\"OutboxMessages\", orders.\"Orders\", " +
            "workshops.\"InboxMessages\", workshops.\"OutboxMessages\", workshops.\"SeatHolds\", workshops.\"WorkshopAttendees\", workshops.\"Workshops\", " +
            "vouchers.\"InboxMessages\", vouchers.\"OutboxMessages\", vouchers.\"Vouchers\" RESTART IDENTITY CASCADE;");
    }

    public static async Task<Workshop> AddWorkshopAsync(int seatsTotal, long priceCents = 5_000, int? maxPerAttendee = null, int kitsTotal = 0, long kitPriceCents = 0)
    {
        await using var db = Workshops();
        var workshop = new Workshop
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Name = "Test Workshop",
            Track = "Hands-on",
            Instructor = "Ada Lovelace",
            WorkshopDate = DateTimeOffset.UtcNow.AddDays(7),
            PriceCents = priceCents,
            SeatsTotal = seatsTotal,
            SeatsLeft = seatsTotal,
            KitsTotal = kitsTotal,
            KitsLeft = kitsTotal,
            KitPriceCents = kitPriceCents,
            MaxPerAttendee = maxPerAttendee ?? seatsTotal,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Workshops.Add(workshop);
        await db.SaveChangesAsync();
        return workshop;
    }

    public static async Task<Order> AddOrderAsync(Workshop workshop, string reference, int seats, OrderStatus status, Guid? ownerId = null)
    {
        await using var db = Orders();
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
            WorkshopId = workshop.Id,
            WorkshopCode = workshop.Code,
            Reference = reference,
            Seats = seats,
            TotalCents = OrderRules.TotalCents(workshop.PriceCents, seats),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }
}

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

public sealed class FixedCatalog(params Workshop[] workshops) : IWorkshopCatalog
{
    public Task<WorkshopInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(workshops.Where(s => s.Code == code).Select(s => new WorkshopInfo(s.Id, s.Code, s.PriceCents)).FirstOrDefault());
}
