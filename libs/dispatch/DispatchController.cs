using Warehouse.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Warehouse.Dispatch;

public record PickSlipDto(Guid OrderId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/slips")]
[Authorize]
public class DispatchController(DispatchDbContext db, ICurrentUser user) : ControllerBase
{
    /// <summary>The owner or staff only; anyone else gets a 404.</summary>
    [HttpGet("{orderId:guid}")]
    public async Task<ActionResult<PickSlipDto>> Get(Guid orderId, CancellationToken ct)
    {
        var doc = await db.PickSlips.AsNoTracking().FirstOrDefaultAsync(d => d.OrderId == orderId, ct);
        return doc is null || !OwnerAccess.CanAccess(user, doc.OwnerId)
            ? NotFound()
            : new PickSlipDto(doc.OrderId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
