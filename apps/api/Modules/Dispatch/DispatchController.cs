using Warehouse.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Warehouse.Api.Modules.Dispatch;

public record PickSlipDto(Guid OrderId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/slips")]
public class DispatchController(WarehouseDbContext db) : ControllerBase
{
    [HttpGet("{orderId:guid}")]
    public async Task<ActionResult<PickSlipDto>> Get(Guid orderId, CancellationToken ct)
    {
        var doc = await db.Slips.AsNoTracking().FirstOrDefaultAsync(d => d.OrderId == orderId, ct);
        return doc is null
            ? NotFound()
            : new PickSlipDto(doc.OrderId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
