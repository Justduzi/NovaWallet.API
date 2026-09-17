using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NovaWallet.Repositories;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NovaWalletDbContext>
{
    public NovaWalletDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<NovaWalletDbContext>()
            .UseSqlServer("Server=localhost;Database=NovaWallet;Integrated Security=true;TrustServerCertificate=true")
            .Options);
}
