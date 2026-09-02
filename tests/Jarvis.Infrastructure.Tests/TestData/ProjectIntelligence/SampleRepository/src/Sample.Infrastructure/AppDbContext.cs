using Microsoft.EntityFrameworkCore;
using Sample.Domain;

namespace Sample.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}
