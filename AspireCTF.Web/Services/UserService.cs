using System.Security.Cryptography;
using AspireCTF.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace AspireCTF.Web.Services;

public sealed class UserService(IDbContextFactory<UserDbContext> factory)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
        if (!await db.Users.AnyAsync(ct))
        {
            db.Users.Add(new AppUser { Username = "admin", PasswordHash = Hash("admin"), IsAdmin = true });
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<AppUser?> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Username == username, ct);
        return user is not null && Verify(password, user.PasswordHash) ? user : null;
    }

    public async Task<List<AppUser>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().OrderBy(x => x.Username).ToListAsync(ct);
    }

    public async Task<bool> SetPasswordAsync(string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password)) return false;
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Username == username, ct);
        if (user is null) return false;
        user.PasswordHash = Hash(password);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> CreateAsync(string username, string password, bool admin, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return false;
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Users.AnyAsync(x => x.Username == username, ct)) return false;
        db.Users.Add(new AppUser { Username = username.Trim(), PasswordHash = Hash(password), IsAdmin = admin });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(string username, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Username == username, ct);
        if (user is null || user.IsAdmin) return false;
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string Hash(string value)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(value, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return $"v1${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool Verify(string value, string encoded)
    {
        var parts = encoded.Split('$');
        if (parts.Length != 3) return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(value, salt, 120_000, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
