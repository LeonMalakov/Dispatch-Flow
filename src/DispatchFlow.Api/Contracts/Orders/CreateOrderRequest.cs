using System.ComponentModel.DataAnnotations;

namespace DispatchFlow.Api.Contracts.Orders;

public sealed class CreateOrderRequest : IValidatableObject {
    [Required]
    public Guid? SlotId { get; init; }

    [Required]
    [Range(1, int.MaxValue)]
    public int? Quantity { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) {
        if (SlotId == Guid.Empty) {
            yield return new ValidationResult("The delivery slot ID must not be empty.", [nameof(SlotId)]);
        }
    }
}
