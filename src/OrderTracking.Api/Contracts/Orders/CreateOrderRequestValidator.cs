using FluentValidation;
using OrderTracking.Domain.Orders;

namespace OrderTracking.Api.Contracts.Orders;

/// <summary>
/// Validates <see cref="CreateOrderRequest"/> before it reaches the handler.
/// </summary>
/// <remarks>
/// These rules duplicate the guards inside <see cref="Order.Create"/> on purpose, and
/// neither copy is redundant. The domain must reject bad input whoever calls it — a
/// background job, a test, a future gRPC endpoint — and it does so by throwing, which is
/// right for an invariant violation but produces a poor HTTP response. This validator
/// exists to turn the same rules into a <c>400</c> that names the offending field, before
/// an order number is drawn from the sequence and wasted.
/// </remarks>
public sealed class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    /// <summary>Defines the rules.</summary>
    public CreateOrderRequestValidator()
    {
        RuleFor(request => request.Description)
            .NotEmpty()
            .MaximumLength(Order.DescriptionMaxLength);
    }
}
