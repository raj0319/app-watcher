using AppWatcher.Core.Sql;
using Xunit;

namespace AppWatcher.Tests;

public class SqlParserTests
{
    [Fact]
    public void Insert_with_columns_and_values()
    {
        var st = SqlStatementParser.Parse("INSERT INTO [dbo].[Customers] ([Name], [City], [Credit]) VALUES (N'O''Neil, Ann', 'Oslo', 1250.50)", DateTime.Now)!;
        Assert.Equal("insert", st.Kind);
        Assert.Equal("Customers", st.Table);
        Assert.Equal(new[] { "Name", "City", "Credit" }, st.Columns);
        Assert.Equal("O'Neil, Ann", st.Values["Name"]);
        Assert.Equal("Oslo", st.Values["City"]);
        Assert.Equal("1250.5", st.Values["Credit"]);
        Assert.True(st.IsWrite);
    }

    [Fact]
    public void Update_and_delete()
    {
        var up = SqlStatementParser.Parse("update Orders set Status = 'paid', Total = Qty * Price, Note = NULL where Id = 7", DateTime.Now)!;
        Assert.Equal("update", up.Kind);
        Assert.Equal("Orders", up.Table);
        Assert.Equal(new[] { "Status", "Total", "Note" }, up.Columns);
        Assert.Equal("paid", up.Values["Status"]);
        Assert.Null(up.Values["Note"]);
        var del = SqlStatementParser.Parse("DELETE FROM sales.OrderLines WHERE OrderId = 3", DateTime.Now)!;
        Assert.Equal("delete", del.Kind);
        Assert.Equal("sales.OrderLines", del.Table);
    }

    [Fact]
    public void Parameterized_sp_executesql_resolves_parameters()
    {
        var sql = "exec sp_executesql N'INSERT INTO Orders (Number, CustomerId, Qty) VALUES (@p0, @p1, @p2)',N'@p0 nvarchar(20),@p1 int,@p2 int',@p0=N'ORD-1009',@p1=2,@p2=3";
        var st = SqlStatementParser.Parse(sql, DateTime.Now)!;
        Assert.Equal("insert", st.Kind);
        Assert.Equal("Orders", st.Table);
        Assert.Equal("ORD-1009", st.Values["Number"]);
        Assert.Equal("2", st.Values["CustomerId"]);
        Assert.Equal("3", st.Values["Qty"]);
    }

    [Fact]
    public void Select_and_comments_are_not_writes()
    {
        var sel = SqlStatementParser.Parse("-- lookup\nSELECT * FROM Customers WHERE Id = 1", DateTime.Now)!;
        Assert.Equal("select", sel.Kind);
        Assert.False(sel.IsWrite);
        Assert.Null(SqlStatementParser.Parse("   ", DateTime.Now));
    }

    [Fact]
    public void Csv_and_odbc_trace_readers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "appwatcher-sql-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var csv = Path.Combine(dir, "log.csv");
            File.WriteAllText(csv, "timestamp,statement\n2026-01-01T10:00:00,\"INSERT INTO T (A) VALUES ('x')\"\n2026-01-01T10:00:05,\"UPDATE T SET A = 'y' WHERE Id = 1\"\n");
            var fromCsv = SqlLogReaders.ReadCsv(csv);
            Assert.Equal(2, fromCsv.Count);
            Assert.Equal("insert", fromCsv[0].Kind);
            Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 5), fromCsv[1].T);

            var log = Path.Combine(dir, "SQL.LOG");
            File.WriteAllText(log, string.Join("\n", new[]
            {
                "myapp           1a2c-3b4d\tENTER SQLExecDirectW ",
                "\t\tHSTMT               0x00000000",
                "\t\tWCHAR *             0x0000000 [      -3] \"INSERT INTO Orders (Number) VALUES ('ORD-1')\\ 0\"",
                "\t\tSDWORD                    -3",
                "otherapp        9999-0000\tENTER SQLExecDirectW ",
                "\t\tWCHAR *             0x0000000 [      -3] \"DELETE FROM Secrets\\ 0\"",
                "myapp           1a2c-3b4d\tEXIT  SQLExecDirectW  with return code 0 (SQL_SUCCESS)",
            }));
            var fromOdbc = SqlLogReaders.ReadOdbcTrace(log, "myapp.exe");
            var st = Assert.Single(fromOdbc);
            Assert.Equal("Orders", st.Table);
            Assert.Equal("insert", st.Kind);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
