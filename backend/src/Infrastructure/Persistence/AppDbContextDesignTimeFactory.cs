using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pgvector.EntityFrameworkCore;

namespace Backend.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations</c> build the model without booting the host or
/// reaching a live database. The placeholder connection string is never opened when
/// adding or scripting a migration — it only satisfies the Npgsql provider so the
/// model can be constructed at design time.
/// </summary>
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=design_time;Username=design;Password=design",
                o => o.UseVector())
            .Options;

        return new AppDbContext(options);
    }
}
