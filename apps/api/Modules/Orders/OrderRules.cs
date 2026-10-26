namespace Warehouse.Api.Modules.Orders;

public static class OrderRules
{
    public const int MaxUnitsPerOrder = 50;
    public const int MaxReferenceLength = 40;
    public const string PoisonToken = "POISON";
    public const long FeeCentsPerUnit = 150;
    public const long ShippingFeeCents = 400;

    /// <summary>Returns an error message, or null when the request is acceptable.</summary>
    public static string? Validate(string? reference, long units)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "Order reference is required.";
        if (reference.Trim().Length > MaxReferenceLength)
            return $"Order reference must be at most {MaxReferenceLength} characters.";
        if (units < 1)
            return "An order needs at least 1 unit.";
        // Compared as long on purpose: narrowing 4294967297 to int would wrap to 1 and pass.
        if (units > MaxUnitsPerOrder)
            return $"An order takes at most {MaxUnitsPerOrder} units.";
        return null;
    }

    /// <summary>Case-sensitive substring match: a longer token that merely contains the poison matches too.</summary>
    public static bool IsPoison(string reference) => reference.Contains(PoisonToken, StringComparison.Ordinal);

    /// <summary>Handling per unit plus a flat shipping fee, in cents. Overflow throws instead of wrapping.</summary>
    public static long FeeCents(int units) => checked(units * FeeCentsPerUnit + ShippingFeeCents);
}
