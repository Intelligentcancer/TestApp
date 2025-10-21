using System;
using Microsoft.EntityFrameworkCore;
namespace GenesysRecordingPostingUtility.Models
{
    public class GenesysConversation
    {
        public string CallId { get; set; }
        public System.DateTime ConversationStart { get; set; }
        public System.DateTime ConversationEnd { get; set; }
        public string ClientID { get; set; }
        public string AgentID { get; set; }
        public string BTN { get; set; }
        public string CalledParty { get; set; }
        public string SkillName { get; set; }
        public string CallOutcome { get; set; }
        public System.DateTime UpdateTime { get; set; }
        public string? AudioSourcePath { get; set; }
        public string? AudioFileName { get; set; }
        public string? ScreenSourcePath { get; set; }
        public string? ScreenFileName { get; set; }
        public int isPosted { get; set; }
        public System.DateTime? PostedAtUtc { get; set; }
    }

    public class GenesysConvDiv
    {
        public int Id { get; set; }
        public string ConversationId { get; set; }
        public string DivisionId { get; set; }
    }
    public class GenesysUser
    {
        public string UserId { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public System.DateTime UpdateDate { get; set; }
    }
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<GenesysConversation> GenesysConversations => Set<GenesysConversation>();
        public DbSet<GenesysConvDiv> GenesysConvDivs => Set<GenesysConvDiv>();
        public DbSet<GenesysUser> GenesysUsers => Set<GenesysUser>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<GenesysConversation>(entity =>
            {
                entity.ToTable("GenesysConversations");
                entity.HasKey(e => e.CallId);
                entity.HasIndex(e => e.CallId).IsUnique(true);
            });

            modelBuilder.Entity<GenesysConvDiv>(entity =>
            {
                entity.ToTable("GenesysConvDivs");
                entity.HasKey(e => e.Id);
            });
            modelBuilder.Entity<GenesysUser>(entity =>
            {
                entity.ToTable("GenesysUsers");
                entity.HasKey(e => e.UserId);
            });
        }
    }
}
