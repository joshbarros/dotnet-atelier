using Atelier.Orders;
using Atelier.Vouchers;
using Atelier.Shared;
using Atelier.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atelier.IntegrationTests;

public class SagaTests
{
    private static OrdersHandler Handler(OrdersDbContext db) => new(db, Options.Create(new OrderOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(5);
        var order = await TestDatabase.AddOrderAsync(workshop, "ATT-POISON-1", 2, OrderStatus.Held);
        var held = new SeatsHeld(order.Id, order.OwnerId, workshop.Id, 2, order.Reference);

        await using var docs = TestDatabase.Vouchers();
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            new VouchersHandler(docs).HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var check = TestDatabase.Vouchers();
        var doc = await check.Vouchers.SingleAsync(d => d.OrderId == order.Id);
        Assert.Equal(VoucherStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(order.OwnerId, doc.OwnerId);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Refund_ThenFinalMark_LeavesOrderRefundedAndVoucherIssued()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(5);
        var owner = Guid.CreateVersion7();
        var order = await TestDatabase.AddOrderAsync(workshop, "ATT-1", 2, OrderStatus.Held, owner);

        // The voucher is issued; the final mark has not landed yet.
        await using (var docs = TestDatabase.Vouchers())
            await new VouchersHandler(docs).IssueAsync(Guid.CreateVersion7(), new SeatsHeld(order.Id, owner, workshop.Id, 2, order.Reference));

        // The owner reverses first.
        await using (var orders = TestDatabase.Orders())
            await new OrderService(orders, new FixedCatalog(workshop), new TestUser(owner)).RefundAsync(order.Id, default);

        await using var late = TestDatabase.Orders();
        var mark = await Handler(late).MarkSoldAsync(Guid.CreateVersion7(), new VoucherIssued(order.Id, "VC-TEST"));

        await using var check = TestDatabase.Orders();
        await using var checkDocs = TestDatabase.Vouchers();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(OrderStatus.Refunded, (await check.Orders.SingleAsync()).Status);
        Assert.Equal(VoucherStatus.Issued, (await checkDocs.Vouchers.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(5);
        var held = await TestDatabase.AddOrderAsync(workshop, "ATT-A", 1, OrderStatus.Held);
        var rejected = await TestDatabase.AddOrderAsync(workshop, "ATT-B", 1, OrderStatus.Rejected);
        var requested = await TestDatabase.AddOrderAsync(workshop, "ATT-C", 1, OrderStatus.Requested);

        await using (var a = TestDatabase.Orders())
            Assert.Equal(MarkOutcome.Marked, await Handler(a).MarkSoldAsync(Guid.CreateVersion7(), new VoucherIssued(held.Id, "VC-1")));
        await using (var b = TestDatabase.Orders())
            Assert.Equal(MarkOutcome.Skipped, await Handler(b).MarkSoldAsync(Guid.CreateVersion7(), new VoucherIssued(rejected.Id, "VC-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await using (var c = TestDatabase.Orders())
            await Assert.ThrowsAsync<OutOfOrderException>(() => Handler(c).MarkSoldAsync(Guid.CreateVersion7(), new VoucherIssued(requested.Id, "VC-3")));

        await using var check = TestDatabase.Orders();
        var statuses = (await check.Orders.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(OrderStatus.Sold, statuses["ATT-A"]);
        Assert.Equal(OrderStatus.Rejected, statuses["ATT-B"]);
        Assert.Equal(OrderStatus.Requested, statuses["ATT-C"]);
    }

    [Fact]
    public async Task Refund_OfARejectedOrder_Conflicts()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(5);
        var owner = Guid.CreateVersion7();
        var order = await TestDatabase.AddOrderAsync(workshop, "ATT-1", 1, OrderStatus.Rejected, owner);

        await using var db = TestDatabase.Orders();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new OrderService(db, new FixedCatalog(workshop), new TestUser(owner)).RefundAsync(order.Id, default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Refund_ByAnotherCustomer_IsNotFound_AndChangesNothing()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(5);
        var order = await TestDatabase.AddOrderAsync(workshop, "ATT-1", 1, OrderStatus.Held);

        await using var db = TestDatabase.Orders();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new OrderService(db, new FixedCatalog(workshop), new TestUser(Guid.NewGuid())).RefundAsync(order.Id, default));

        Assert.Equal(404, ex.Status);
        await using var check = TestDatabase.Orders();
        Assert.Equal(OrderStatus.Held, (await check.Orders.SingleAsync()).Status);
        Assert.Equal(0, await check.Outbox.CountAsync());
    }

    [Fact]
    public async Task Staff_CanRefundAnyonesOrder()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(5);
        var order = await TestDatabase.AddOrderAsync(workshop, "ATT-1", 1, OrderStatus.Held);

        await using var db = TestDatabase.Orders();
        var refunded = await new OrderService(db, new FixedCatalog(workshop), new TestUser(Guid.NewGuid(), staff: true)).RefundAsync(order.Id, default);
        Assert.Equal(OrderStatus.Refunded, refunded.Status);
    }

    [Fact]
    public async Task Create_RecordsTheCallerAsTheOwner_AndQueuesTheReservation()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(5);
        var owner = Guid.CreateVersion7();

        await using var db = TestDatabase.Orders();
        var order = await new OrderService(db, new FixedCatalog(workshop), new TestUser(owner))
            .CreateAsync(new CreateOrderRequest(workshop.Code, "ATT-1", 2), default);

        await using var check = TestDatabase.Orders();
        Assert.Equal(owner, (await check.Orders.SingleAsync()).OwnerId);
        var message = await check.Outbox.SingleAsync();
        Assert.Equal(Topology.ReserveKey, message.RoutingKey);
        Assert.Equal(owner, Json.Parse<ReserveSeats>(message.Payload).OwnerId);
        Assert.Equal(OrderStatus.Requested, order.Status);
    }

    [Fact]
    public async Task Create_WithoutASignedInUser_IsRefused()
    {
        await TestDatabase.OpenAsync();
        var workshop = await TestDatabase.AddWorkshopAsync(5);
        await using var db = TestDatabase.Orders();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new OrderService(db, new FixedCatalog(workshop), new TestUser(null)).CreateAsync(new CreateOrderRequest(workshop.Code, "ATT-1", 1), default));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task Create_ForAnUnknownWorkshop_IsNotFound()
    {
        await TestDatabase.OpenAsync();
        await using var db = TestDatabase.Orders();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new OrderService(db, new FixedCatalog(), new TestUser(Guid.NewGuid())).CreateAsync(new CreateOrderRequest("NOPE", "ATT-1", 1), default));
        Assert.Equal(404, ex.Status);
    }
}
