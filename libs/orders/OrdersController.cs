using System.Text.Json;
using Warehouse.Shared.Auth;
using Warehouse.Shared.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Warehouse.Orders;

[ApiController]
[Route("api/orders")]
[Authorize]
public class OrdersController(OrderService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the order for the signed-in user. Inventory and the pick slip follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<OrderDto>> Create(CreateOrderRequest request, CancellationToken ct)
    {
        var order = await service.CreateAsync(request, ct);
        return Accepted($"/api/orders/{order.Id}", OrderDto.From(order));
    }

    /// <summary>Your orders. Staff see everyone's.</summary>
    [HttpGet]
    public async Task<IEnumerable<OrderDto>> List([FromQuery] string? skuCode, CancellationToken ct) =>
        (await service.ListAsync(skuCode, ct)).Select(OrderDto.From);

    [HttpGet("{id:guid}")]
    public async Task<OrderDto> Get(Guid id, CancellationToken ct) => OrderDto.From(await service.GetAsync(id, ct));

    [HttpPost("{id:guid}/cancellation")]
    public async Task<ActionResult<OrderDto>> Cancel(Guid id, CancellationToken ct) =>
        Accepted($"/api/orders/{id}", OrderDto.From(await service.CancelAsync(id, ct)));

    /// <summary>The Kafka event log for one order, read from the start of the topic. Staff only.</summary>
    [HttpGet("{id:guid}/events")]
    [Authorize(Policy = Policies.ViewAll)]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
