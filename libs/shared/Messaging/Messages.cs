using System.Text.Json;

namespace Atelier.Shared.Messaging;

/// <summary>Kit: the attendee also needs a laptop kit for every seat; seats and kits are taken together or not at all.</summary>
public record ReserveSeats(Guid OrderId, Guid OwnerId, Guid WorkshopId, int Seats, string Reference, bool Kit = false);
public record SeatsHeld(Guid OrderId, Guid OwnerId, Guid WorkshopId, int Seats, string Reference);
public record SeatsShort(Guid OrderId, string Reference, int RequestedSeats, int SeatsLeft, string Reason = "stock", int PerAttendeeLimit = 0, int KitsLeft = 0);
public record ReleaseSeats(Guid OrderId, string Reason);
public record SeatsReleased(Guid OrderId, int Seats, string Reason);
public record VoucherIssued(Guid OrderId, string Number);
public record VoucherRejected(Guid OrderId, string Reason);

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
