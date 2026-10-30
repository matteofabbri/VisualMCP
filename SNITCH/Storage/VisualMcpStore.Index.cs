using Microsoft.Data.Sqlite;

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
                using var transaction = connection.BeginTransaction();

                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM package_fact WHERE project = $project;";
                    delete.Parameters.AddWithValue("$project", project);
                    delete.ExecuteNonQuery();
                }

                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO package_fact (project, id, version, tfm, direct)
                        VALUES ($project, $id, $version, $tfm, $direct)
                        ON CONFLICT(project, id, tfm) DO UPDATE SET
                            version = excluded.version,
                            direct = excluded.direct;
                        """;
                    var projectParameter = insert.Parameters.Add("$project", SqliteType.Text);
                    var id = insert.Parameters.Add("$id", SqliteType.Text);
                    var version = insert.Parameters.Add("$version", SqliteType.Text);
                    var tfm = insert.Parameters.Add("$tfm", SqliteType.Text);
                    var direct = insert.Parameters.Add("$direct", SqliteType.Integer);

                    foreach (var package in packages)
                    {
                        projectParameter.Value = project;
                        id.Value = package.Id;
                        version.Value = package.Version;
                        tfm.Value = package.Tfm;
                        direct.Value = package.Direct ? 1 : 0;
                        insert.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }
        }
        catch (SqliteException)
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
                using var command = connection.CreateCommand();
                command.CommandText = project is null
                    ? "SELECT project, id, version, tfm, direct FROM package_fact ORDER BY project, id LIMIT $limit;"
                    : "SELECT project, id, version, tfm, direct FROM package_fact WHERE project = $project ORDER BY id LIMIT $limit;";
                command.Parameters.AddWithValue("$limit", limit);
                if (project is not null) command.Parameters.AddWithValue("$project", project);

                var packages = new List<PackageFact>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    packages.Add(new PackageFact(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.GetInt64(4) != 0));
                }

                return packages;
            }
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    public int PackageCount() => Count("SELECT COUNT(*) FROM package_fact;");

    public void ReplacePackageDependencies(string project, IReadOnlyList<PackageDependency> dependencies)
    {
        if (string.IsNullOrWhiteSpace(project)) return;
        var connection = Connection();
        if (connection is null) return;

        try
        {
            lock (_gate)
            {
                using var transaction = connection.BeginTransaction();

                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM package_dependency WHERE project = $project;";
                    delete.Parameters.AddWithValue("$project", project);
                    delete.ExecuteNonQuery();
                }

                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
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
                using var command = connection.CreateCommand();
                var conditions = new List<string>();
                if (project is not null) conditions.Add("project = $project");
                if (childId is not null) conditions.Add("child_id = $child");
                var where = conditions.Count == 0 ? "1 = 1" : string.Join(" AND ", conditions);

                command.CommandText = $"""
                    SELECT project, parent_id, parent_version, tfm, child_id, child_version
                    FROM package_dependency WHERE {where} ORDER BY project, parent_id LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$limit", limit);
                if (project is not null) command.Parameters.AddWithValue("$project", project);
                if (childId is not null) command.Parameters.AddWithValue("$child", childId);

                var edges = new List<PackageDependency>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    edges.Add(new PackageDependency(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), reader.GetString(4), reader.GetString(5)));
                }

                return edges;
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
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO decompiled_artifact (mvid, assembly, name, directory, types, created_at)
                    VALUES ($mvid, $assembly, $name, $directory, $types, $at)
                    ON CONFLICT(mvid) DO UPDATE SET
                        assembly = excluded.assembly,
                        name = excluded.name,
                        directory = excluded.directory,
                        types = excluded.types,
                        created_at = excluded.created_at;
                    """;
                command.Parameters.AddWithValue("$mvid", artifact.Mvid);
                command.Parameters.AddWithValue("$assembly", artifact.AssemblyPath);
                command.Parameters.AddWithValue("$name", artifact.Name);
                command.Parameters.AddWithValue("$directory", artifact.Directory);
                command.Parameters.AddWithValue("$types", artifact.Types);
                command.Parameters.AddWithValue("$at", artifact.CreatedAt.ToString("O"));
                command.ExecuteNonQuery();
            }
        }
        catch (SqliteException)
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
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT mvid, assembly, name, directory, types, created_at
                    FROM decompiled_artifact WHERE mvid = $mvid;
                    """;
                command.Parameters.AddWithValue("$mvid", mvid);

                using var reader = command.ExecuteReader();
                if (!reader.Read()) return null;

                var at = DateTimeOffset.TryParse(reader.GetString(5), out var parsed) ? parsed : DateTimeOffset.MinValue;
                return new DecompiledArtifact(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt32(4), at);
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
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT mvid, assembly, name, directory, types, created_at
                    FROM decompiled_artifact ORDER BY created_at DESC LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$limit", limit);

                var artifacts = new List<DecompiledArtifact>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var at = DateTimeOffset.TryParse(reader.GetString(5), out var parsed) ? parsed : DateTimeOffset.MinValue;
                    artifacts.Add(new DecompiledArtifact(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), reader.GetInt32(4), at));
                }

                return artifacts;
            }
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    // ── small helpers ───────────────────────────────────────────────────────
    private int Count(string sql)
    {
        var connection = Connection();
        if (connection is null) return 0;

        try
        {
            lock (_gate)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }
        catch (SqliteException)
        {
            return 0;
        }
    }
}
