using OrderTracking.Domain.Orders;

namespace OrderTracking.UnitTests.Orders;

public sealed class OrderStatusTransitionsTests
{
    /// <summary>
    /// Every ordered pair of statuses with its expected verdict, written out by hand.
    /// </summary>
    /// <remarks>
    /// Deliberately enumerated rather than derived from the production rule table
    /// </remarks>
    public static TheoryData<OrderStatus, OrderStatus, bool> AllTransitions => new()
    {
        { OrderStatus.Created, OrderStatus.Created, false },
        { OrderStatus.Created, OrderStatus.Shipped, true },
        { OrderStatus.Created, OrderStatus.Delivered, false },
        { OrderStatus.Created, OrderStatus.Cancelled, true },

        { OrderStatus.Shipped, OrderStatus.Created, false },
        { OrderStatus.Shipped, OrderStatus.Shipped, false },
        { OrderStatus.Shipped, OrderStatus.Delivered, true },
        { OrderStatus.Shipped, OrderStatus.Cancelled, true },

        { OrderStatus.Delivered, OrderStatus.Created, false },
        { OrderStatus.Delivered, OrderStatus.Shipped, false },
        { OrderStatus.Delivered, OrderStatus.Delivered, false },
        { OrderStatus.Delivered, OrderStatus.Cancelled, false },

        { OrderStatus.Cancelled, OrderStatus.Created, false },
        { OrderStatus.Cancelled, OrderStatus.Shipped, false },
        { OrderStatus.Cancelled, OrderStatus.Delivered, false },
        { OrderStatus.Cancelled, OrderStatus.Cancelled, false }
    };

    [Theory]
    [MemberData(nameof(AllTransitions))]
    public void IsAllowed_matches_the_specified_state_machine(
        OrderStatus from, OrderStatus to, bool expected)
    {
        Assert.Equal(expected, OrderStatusTransitions.IsAllowed(from, to));
    }

    [Theory]
    [InlineData(OrderStatus.Created, false)]
    [InlineData(OrderStatus.Shipped, false)]
    [InlineData(OrderStatus.Delivered, true)]
    [InlineData(OrderStatus.Cancelled, true)]
    public void IsTerminal_identifies_the_end_states(OrderStatus status, bool expected)
    {
        Assert.Equal(expected, OrderStatusTransitions.IsTerminal(status));
    }

    [Fact]
    public void Every_defined_status_has_a_rule()
    {
        // Guards against adding a value to the enum and forgetting the rule table:
        // AllowedFrom throws for anything it does not know about.
        foreach (var status in Enum.GetValues<OrderStatus>())
        {
            var next = OrderStatusTransitions.AllowedFrom(status);
            Assert.NotNull(next);
        }
    }

    [Fact]
    public void AllowedFrom_rejects_a_value_outside_the_enum()
    {
        var undefined = (OrderStatus)999;

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => OrderStatusTransitions.AllowedFrom(undefined));

        Assert.Equal("from", exception.ParamName);
    }

    [Fact]
    public void No_transition_leads_back_into_Created()
    {
        // Created is the entry state: nothing may return to it, or an order could be
        // "un-shipped" and the audit trail would stop making sense.
        foreach (var from in Enum.GetValues<OrderStatus>())
        {
            Assert.DoesNotContain(OrderStatus.Created, OrderStatusTransitions.AllowedFrom(from));
        }
    }
}
