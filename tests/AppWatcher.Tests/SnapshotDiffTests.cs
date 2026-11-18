using AppWatcher.Core.Snapshots;
using AppWatcher.Core.Sources;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AppWatcher.Tests;

public class SnapshotDiffTests
{
    private static readonly string[] Cols = { "id", "name", "total" };

    [Fact]
    public void Diff_reports_added_removed_changed_rows_and_files()
    {
        var before = Synthetic.Snapshot("app.db",
            ("orders", Synthetic.Table(Cols, new[] { "id" }, new[] { "1", "A", "10" }, new[] { "2", "B", "20" })));
        before.Files["c:/data/report.csv"] = new FileEntry { Size = 10, Sha256 = "aa" };
        before.Files["c:/data/gone.txt"] = new FileEntry { Size = 1, Sha256 = "x" };
        var after = Synthetic.Snapshot("app.db",
            ("orders", Synthetic.Table(Cols, new[] { "id" }, new[] { "1", "A", "15" }, new[] { "3", "C", "30" })));
        after.Files["c:/data/report.csv"] = new FileEntry { Size = 12, Sha256 = "bb" };
        after.Files["c:/data/new.txt"] = new FileEntry { Size = 1, Sha256 = "y" };

        var diff = SnapshotDiffer.Diff(before, after);

        var t = Assert.Single(diff.Tables);
        Assert.Equal("orders", t.Table);
        Assert.Single(t.Added); Assert.Equal("C", t.Added["3"]["name"]);
        Assert.Single(t.Removed); Assert.Equal("B", t.Removed["2"]["name"]);
        var changed = Assert.Single(t.Changed);
        Assert.Equal("1", changed.Key);
        Assert.Equal("10", changed.Value["total"].Old); Assert.Equal("15", changed.Value["total"].New);
        Assert.DoesNotContain("name", changed.Value.Keys);
        Assert.Equal(3, diff.Files.Count);
        Assert.Contains(diff.Files, f => f.Path.EndsWith("report.csv") && f.State == "modified");
        Assert.Contains(diff.Files, f => f.Path.EndsWith("gone.txt") && f.State == "deleted");
        Assert.Contains(diff.Files, f => f.Path.EndsWith("new.txt") && f.State == "created");
        Assert.False(diff.IsEmpty);
    }

    [Fact]
    public void Diff_markdown_matches_python_layout()
    {
        var before = Synthetic.Snapshot("c:/x/app.db", ("t", Synthetic.Table(new[] { "id", "v" }, new[] { "id" }, new[] { "1", "a" })));
        var after = Synthetic.Snapshot("c:/x/app.db", ("t", Synthetic.Table(new[] { "id", "v" }, new[] { "id" }, new[] { "1", "b" }, new[] { "2", "c" })));
        var md = SnapshotDiffer.Diff(before, after).ToMarkdown("before-x", "after-x");
        Assert.StartsWith("## Data changes: before-x -> after-x", md);
        Assert.Contains("### app.db / t: +1 -0 ~1", md);
        Assert.Contains("- added 2: {\"id\":\"2\",\"v\":\"c\"}", md);
        Assert.Contains("- changed 1: {\"v\":[\"a\",\"b\"]}", md);
        Assert.Contains("No data changes detected.", SnapshotDiffer.Diff(after, after).ToMarkdown());
    }

    [Fact]
    public void Rows_without_primary_key_are_keyed_by_content_hash()
    {
        var t = Synthetic.Table(new[] { "a", "b" }, Array.Empty<string>(), new[] { "1", "x" }, new[] { "2", "y" });
        Assert.Equal(2, t.Rows.Count);
        Assert.All(t.Rows.Keys, k => Assert.Equal(32, k.Length));
    }

    [Fact]
    public void Sqlite_source_reads_tables_read_only_and_diffs_real_changes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "appwatcher-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "shop.db");
        try
        {
            using (var con = new SqliteConnection($"Data Source={db}"))
            {
                con.Open();
                Exec(con, "CREATE TABLE customers(id INTEGER PRIMARY KEY, name TEXT, city TEXT)");
                Exec(con, "CREATE TABLE orders(id INTEGER PRIMARY KEY, customer_id INTEGER, qty INTEGER, price REAL, total REAL, status TEXT)");
                Exec(con, "INSERT INTO customers VALUES (1,'Ann','Oslo'),(2,'Bob','Rome')");
                Exec(con, "INSERT INTO orders VALUES (10,1,2,5.5,11.0,'open')");
            }
            var settings = new AppWatcher.Core.SnapshotSettings();
            var src = (SqliteSource)FileSource.ForPath(db);
            var before = new DataSnapshot(); src.Snapshot(before, settings, null);
            Assert.Equal(2, before.Databases[src.Id].Count);
            Assert.Equal(new[] { "id" }, before.Databases[src.Id]["orders"].PrimaryKey);
            Assert.Equal("11", before.Databases[src.Id]["orders"].Rows["10"]["total"]);

            using (var con = new SqliteConnection($"Data Source={db}"))
            {
                con.Open();
                Exec(con, "INSERT INTO orders VALUES (11,2,3,4.0,12.0,'open')");
                Exec(con, "UPDATE orders SET status='paid' WHERE id=10");
            }
            var after = new DataSnapshot(); src.Snapshot(after, settings, null);
            var diff = SnapshotDiffer.Diff(before, after);
            var od = Assert.Single(diff.Tables);
            Assert.Single(od.Added); Assert.Single(od.Changed);
            Assert.Equal("paid", od.Changed["10"]["status"].New);

            // hot-table mode skips cold tables when the db is over the cap
            var hot = new DataSnapshot();
            src.Snapshot(hot, new AppWatcher.Core.SnapshotSettings { MaxDbBytes = 1 }, new HashSet<string> { "orders" });
            Assert.True(hot.Databases[src.Id]["customers"].Skipped);
            Assert.False(hot.Databases[src.Id]["orders"].Skipped);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void Exec(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }
}
