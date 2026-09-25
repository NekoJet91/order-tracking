namespace OrderTracking.Domain.Orders;

/// <summary>
/// Lifecycle state of an <see cref="Order"/>.
/// </summary>
/// <remarks>
/// Values start at 1 rather than 0 so that <c>default(OrderStatus)</c> is not valid
/// </remarks>
public enum OrderStatus
{
    /// <summary>Placed but not yet dispatched. In the specification: «Создан».</summary>
    Created = 1,

    /// <summary>Dispatched to the customer. In the specification: «Отправлен».</summary>
    Shipped = 2,

    /// <summary>Received by the customer. Terminal. In the specification: «Доставлен».</summary>
    Delivered = 3,

    /// <summary>Cancelled before delivery. Terminal. In the specification: «Отменён».</summary>
    Cancelled = 4
}
