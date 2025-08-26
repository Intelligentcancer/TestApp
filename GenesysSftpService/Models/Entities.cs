using System;
using Microsoft.EntityFrameworkCore;

namespace GenesysSftpService.Models;

public class GenesysConversation
{
    public int Id { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public DateTime ConversationEnd { get; set; }
    public bool IsPosted { get; set; }
    public string? OtherPropertiesJson { get; set; }
}

public class GenesysConvDiv
{
    public int Id { get; set; }
    public string DivisionId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<GenesysConversation> GenesysConversations => Set<GenesysConversation>();
    public DbSet<GenesysConvDiv> GenesysConvDivs => Set<GenesysConvDiv>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GenesysConversation>(entity =>
        {
            entity.ToTable("GenesysConversations");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ConversationId).IsUnique(false);
        });

        modelBuilder.Entity<GenesysConvDiv>(entity =>
        {
            entity.ToTable("GenesysConvDivs");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.DivisionId, e.ConversationId }).IsUnique(false);
        });
    }
}

