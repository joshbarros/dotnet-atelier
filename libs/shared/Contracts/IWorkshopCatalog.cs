namespace Atelier.Shared.Contracts;

public record WorkshopInfo(Guid Id, string Code, long PriceCents, long KitPriceCents = 0, int KitsTotal = 0);

/// <summary>
/// What Orders may ask Workshops, without referencing it. Workshops implements it over its own tables;
/// Orders never reads another module's schema.
/// </summary>
public interface IWorkshopCatalog
{
    Task<WorkshopInfo?> FindAsync(string code, CancellationToken ct);
}
