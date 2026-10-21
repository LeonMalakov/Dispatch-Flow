using DispatchFlow.Application.DeliverySlots;
using Microsoft.EntityFrameworkCore;

namespace DispatchFlow.Infrastructure.Persistence;

public static class DevelopmentDataSeeder {
    private static readonly Guid MorningSlotId = new("d1010000-0000-0000-0000-000000000001");
    private static readonly Guid AfternoonSlotId = new("d1010000-0000-0000-0000-000000000002");

    public static async Task<int> SeedAsync(AppDbContext dbContext, CancellationToken cancellationToken = default) {
        var tomorrowUtc = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1), TimeSpan.Zero);
        var morning = new DeliverySlot(MorningSlotId, 20, tomorrowUtc.AddHours(8), tomorrowUtc.AddHours(12));
        var afternoon = new DeliverySlot(AfternoonSlotId, 30, tomorrowUtc.AddHours(12), tomorrowUtc.AddHours(16));

        // Stable IDs and DO NOTHING preserve existing slots, including their times and reservations.
        return await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO delivery_slots (id, capacity, reserved, starts_at_utc, ends_at_utc)
            VALUES ({morning.Id}, {morning.Capacity}, {morning.Reserved}, {morning.StartsAtUtc}, {morning.EndsAtUtc}),
                   ({afternoon.Id}, {afternoon.Capacity}, {afternoon.Reserved}, {afternoon.StartsAtUtc}, {afternoon.EndsAtUtc})
            ON CONFLICT (id) DO NOTHING;
            """, cancellationToken);
    }
}