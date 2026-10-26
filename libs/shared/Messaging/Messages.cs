using System.Text.Json;

namespace Warehouse.Shared.Messaging;

public record ReserveStock(Guid OrderId, Guid OwnerId, Guid SkuId, int Units, string Reference);
public record StockHeld(Guid OrderId, Guid OwnerId, Guid SkuId, int Units, string Reference);
public record StockShort(Guid OrderId, string Reference, int RequestedUnits, int OnHandUnits);
public record ReleaseStock(Guid OrderId, string Reason);
public record StockReleased(Guid OrderId, int Units, string Reason);
public record SlipIssued(Guid OrderId, string Number);
public record SlipRejected(Guid OrderId, string Reason);

public record Delivery(Guid MessageId, string RoutingKey, string Body);

public interface IMessageHandler
{
    Task HandleAsync(Delivery delivery, CancellationToken ct);
}

/// <summary>Committed as rejected; the message must be dead-lettered, not retried.</summary>
public class PoisonMessageException(string message) : Exception(message);

/// <summary>The message arrived before the state it depends on; retry later.</summary>
public class OutOfOrderException(string message) : Exception(message);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidOperationException("Empty message body.");
}
