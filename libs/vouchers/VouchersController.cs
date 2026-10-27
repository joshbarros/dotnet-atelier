using Atelier.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Atelier.Vouchers;

public record VoucherDto(Guid OrderId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/vouchers")]
[Authorize]
public class VouchersController(VouchersDbContext db, ICurrentUser user) : ControllerBase
{
    /// <summary>The owner or staff only; anyone else gets a 404.</summary>
    [HttpGet("{orderId:guid}")]
    public async Task<ActionResult<VoucherDto>> Get(Guid orderId, CancellationToken ct)
    {
        var doc = await db.Vouchers.AsNoTracking().FirstOrDefaultAsync(d => d.OrderId == orderId, ct);
        return doc is null || !OwnerAccess.CanAccess(user, doc.OwnerId)
            ? NotFound()
            : new VoucherDto(doc.OrderId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
