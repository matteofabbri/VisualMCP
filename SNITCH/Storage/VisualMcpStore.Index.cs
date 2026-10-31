using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Snitch.Storage.Schema;

namespace Snitch.Storage;

/// <summary>
/// The parts of the store that carry derived facts about the solution outside the source itself: the
/// resolved NuGet package facts and the decompiled artifacts kept under <c>.visualMCP</c>. Everything
/// here is rebuildable; the files on disk stay the only truth. The symbol index — which is derived from
/// the source, not from a build — lives in Snitch.Metacode and owns its own tables in the same database.
/// </summary>
public sealed partial class VisualMcpStore
{
    // ── packages ────────────────────────────────────────────────────────────
    public void ReplacePackages(string project, IReadOnlyList<PackageFact> packages)
    {
        if (string.IsNullOrWhiteSpace(project)) return;
        var connection = Connection();
        if (connection is null) return;

        try
        {
            lock (_gate)
            {
                using var context = VisualMcpSchema.CreateContext(connection);
                using var transaction = context.Database.BeginTransaction();

                context.PackageFacts.Where(fact => fact.Project == project).ExecuteDelete();

                var rows = packages
                    .GroupBy(package => (package.Id, package.Tfm))
                    .Select(group => group.Last())
                    .Select(package => new PackageFactRow
                    {
                        Project = project,
                        Id = package.Id,
                        Version = package.Version,
                        Tfm = package.Tfm,
                        Direct = package.Direct,
                    });

                context.PackageFacts.AddRange(rows);
                context.SaveChanges();
                transaction.Commit();
            }
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException)
        {
        }
    }

    public IReadOnlyList<PackageFact> Packages(string? project = null, int limit = 2000)
    {
        if (limit < 1) limit = 1;
        if (limit > 20_000) limit = 20_000;

        var connection = Connection();
        if (connection is null) return [];

        try
        {
            lock (_gate)
            {
                using var context = VisualMcpSchema.CreateContext(connection);
                var query = context.PackageFacts.AsNoTracking().AsQueryable();
                if (project is not null) query = query.Where(fact => fact.Project == project);

                return query
                    .OrderBy(fact => fact.Project)
                    .ThenBy(fact => fact.Id)
                    .Take(limit)
                    .Select(fact => new { fact.Project, fact.Id, fact.Version, fact.Tfm, fact.Direct })
                    .ToList()
                    .Select(fact => new PackageFact(fact.Project, fact.Id, fact.Version, fact.Tfm, fact.Direct))
                    .ToList();
            }
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    public int PackageCount()
    {
        var connection = Connection();
        if (connection is null) return 0;

        try
        {
            lock (_gate)
            {
                using var context = VisualMcpSchema.CreateContext(connection);
                return context.PackageFacts.Count();
            }
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    public void ReplacePackageDependencies(string project, IReadOnlyList<PackageDependency> dependencies)
    {
        if (string.IsNullOrWhiteSpace(project)) return;
        var connection = Connection();
        if (connection is null) return;

        try
        {
            lock (_gate)
            {
                using var context = VisualMcpSchema.CreateContext(connection);
                using var transaction = context.Database.BeginTransaction();

                // package_dependency is a keyless entity: EF Core never inserts a keyless type, so the
                // clear goes through the model and the edges are written with one prepared command,
                // inside the same transaction.
                context.Database.ExecuteSqlInterpolated($"DELETE FROM package_dependency WHERE project = {project}");

                using var insert = connection.CreateCommand();
                insert.Transaction = (SqliteTransaction)transaction.GetDbTransaction();
                insert.CommandText = """
                    INSERT INTO package_dependency (project, parent_id, parent_version, tfm, child_id, child_version)
                    VALUES ($project, $parent, $parentVersion, $tfm, $child, $childVersion);
                    """;
                var projectParameter = insert.Parameters.Add("$project", SqliteType.Text);
                var parent = insert.Parameters.Add("$parent", SqliteType.Text);
                var parentVersion = insert.Parameters.Add("$parentVersion", SqliteType.Text);
                var tfm = insert.Parameters.Add("$tfm", SqliteType.Text);
                var child = insert.Parameters.Add("$child", SqliteType.Text);
                var childVersion = insert.Parameters.Add("$childVersion", SqliteType.Text);

                foreach (var dependency in dependencies)
                {
                    projectParameter.Value = project;
                    parent.Value = dependency.ParentId;
                    parentVersion.Value = dependency.ParentVersion;
                    tfm.Value = dependency.Tfm;
                    child.Value = dependency.ChildId;
                    childVersion.Value = dependency.ChildVersion;
                    insert.ExecuteNonQuery();
                }

                transaction.Commit();
            }
        }
        catch (SqliteException)
        {
        }
    }

    public IReadOnlyList<PackageDependency> PackageDependencies(string? project, string? childId = null, int limit = 500)
    {
        if (limit < 1) limit = 1;
        if (limit > 5000) limit = 5000;

        var connection = Connection();
        if (connection is null) return [];

        try
        {
            lock (_gate)
            {
                using var context = VisualMcpSchema.CreateContext(connection);
                var query = context.PackageDependencies.AsNoTracking().AsQueryable();
                if (project is not null) query = query.Where(edge => edge.Project == project);
                if (childId is not null) query = query.Where(edge => edge.ChildId == childId);

                return query
                    .OrderBy(edge => edge.Project)
                    .ThenBy(edge => edge.ParentId)
                    .Take(limit)
                    .Select(edge => new
                    {
                        edge.Project,
                        edge.ParentId,
                        edge.ParentVersion,
                        edge.Tfm,
                        edge.ChildId,
                        edge.ChildVersion,
                    })
                    .ToList()
                    .Select(edge => new PackageDependency(
                        edge.Project, edge.ParentId, edge.ParentVersion, edge.Tfm, edge.ChildId, edge.ChildVersion))
                    .ToList();
            }
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    // ── decompiled artifacts ────────────────────────────────────────────────
    public void RecordArtifact(DecompiledArtifact artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.Mvid)) return;
        var connection = Connection();
        if (connection is null) return;

        try
        {
            lock (_gate)
            {
                using var context = VisualMcpSchema.CreateContext(connection);
                var row = context.DecompiledArtifacts.FirstOrDefault(item => item.Mvid == artifact.Mvid);
                if (row is null)
                {
                    context.DecompiledArtifacts.Add(new DecompiledArtifactRow
                    {
                        Mvid = artifact.Mvid,
                        Assembly = artifact.AssemblyPath,
                        Name = artifact.Name,
                        Directory = artifact.Directory,
                        Types = artifact.Types,
                        CreatedAt = artifact.CreatedAt,
                    });
                }
                else
                {
                    row.Assembly = artifact.AssemblyPath;
                    row.Name = artifact.Name;
                    row.Directory = artifact.Directory;
                    row.Types = artifact.Types;
                    row.CreatedAt = artifact.CreatedAt;
                }

                context.SaveChanges();
            }
        }
        catch (Exception ex) when (ex is SqliteException or DbUpdateException)
        {
        }
    }

    public DecompiledArtifact? FindArtifact(string mvid)
    {
        if (string.IsNullOrWhiteSpace(mvid)) return null;
        var connection = Connection();
        if (connection is null) return null;

        try
        {
            lock (_gate)
            {
                using var context = VisualMcpSchema.CreateContext(connection);
                var artifact = context.DecompiledArtifacts
                    .AsNoTracking()
                    .Where(row => row.Mvid == mvid)
                    .Select(row => new { row.Mvid, row.Assembly, row.Name, row.Directory, row.Types, row.CreatedAt })
                    .FirstOrDefault();

                return artifact is null
                    ? null
                    : new DecompiledArtifact(
                        artifact.Mvid, artifact.Assembly, artifact.Name,
                        artifact.Directory, artifact.Types, artifact.CreatedAt);
            }
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public IReadOnlyList<DecompiledArtifact> Artifacts(int limit = 200)
    {
        if (limit < 1) limit = 1;
        if (limit > 2000) limit = 2000;

        var connection = Connection();
        if (connection is null) return [];

        try
        {
            lock (_gate)
            {
                using var context = VisualMcpSchema.CreateContext(connection);

                // SQLite cannot ORDER BY a DateTimeOffset through EF; artifacts are few, so the page is
                // ordered after it is read.
                return context.DecompiledArtifacts
                    .AsNoTracking()
                    .Select(row => new { row.Mvid, row.Assembly, row.Name, row.Directory, row.Types, row.CreatedAt })
                    .ToList()
                    .OrderByDescending(row => row.CreatedAt)
                    .Take(limit)
                    .Select(row => new DecompiledArtifact(
                        row.Mvid, row.Assembly, row.Name, row.Directory, row.Types, row.CreatedAt))
                    .ToList();
            }
        }
        catch (SqliteException)
        {
            return [];
        }
    }
}
