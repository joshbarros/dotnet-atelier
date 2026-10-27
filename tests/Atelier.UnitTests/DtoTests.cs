using Atelier.Orders;
using Atelier.Workshops;
using Xunit;

namespace Atelier.UnitTests;

public class DtoTests
{
    [Fact]
    public void OrderDto_MapsEveryFieldAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            WorkshopId = Guid.NewGuid(),
            WorkshopCode = "S-1",
            Reference = "ATT-1",
            Seats = 3,
            TotalCents = 277_500,
            Status = OrderStatus.Sold,
            VoucherNumber = "VC-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = OrderDto.From(order);

        Assert.Equal(order.Id, dto.Id);
        Assert.Equal(order.OwnerId, dto.OwnerId);
        Assert.Equal("Sold", dto.Status);
        Assert.Equal(277_500, dto.TotalCents);
        Assert.Equal("VC-1", dto.VoucherNumber);
    }

    [Fact]
    public void WorkshopDto_MapsTheScarceNumber()
    {
        var workshop = new Workshop { Id = Guid.NewGuid(), Code = "S-1", Name = "v", Track = "o", Instructor = "d", SeatsTotal = 10, SeatsLeft = 4, MaxPerAttendee = 3 };
        var dto = WorkshopDto.From(workshop);
        Assert.Equal(10, dto.SeatsTotal);
        Assert.Equal(3, dto.MaxPerAttendee);
        Assert.Equal(4, dto.SeatsLeft);
    }
}
