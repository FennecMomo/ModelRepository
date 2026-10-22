using Microsoft.EntityFrameworkCore;

namespace ModuleRepository.Web.Persistence;

public sealed class RepositoryDb(DbContextOptions<RepositoryDb> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccessToken> AccessTokens => Set<AccessToken>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Module> Modules => Set<Module>();
    public DbSet<ModuleGrant> Grants => Set<ModuleGrant>();
    public DbSet<ModuleFile> Files => Set<ModuleFile>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Account>().HasIndex(x => x.NormalizedUsername).IsUnique();
        model.Entity<AccessToken>().HasIndex(x => x.Hash).IsUnique();
        model.Entity<AccessToken>().HasOne<Account>().WithMany().HasForeignKey(x => x.UserId);
        model.Entity<Category>().HasOne<Category>().WithMany().HasForeignKey(x => x.ParentId)
            .OnDelete(DeleteBehavior.Restrict);
        model.Entity<Module>().HasOne<Account>().WithMany().HasForeignKey(x => x.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);
        model.Entity<Module>().HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
        model.Entity<ModuleGrant>().HasKey(x => new { x.ModuleId, x.UserId });
        model.Entity<ModuleGrant>().HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId);
        model.Entity<ModuleGrant>().HasOne<Account>().WithMany().HasForeignKey(x => x.UserId);
        // Paths are normalized and compared case-insensitively for safe Windows consumption.
        model.Entity<ModuleFile>().HasKey(x => new { x.ModuleId, x.Kind, x.PathKey });
        model.Entity<ModuleFile>().HasOne<Module>().WithMany().HasForeignKey(x => x.ModuleId);
    }
}

public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = "";
    public string NormalizedUsername { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public bool IsAdmin { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class AccessToken
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Category
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? ParentId { get; set; }
}

public sealed class Module
{
    public string Id { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string? CategoryId { get; set; }
    public bool Enabled { get; set; } = true;
    public string DraftJson { get; set; } = "{}";
    public long DraftRevision { get; set; } = 1;
    public string? PublishedJson { get; set; }
    public string? PublishedVersion { get; set; }
    public string? ChangeNotes { get; set; }
    public long Revision { get; set; }
    public string? ContentHash { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ModuleGrant
{
    public string ModuleId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Role { get; set; } = "reader";
}

public sealed class ModuleFile
{
    public string ModuleId { get; set; } = "";
    public string Kind { get; set; } = "draft";
    public string Path { get; set; } = "";
    public string PathKey { get; set; } = "";
    public string Hash { get; set; } = "";
    public long Size { get; set; }
}
