using System.Text.Json;
using Warehouse.Api.Data;
using Warehouse.Api.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Warehouse.Api.Modules.Orders;

[ApiController]
[Route("api/orders")]
public class OrdersController(WarehouseDbContext db, OrderService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the order. The stock hold and the pick slip follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<OrderDto>> Create(CreateOrderRequest request, CancellationToken ct)
    {
        var order = await service.CreateAsync(request, ct);
        return Accepted($"/api/orders/{order.Id}", OrderDto.From(order));
    }

    [HttpGet]
    public async Task<IEnumerable<OrderDto>> List([FromQuery] string? skuCode, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(skuCode))
            query = query.Where(b => b.SkuCode == skuCode);
        return (await query.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct)).Select(OrderDto.From);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderDto>> Get(Guid id, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        return order is null ? NotFound() : OrderDto.From(order);
    }

    [HttpPost("{id:guid}/cancellation")]
    public async Task<ActionResult<OrderDto>> Cancel(Guid id, CancellationToken ct) =>
        Accepted($"/api/orders/{id}", OrderDto.From(await service.CancelAsync(id, ct)));

    /// <summary>The Kafka event log for one order, read from the start of the topic.</summary>
    [HttpGet("{id:guid}/events")]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
