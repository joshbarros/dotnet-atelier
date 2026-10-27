namespace Atelier.Orders;

public static class OrderRules
{
    public const int MaxSeatsPerOrder = 6;
    public const int MaxReferenceLength = 40;
    public const long ServiceFeeBasisPoints = 1_200;

    /// <summary>Returns an error message, or null when the request is acceptable.</summary>
    public static string? Validate(string? reference, long seats)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "Attendee reference is required.";
        if (reference.Trim().Length > MaxReferenceLength)
            return $"Attendee reference must be at most {MaxReferenceLength} characters.";
        if (seats < 1)
            return "An order needs at least 1 seat.";
        // Compared as long on purpose: narrowing 4294967297 to int would wrap to 1 and pass.
        if (seats > MaxSeatsPerOrder)
            return $"An order takes at most {MaxSeatsPerOrder} seats.";
        return null;
    }

    /// <summary>Face value plus a 12% service fee truncated to a whole cent, in cents. Overflow throws instead of wrapping.</summary>
    public static long TotalCents(long priceCents, int seats)
    {
        var face = checked(priceCents * seats);
        return checked(face + face * ServiceFeeBasisPoints / 10_000);
    }
}
