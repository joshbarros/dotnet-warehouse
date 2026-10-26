namespace Warehouse.Orders;

public enum OrderStatus { Requested, Held, Dispatched, Cancelled, Rejected }

public class Order
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid SkuId { get; set; }
    public string SkuCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Units { get; set; }
    public long FeeCents { get; set; }
    public OrderStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? SlipNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? UnitsReleasedAt { get; set; }
}

public class OrderOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the cancellation race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public record CreateOrderRequest(string? SkuCode, string? Reference, long Units);

public record OrderDto(
    Guid Id, Guid OwnerId, string SkuCode, string Reference, int Units, long FeeCents, string Status,
    string? Reason, string? SlipNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? UnitsReleasedAt)
{
    public static OrderDto From(Order b) => new(
        b.Id, b.OwnerId, b.SkuCode, b.Reference, b.Units, b.FeeCents, b.Status.ToString(),
        b.Reason, b.SlipNumber, b.CreatedAt, b.UpdatedAt, b.UnitsReleasedAt);
}
