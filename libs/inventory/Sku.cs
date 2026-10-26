namespace Warehouse.Inventory;

public class Sku
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Bin { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public int ReceivedUnits { get; set; }
    public int OnHandUnits { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum HoldStatus { Held, Released }

public class StockHold
{
    public Guid OrderId { get; set; }
    public Guid SkuId { get; set; }
    public int Units { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}
