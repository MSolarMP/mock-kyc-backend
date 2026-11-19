using Kyc.Infrastructure.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Kyc.Infrastructure.Persistence;

// Only used by `dotnet ef` for migrations.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<KycDbContext>
{
    public KycDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<KycDbContext>()
            .UseNpgsql("Host=localhost;Database=kyc;Username=kyc;Password=kyc")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new KycDbContext(options, new RequestContext(), TimeProvider.System);
    }
}
