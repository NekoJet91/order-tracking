using FluentValidation;
using OrderTracking.Domain.Orders;

namespace OrderTracking.Api.Contracts.Orders;

/// <summary>
/// Validates <see cref="ChangeOrderStatusRequest"/> before it reaches the handler.
/// </summary>
/// <remarks>
/// Only checks that the value is a defined <see cref="OrderStatus"/>. Whether the
/// transition is <em>permitted</em> depends on the order's current status, which this
/// validator cannot see and should not go to the database to find out; that check belongs
/// to the domain and surfaces as <c>409</c>.
/// </remarks>
public sealed class ChangeOrderStatusRequestValidator : AbstractValidator<ChangeOrderStatusRequest>
{
    /// <summary>Defines the rules.</summary>
    public ChangeOrderStatusRequestValidator()
    {
        RuleFor(request => request.Status).IsInEnum();
    }
}
