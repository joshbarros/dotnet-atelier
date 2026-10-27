using Atelier.Shared;
using Atelier.Shared.Auth;
using Atelier.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Atelier.Workshops;

public record CreateWorkshopRequest(string? Code, string? Name, string? Track, string? Instructor, DateTimeOffset? WorkshopDate, long SeatsTotal, long PriceCents, long? MaxPerAttendee = null, long? KitsTotal = null, long? KitPriceCents = null);

public record WorkshopDto(Guid Id, string Code, string Name, string Track, string Instructor, DateTimeOffset WorkshopDate, long PriceCents, int SeatsTotal, int SeatsLeft, int MaxPerAttendee, int KitsTotal, int KitsLeft, long KitPriceCents)
{
    public static WorkshopDto From(Workshop s) => new(s.Id, s.Code, s.Name, s.Track, s.Instructor, s.WorkshopDate, s.PriceCents, s.SeatsTotal, s.SeatsLeft, s.MaxPerAttendee, s.KitsTotal, s.KitsLeft, s.KitPriceCents);
}

/// <summary>What other modules may ask Workshops, over Workshops's own tables.</summary>
public sealed class WorkshopCatalog(WorkshopsDbContext db) : IWorkshopCatalog
{
    public async Task<WorkshopInfo?> FindAsync(string code, CancellationToken ct) =>
        await db.Workshops.AsNoTracking().Where(s => s.Code == code).Select(s => new WorkshopInfo(s.Id, s.Code, s.PriceCents, s.KitPriceCents, s.KitsTotal)).FirstOrDefaultAsync(ct);
}

[ApiController]
[Route("api/workshops")]
[Authorize]
public class WorkshopsController(WorkshopsDbContext db) : ControllerBase
{
    private const int MaxSeatsTotal = 5000;
    private const long MaxPriceCents = 1_000_000;

    [HttpGet]
    public async Task<IEnumerable<WorkshopDto>> List(CancellationToken ct) =>
        (await db.Workshops.AsNoTracking().OrderBy(s => s.WorkshopDate).ThenBy(s => s.Code).ToListAsync(ct)).Select(WorkshopDto.From);

    [HttpGet("{code}")]
    public async Task<ActionResult<WorkshopDto>> Get(string code, CancellationToken ct)
    {
        var workshop = await db.Workshops.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        return workshop is null ? NotFound() : WorkshopDto.From(workshop);
    }

    /// <summary>Opens a workshop. Staff only. This is the only place SeatsLeft is initialised; after that only the Workshops handler writes it.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ManageCatalog)]
    public async Task<ActionResult<WorkshopDto>> Create(CreateWorkshopRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Track) || string.IsNullOrWhiteSpace(request.Instructor))
            throw new DomainException(400, "Name, track and instructor are required.");
        if (request.SeatsTotal < 1 || request.SeatsTotal > MaxSeatsTotal)
            throw new DomainException(400, $"Seats on sale must be between 1 and {MaxSeatsTotal} seats.");
        if (request.PriceCents < 1 || request.PriceCents > MaxPriceCents)
            throw new DomainException(400, $"Price must be between 1 and {MaxPriceCents} cents per seat.");
        var kits = request.KitsTotal ?? 0;
        if (kits < 0 || kits > MaxSeatsTotal)
            throw new DomainException(400, $"Kits must be between 0 and {MaxSeatsTotal}.");
        var kitPrice = request.KitPriceCents ?? 0;
        if (kitPrice < 0 || kitPrice > MaxPriceCents || (kits > 0 && kitPrice < 1))
            throw new DomainException(400, "A kit needs a price between 1 and the seat price limit when kits are offered.");
        var cap = request.MaxPerAttendee ?? Math.Min(4, request.SeatsTotal);
        if (cap < 1 || cap > request.SeatsTotal)
            throw new DomainException(400, "Per-attendee cap must be at least 1 and no more than the seats on sale.");
        if (await db.Workshops.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(409, $"Workshop '{code}' already exists.");

        var workshop = new Workshop
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = request.Name.Trim(),
            Track = request.Track.Trim(),
            Instructor = request.Instructor.Trim(),
            WorkshopDate = request.WorkshopDate ?? DateTimeOffset.UtcNow.AddDays(14),
            PriceCents = request.PriceCents,
            KitsTotal = (int)kits,
            KitsLeft = (int)kits,
            KitPriceCents = kitPrice,
            SeatsTotal = (int)request.SeatsTotal,
            SeatsLeft = (int)request.SeatsTotal,
            MaxPerAttendee = (int)cap,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Workshops.Add(workshop);
        await db.SaveChangesAsync(ct);
        return Created($"/api/workshops/{workshop.Code}", WorkshopDto.From(workshop));
    }
}
