namespace Warehouse.Shared.Contracts;

public record SkuInfo(Guid Id, string Code);

/// <summary>
/// What Orders may ask Inventory, without referencing it. Inventory implements it over its own tables;
/// Orders never reads another module's schema.
/// </summary>
public interface ISkuCatalog
{
    Task<SkuInfo?> FindAsync(string code, CancellationToken ct);
}
