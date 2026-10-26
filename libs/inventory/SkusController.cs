using Warehouse.Shared;
using Warehouse.Shared.Auth;
using Warehouse.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Warehouse.Inventory;

public record CreateSkuRequest(string? Code, string? Name, string? Category, string? Bin, DateTimeOffset? ReceivedAt, long ReceivedUnits);

public record SkuDto(Guid Id, string Code, string Name, string Category, string Bin, DateTimeOffset ReceivedAt, int ReceivedUnits, int OnHandUnits)
{
    public static SkuDto From(Sku s) => new(s.Id, s.Code, s.Name, s.Category, s.Bin, s.ReceivedAt, s.ReceivedUnits, s.OnHandUnits);
}

/// <summary>What other modules may ask Inventory, over Inventory's own tables.</summary>
public sealed class SkuCatalog(InventoryDbContext db) : ISkuCatalog
{
    public async Task<SkuInfo?> FindAsync(string code, CancellationToken ct) =>
        await db.Skus.AsNoTracking().Where(s => s.Code == code).Select(s => new SkuInfo(s.Id, s.Code)).FirstOrDefaultAsync(ct);
}

[ApiController]
[Route("api/skus")]
[Authorize]
public class SkusController(InventoryDbContext db) : ControllerBase
{
    private const int MaxReceivedUnits = 100_000;

    [HttpGet]
    public async Task<IEnumerable<SkuDto>> List(CancellationToken ct) =>
        (await db.Skus.AsNoTracking().OrderBy(s => s.ReceivedAt).ThenBy(s => s.Code).ToListAsync(ct)).Select(SkuDto.From);

    [HttpGet("{code}")]
    public async Task<ActionResult<SkuDto>> Get(string code, CancellationToken ct)
    {
        var sku = await db.Skus.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        return sku is null ? NotFound() : SkuDto.From(sku);
    }

    /// <summary>Opens a sku. Staff only. This is the only place OnHandUnits is initialised; after that only the Inventory handler writes it.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ManageCatalog)]
    public async Task<ActionResult<SkuDto>> Create(CreateSkuRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Category) || string.IsNullOrWhiteSpace(request.Bin))
            throw new DomainException(400, "Name, category and bin are required.");
        if (request.ReceivedUnits < 1 || request.ReceivedUnits > MaxReceivedUnits)
            throw new DomainException(400, $"Units received must be between 1 and {MaxReceivedUnits} units.");
        if (await db.Skus.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(409, $"Sku '{code}' already exists.");

        var sku = new Sku
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = request.Name.Trim(),
            Category = request.Category.Trim(),
            Bin = request.Bin.Trim(),
            ReceivedAt = request.ReceivedAt ?? DateTimeOffset.UtcNow.AddDays(14),
            ReceivedUnits = (int)request.ReceivedUnits,
            OnHandUnits = (int)request.ReceivedUnits,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Skus.Add(sku);
        await db.SaveChangesAsync(ct);
        return Created($"/api/skus/{sku.Code}", SkuDto.From(sku));
    }
}
