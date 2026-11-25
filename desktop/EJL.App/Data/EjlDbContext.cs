using System.Text.Json;
using EJL.Domain;
using EJL.Util;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EJL.Data;

public class EjlDbContext(DbContextOptions<EjlDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<TemplateVersion> TemplateVersions => Set<TemplateVersion>();
    public DbSet<Journal> Journals => Set<Journal>();
    public DbSet<JournalMember> JournalMembers => Set<JournalMember>();
    public DbSet<RecordEntity> Records => Set<RecordEntity>();
    public DbSet<Revision> Revisions => Set<Revision>();
    public DbSet<ReviewLog> ReviewLogs => Set<ReviewLog>();
    public DbSet<MaterialItem> Items => Set<MaterialItem>();
    public DbSet<Batch> Batches => Set<Batch>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // JSON-колонки: словари и колонки шаблонов хранятся как TEXT
        Dict(b.Entity<RecordEntity>().Property(r => r.Data));
        Dict(b.Entity<Revision>().Property(r => r.OldData));
        Dict(b.Entity<Revision>().Property(r => r.NewData));
        Dict(b.Entity<AuditEvent>().Property(e => e.Payload));

        var cols = b.Entity<TemplateVersion>().Property(v => v.Columns);
        cols.HasConversion(v => JsonEf.SerCols(v), s => JsonEf.DeCols(s));
        cols.Metadata.SetValueComparer(new ValueComparer<List<FormColumn>>(
            (a, c) => JsonEf.SerCols(a) == JsonEf.SerCols(c),
            v => JsonEf.SerCols(v).GetHashCode(),
            v => JsonEf.DeCols(JsonEf.SerCols(v))));

        b.Entity<User>().HasIndex(u => u.Login).IsUnique();
        b.Entity<JournalMember>().HasKey(m => new { m.JournalId, m.UserId });
        b.Entity<RecordEntity>().HasIndex(r => r.JournalId);
        b.Entity<Revision>().HasIndex(r => r.RecordId);
        b.Entity<ReviewLog>().HasIndex(r => r.JournalId);
        b.Entity<Batch>().HasIndex(x => x.ItemId);
        b.Entity<Operation>().HasIndex(x => x.ItemId);
        b.Entity<Notification>().HasIndex(n => n.UserId);
        b.Entity<Notification>().HasIndex(n => n.DedupeKey).IsUnique();
    }

    private static void Dict(PropertyBuilder<Dictionary<string, object?>> p)
    {
        p.HasConversion(v => JsonEf.Ser(v), s => JsonEf.De(s));
        p.Metadata.SetValueComparer(new ValueComparer<Dictionary<string, object?>>(
            (a, b) => JsonEf.Ser(a) == JsonEf.Ser(b),
            v => JsonEf.Ser(v).GetHashCode(),
            v => JsonEf.De(JsonEf.Ser(v))));
    }
}

internal static class JsonEf
{
    private static readonly JsonSerializerOptions Opts = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions();
        Json.Configure(o);
        return o;
    }

    public static string Ser(Dictionary<string, object?>? v) =>
        v is null ? "{}" : JsonSerializer.Serialize(v, Opts);
    public static Dictionary<string, object?> De(string s) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(s, Opts) ?? new();
    public static string SerCols(List<FormColumn>? v) =>
        v is null ? "[]" : JsonSerializer.Serialize(v, Opts);
    public static List<FormColumn> DeCols(string s) =>
        JsonSerializer.Deserialize<List<FormColumn>>(s, Opts) ?? new();
}
