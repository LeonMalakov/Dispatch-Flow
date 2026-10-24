using System.Data;
using DispatchFlow.Application.Orders;
using DispatchFlow.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DispatchFlow.Infrastructure.Persistence;

public sealed class EfOrderRepository(AppDbContext dbContext) : IOrderRepository {
    public async Task<CreateOrderResult> CreateWithReservationAsync(Order order, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(order);

        if (order.IdempotencyKey is not null) {
            return await CreateIdempotentAsync(order, ct);
        }

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

    private async Task<CreateOrderResult> CreateIdempotentAsync(Order order, CancellationToken ct) {
        var existing = await FindByKeyAsync(order.IdempotencyKey!, ct);
        if (existing is not null) {
            return ResolveExisting(order, existing);
        }

        await using (var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)) {
            bool committed = false;
            try {
                if (!await dbContext.DeliverySlots.AnyAsync(slot => slot.Id == order.SlotId, ct)) {
                    return CreateOrderResult.SlotNotFound();
                }

                dbContext.Orders.Add(order);
                await dbContext.SaveChangesAsync(ct);

                int updatedRows = await dbContext.DeliverySlots
                    .Where(slot => slot.Id == order.SlotId && slot.Capacity - slot.Reserved >= order.Quantity)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(slot => slot.Reserved, slot => slot.Reserved + order.Quantity), ct);
                if (updatedRows == 0) {
                    return CreateOrderResult.InsufficientCapacity();
                }

                await transaction.CommitAsync(ct);
                committed = true;
                return CreateOrderResult.Created(order);
            } catch (DbUpdateException exception) when (exception.InnerException is PostgresException {
                SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: OrderConfiguration.IDEMPOTENCY_KEY_INDEX
            }) {
                await transaction.RollbackAsync(ct);
            } catch (DbUpdateException exception) when (exception.InnerException is PostgresException {
                SqlState: PostgresErrorCodes.ForeignKeyViolation, ConstraintName: OrderConfiguration.SLOT_FOREIGN_KEY
            }) {
                // The slot can disappear between the existence check and the insert.
                await transaction.RollbackAsync(ct);
                return CreateOrderResult.SlotNotFound();
            } finally {
                if (!committed) {
                    dbContext.Entry(order).State = EntityState.Detached;
                }
            }
        }

        var winner = await FindByKeyAsync(order.IdempotencyKey!, ct)
            ?? throw new InvalidOperationException("The order owning the idempotency key could not be found.");
        return ResolveExisting(order, winner);
    }

    private Task<Order?> FindByKeyAsync(string key, CancellationToken ct) =>
        dbContext.Orders.AsNoTracking().SingleOrDefaultAsync(order => order.IdempotencyKey == key, ct);

    private static CreateOrderResult ResolveExisting(Order requested, Order existing) =>
        requested.SlotId == existing.SlotId && requested.Quantity == existing.Quantity
            ? CreateOrderResult.Replayed(existing)
            : CreateOrderResult.IdempotencyConflict();
}
