using System.Data;
using DispatchFlow.Application.Orders;
using Microsoft.EntityFrameworkCore;

namespace DispatchFlow.Infrastructure.Persistence;

public sealed class EfOrderRepository(AppDbContext dbContext) : IOrderRepository {
    public async Task<CreateOrderResult> CreateWithReservationAsync(Order order, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(order);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        int updatedRows = await dbContext.DeliverySlots
            .Where(slot => slot.Id == order.SlotId && slot.Capacity - slot.Reserved >= order.Quantity)
            .ExecuteUpdateAsync(setters => setters.SetProperty(slot => slot.Reserved, slot => slot.Reserved + order.Quantity),
                ct);

        if (updatedRows == 0) {
            bool slotExists = await dbContext.DeliverySlots.AnyAsync(slot => slot.Id == order.SlotId, ct);
            return slotExists ? CreateOrderResult.InsufficientCapacity() : CreateOrderResult.SlotNotFound();
        }

        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return CreateOrderResult.Created(order);
    }

    public Task<Order?> GetByIdAsync(Guid orderId, CancellationToken ct = default) =>
        dbContext.Orders.AsNoTracking().SingleOrDefaultAsync(order => order.Id == orderId, ct);
}
