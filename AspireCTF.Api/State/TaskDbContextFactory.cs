using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AspireCTF.Api.State;

public sealed class TaskDbContextFactory : IDesignTimeDbContextFactory<TaskDbContext>
{
    public TaskDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TaskDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=ctfdb;Username=ctf;Password=ctf")
            .Options;
        return new TaskDbContext(options);
    }
}
