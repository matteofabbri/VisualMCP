using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Snitch.Storage.Schema;

/// <summary>
/// The EF Core model of <c>&lt;solution&gt;/.visualMCP/visualmcp.db</c>: every table the stores own is
/// declared here once, and <see cref="VisualMcpSchema"/> creates the file's tables from this model.
/// </summary>
/// <remarks>
/// <para>
/// The model is the schema's single source of truth. The stores keep their own tuned SQL for reads and
/// writes — several of them need statements a query provider would only obscure — but none of them
/// carries CREATE TABLE text any more. A database created by an older build keeps its tables:
/// <see cref="VisualMcpSchema.Ensure"/> creates only what is missing.
/// </para>
/// <para>
/// Every column is spelled as the snake_case of its property, which is the convention the schema has
/// always followed; the model applies it in one pass instead of at each property.
/// </para>
/// </remarks>
public sealed class VisualMcpDbContext : DbContext
{
    public VisualMcpDbContext(DbContextOptions<VisualMcpDbContext> options) : base(options) { }

    public DbSet<ContextFactRow> ContextFacts => Set<ContextFactRow>();

    public DbSet<CommandLogRow> CommandLog => Set<CommandLogRow>();

    public DbSet<AppliedEditRow> AppliedEdits => Set<AppliedEditRow>();

    public DbSet<FileSnapshotRow> FileSnapshots => Set<FileSnapshotRow>();

    public DbSet<PackageFactRow> PackageFacts => Set<PackageFactRow>();

    public DbSet<PackageDependencyRow> PackageDependencies => Set<PackageDependencyRow>();

    public DbSet<DecompiledArtifactRow> DecompiledArtifacts => Set<DecompiledArtifactRow>();

    public DbSet<BoardItemRow> BoardItems => Set<BoardItemRow>();

    public DbSet<BoardCommentRow> BoardComments => Set<BoardCommentRow>();

    public DbSet<BoardHistoryRow> BoardHistory => Set<BoardHistoryRow>();

    public DbSet<TextIndexDocumentRow> TextIndexDocuments => Set<TextIndexDocumentRow>();

    public DbSet<TextIndexLineRow> TextIndexLines => Set<TextIndexLineRow>();

    public DbSet<MetacodeDocumentRow> MetacodeDocuments => Set<MetacodeDocumentRow>();

    public DbSet<MetacodeTreeRow> MetacodeTrees => Set<MetacodeTreeRow>();

    public DbSet<SourceDocumentRow> SourceDocuments => Set<SourceDocumentRow>();

    public DbSet<MetacodeNodeRow> MetacodeNodes => Set<MetacodeNodeRow>();

    public DbSet<SymbolRow> Symbols => Set<SymbolRow>();

    public DbSet<SymbolUsageRow> SymbolUsages => Set<SymbolUsageRow>();

    public DbSet<SymbolDocumentRow> SymbolDocuments => Set<SymbolDocumentRow>();

    public DbSet<SymbolMetacodeRow> SymbolMetacode => Set<SymbolMetacodeRow>();

    public DbSet<NativeArtifactRow> NativeArtifacts => Set<NativeArtifactRow>();

    public DbSet<NativeFunctionRow> NativeFunctions => Set<NativeFunctionRow>();

    public DbSet<NativeCallRow> NativeCalls => Set<NativeCallRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<ContextFactRow>(row =>
        {
            row.ToTable("context");
            row.HasKey(fact => fact.Key);
        });

        model.Entity<CommandLogRow>(row =>
        {
            row.ToTable("command_log");
            row.HasKey(entry => entry.Id);
            row.HasIndex(entry => entry.OccurredAt).HasDatabaseName("command_log_at").IsDescending();
        });

        model.Entity<AppliedEditRow>(row =>
        {
            row.ToTable("applied_edit");
            row.HasKey(edit => edit.Id);
            row.HasIndex(edit => new { edit.File, edit.AppliedAt })
                .HasDatabaseName("applied_edit_file")
                .IsDescending(false, true);
        });

        // Local history: whole-file snapshots, newest first per path. The unique (file, hash) index is
        // what makes a repeated snapshot of unchanged content a no-op instead of a duplicate row.
        model.Entity<FileSnapshotRow>(row =>
        {
            row.ToTable("file_snapshot");
            row.HasKey(snapshot => snapshot.Id);
            row.HasIndex(snapshot => new { snapshot.File, snapshot.CapturedAt })
                .HasDatabaseName("file_snapshot_file")
                .IsDescending(false, true);
            row.HasIndex(snapshot => new { snapshot.File, snapshot.Hash })
                .IsUnique()
                .HasDatabaseName("file_snapshot_content");
        });

        model.Entity<PackageFactRow>(row =>
        {
            row.ToTable("package_fact");
            row.HasKey(fact => new { fact.Project, fact.Id, fact.Tfm });
        });

        // The historical table has no key, and the insert path (delete the project's edges, write them
        // again) was only ever tested against a table without one: the model mirrors that instead of
        // adding a constraint that would turn a repeated edge into a failed refresh.
        model.Entity<PackageDependencyRow>(row =>
        {
            row.ToTable("package_dependency");
            row.HasNoKey();
            row.HasIndex(edge => new { edge.Project, edge.ParentId }).HasDatabaseName("package_dependency_parent");
            row.HasIndex(edge => new { edge.Project, edge.ChildId }).HasDatabaseName("package_dependency_child");
        });

        model.Entity<DecompiledArtifactRow>(row =>
        {
            row.ToTable("decompiled_artifact");
            row.HasKey(artifact => artifact.Mvid);
        });

        model.Entity<BoardItemRow>(row =>
        {
            row.ToTable("board_item");
            row.HasKey(item => item.Id);
            row.HasIndex(item => new { item.Status, item.Position }).HasDatabaseName("board_item_status");
        });

        model.Entity<BoardCommentRow>(row =>
        {
            row.ToTable("board_comment");
            row.HasKey(comment => comment.Id);
            row.HasIndex(comment => comment.ItemId).HasDatabaseName("board_comment_item");
        });

        model.Entity<BoardHistoryRow>(row =>
        {
            row.ToTable("board_history");
            row.HasKey(history => history.Id);
            row.HasIndex(history => history.ItemId).HasDatabaseName("board_history_item");
        });

        model.Entity<TextIndexDocumentRow>(row =>
        {
            row.ToTable("ti_document");
            row.HasKey(document => document.Path);
        });

        // A map from a document's line numbers to the rowids of the FTS5 table; (path, line) is unique in
        // practice, but the historical table has no key, so the model adds none.
        model.Entity<TextIndexLineRow>(row =>
        {
            row.ToTable("ti_row");
            row.HasNoKey();
            row.HasIndex(line => line.Path).HasDatabaseName("ti_row_path");
        });

        model.Entity<MetacodeDocumentRow>(row =>
        {
            row.ToTable("mc_document");
            row.HasKey(document => document.Path);
        });

        model.Entity<MetacodeTreeRow>(row =>
        {
            row.ToTable("mc_tree");
            row.HasKey(tree => new { tree.DeclarationKey, tree.Path, tree.Line, tree.Name });
            row.HasIndex(tree => tree.Path).HasDatabaseName("mc_tree_path");
            row.HasIndex(tree => tree.Name).HasDatabaseName("mc_tree_name");
            row.HasIndex(tree => tree.Kind).HasDatabaseName("mc_tree_kind");
            row.HasIndex(tree => tree.Cyclomatic).HasDatabaseName("mc_tree_cc");
        });

        model.Entity<SourceDocumentRow>(row =>
        {
            row.ToTable("source_document");
            row.HasKey(document => document.Path);
        });

        model.Entity<MetacodeNodeRow>(row =>
        {
            row.ToTable("mc_node");
            row.HasKey(node => new { node.Path, node.DeclarationKey, node.NodeId });
            row.HasIndex(node => node.Path).HasDatabaseName("mc_node_path");
            row.HasIndex(node => new { node.Path, node.DeclarationKey, node.ParentId })
                .HasDatabaseName("mc_node_parent");
            row.HasIndex(node => node.Kind).HasDatabaseName("mc_node_kind");
        });

        model.Entity<SymbolRow>(row =>
        {
            row.ToTable("si_symbol");
            row.HasKey(symbol => new { symbol.SymbolKey, symbol.Path, symbol.Start });
            row.HasIndex(symbol => symbol.Name).HasDatabaseName("si_symbol_name");
            row.HasIndex(symbol => symbol.ContainerKey).HasDatabaseName("si_symbol_container");
            row.HasIndex(symbol => symbol.Path).HasDatabaseName("si_symbol_path");
            row.HasIndex(symbol => symbol.Kind).HasDatabaseName("si_symbol_kind");
        });

        model.Entity<SymbolUsageRow>(row =>
        {
            row.ToTable("si_usage");
            row.HasKey(usage => usage.Id);
            row.HasIndex(usage => usage.SymbolKey).HasDatabaseName("si_usage_key");
            row.HasIndex(usage => usage.ContainerKey).HasDatabaseName("si_usage_container");
            row.HasIndex(usage => usage.Kind).HasDatabaseName("si_usage_kind");
            row.HasIndex(usage => usage.Path).HasDatabaseName("si_usage_path");
        });

        model.Entity<SymbolDocumentRow>(row =>
        {
            row.ToTable("si_document");
            row.HasKey(document => document.Path);
        });

        model.Entity<SymbolMetacodeRow>(row =>
        {
            row.ToTable("si_metacode");
            row.HasKey(metacode => new { metacode.SymbolKey, metacode.Path });
            row.HasIndex(metacode => metacode.Path).HasDatabaseName("si_metacode_path");
        });

        model.Entity<NativeArtifactRow>(row =>
        {
            row.ToTable("native_artifact");
            row.HasKey(artifact => artifact.Id);
            // An inline UNIQUE in the historical table; EF states the same guarantee as a unique index.
            row.HasIndex(artifact => artifact.Sha256).IsUnique().HasDatabaseName("native_artifact_sha256");
        });

        model.Entity<NativeFunctionRow>(row =>
        {
            row.ToTable("native_function");
            row.HasKey(function => new { function.Artifact, function.Ordinal });
            row.HasIndex(function => new { function.Artifact, function.Name }).HasDatabaseName("native_function_name");
            row.HasIndex(function => new { function.Artifact, function.Entry }).HasDatabaseName("native_function_entry");
        });

        model.Entity<NativeCallRow>(row =>
        {
            row.ToTable("native_call");
            row.HasKey(call => new { call.Artifact, call.Caller, call.Callee });
            row.HasIndex(call => new { call.Artifact, call.Callee }).HasDatabaseName("native_call_callee");
        });

        // Applied last, so a table or key configured above cannot be missed by the convention.
        foreach (var entityType in model.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
                property.SetColumnName(SnakeCase(property.Name));
        }

        // The one column whose historical spelling is not the snake_case of the property that holds it.
        model.Entity<TextIndexLineRow>().Property(row => row.RowId).HasColumnName("rowid");
    }

    /// <summary>The schema's historical column spelling: <c>OccurredAt</c> is <c>occurred_at</c>.</summary>
    private static string SnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        foreach (var character in name)
        {
            if (char.IsUpper(character) && builder.Length > 0) builder.Append('_');
            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
