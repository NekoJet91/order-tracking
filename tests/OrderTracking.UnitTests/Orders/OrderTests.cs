using OrderTracking.Domain.Orders;
using OrderTracking.Domain.Orders.Events;

namespace OrderTracking.UnitTests.Orders;

public sealed class OrderTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static Order NewOrder(string description = "Order under test") =>
        Order.Create("ORD-00000001", description, _now);

    [Fact]
    public void Create_starts_the_order_in_Created()
    {
        var order = Order.Create("ORD-00000001", "Desk lamp, black", _now);

        Assert.Equal("ORD-00000001", order.OrderNumber);
        Assert.Equal("Desk lamp, black", order.Description);
        Assert.Equal(OrderStatus.Created, order.Status);
        Assert.Equal(_now, order.CreatedAt);
        Assert.Equal(_now, order.UpdatedAt);
    }

    [Fact]
    public void Create_records_a_creation_event()
    {
        var order = NewOrder();

        var created = Assert.IsType<OrderCreatedEvent>(Assert.Single(order.DomainEvents));
        Assert.Equal("ORD-00000001", created.OrderNumber);
        Assert.Equal(OrderStatus.Created, created.Status);
        Assert.Equal(_now, created.OccurredAt);
        Assert.NotEqual(Guid.Empty, created.EventId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_order_number(string orderNumber)
    {
        Assert.Throws<ArgumentException>(() => Order.Create(orderNumber, "Desk lamp, black", _now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_description(string description)
    {
        Assert.Throws<ArgumentException>(() => Order.Create("ORD-00000001", description, _now));
    }

    [Fact]
    public void Create_rejects_a_description_that_is_too_long()
    {
        var tooLong = new string('x', Order.DescriptionMaxLength + 1);

        var exception = Assert.Throws<ArgumentException>(
            () => Order.Create("ORD-00000001", tooLong, _now));

        Assert.Equal("description", exception.ParamName);
    }

    [Fact]
    public void Create_accepts_a_description_of_exactly_the_maximum_length()
    {
        // The boundary itself must be legal
        var atLimit = new string('x', Order.DescriptionMaxLength);

        var order = Order.Create("ORD-00000001", atLimit, _now);

        Assert.Equal(atLimit, order.Description);
    }

    [Fact]
    public void ChangeStatus_applies_a_legal_transition_and_moves_the_clock()
    {
        var order = NewOrder();
        order.ClearDomainEvents();
        var later = _now.AddMinutes(30);

        var changed = order.ChangeStatus(OrderStatus.Shipped, later);

        Assert.True(changed);
        Assert.Equal(OrderStatus.Shipped, order.Status);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(_now, order.CreatedAt);
    }

    [Fact]
    public void ChangeStatus_records_both_the_old_and_the_new_status()
    {
        var order = NewOrder();
        order.ClearDomainEvents();

        order.ChangeStatus(OrderStatus.Shipped, _now.AddMinutes(30));

        var changed = Assert.IsType<OrderStatusChangedEvent>(Assert.Single(order.DomainEvents));
        Assert.Equal(OrderStatus.Created, changed.OldStatus);
        Assert.Equal(OrderStatus.Shipped, changed.NewStatus);
        Assert.Equal("ORD-00000001", changed.OrderNumber);
    }

    [Fact]
    public void ChangeStatus_to_the_current_status_is_a_no_op()
    {
        // A client retrying after a timeout must succeed rather than receive a conflict,
        // and must not cause a second event to be published.
        var order = NewOrder();
        order.ClearDomainEvents();

        var changed = order.ChangeStatus(OrderStatus.Created, _now.AddHours(1));

        Assert.False(changed);
        Assert.Equal(OrderStatus.Created, order.Status);
        Assert.Equal(_now, order.UpdatedAt);
        Assert.Empty(order.DomainEvents);
    }

    [Fact]
    public void ChangeStatus_rejects_an_illegal_transition()
    {
        var order = NewOrder();

        var exception = Assert.Throws<InvalidOrderStatusTransitionException>(
            () => order.ChangeStatus(OrderStatus.Delivered, _now.AddHours(1)));

        Assert.Equal(OrderStatus.Created, exception.From);
        Assert.Equal(OrderStatus.Delivered, exception.To);
    }

    [Fact]
    public void ChangeStatus_leaves_the_order_untouched_when_it_rejects()
    {
        var order = NewOrder();
        order.ClearDomainEvents();

        Assert.Throws<InvalidOrderStatusTransitionException>(
            () => order.ChangeStatus(OrderStatus.Delivered, _now.AddHours(1)));

        Assert.Equal(OrderStatus.Created, order.Status);
        Assert.Equal(_now, order.UpdatedAt);
        Assert.Empty(order.DomainEvents);
    }

    [Fact]
    public void ChangeStatus_rejects_a_value_outside_the_enum()
    {
        var order = NewOrder();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => order.ChangeStatus((OrderStatus)999, _now.AddHours(1)));
    }

    [Fact]
    public void A_delivered_order_cannot_be_cancelled()
    {
        var order = NewOrder();
        order.ChangeStatus(OrderStatus.Shipped, _now.AddMinutes(10));
        order.ChangeStatus(OrderStatus.Delivered, _now.AddMinutes(20));

        Assert.Throws<InvalidOrderStatusTransitionException>(
            () => order.ChangeStatus(OrderStatus.Cancelled, _now.AddMinutes(30)));
    }

    [Fact]
    public void Events_accumulate_across_several_changes_until_cleared()
    {
        var order = NewOrder();

        order.ChangeStatus(OrderStatus.Shipped, _now.AddMinutes(10));
        order.ChangeStatus(OrderStatus.Delivered, _now.AddMinutes(20));

        Assert.Equal(3, order.DomainEvents.Count);

        order.ClearDomainEvents();

        Assert.Empty(order.DomainEvents);
    }
}
