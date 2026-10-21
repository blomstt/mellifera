using System.Runtime.InteropServices;
using System.Text;

namespace Hive;

// The system libsqlite3 (built with FTS5, as distributions ship it), bound by hand: a dozen
// functions instead of a NuGet package and a bundled native library.
static unsafe partial class Sqlite3
{
    const string Lib = "libsqlite3.so.0";
    public const int Ok = 0, Row = 100, Done = 101;

    [LibraryImport(Lib)] public static partial int sqlite3_open_v2(byte* filename, out nint db, int flags, byte* vfs);
    [LibraryImport(Lib)] public static partial int sqlite3_close_v2(nint db);
    [LibraryImport(Lib)] public static partial int sqlite3_busy_timeout(nint db, int ms);
    [LibraryImport(Lib)] public static partial byte* sqlite3_errmsg(nint db);
    [LibraryImport(Lib)] public static partial int sqlite3_prepare_v2(nint db, byte* sql, int bytes, out nint stmt, out nint tail);
    [LibraryImport(Lib)] public static partial int sqlite3_step(nint stmt);
    [LibraryImport(Lib)] public static partial int sqlite3_finalize(nint stmt);
    [LibraryImport(Lib)] public static partial int sqlite3_bind_text(nint stmt, int index, byte* text, int bytes, nint destructor);
    [LibraryImport(Lib)] public static partial int sqlite3_bind_int64(nint stmt, int index, long value);
    [LibraryImport(Lib)] public static partial int sqlite3_bind_null(nint stmt, int index);
    [LibraryImport(Lib)] public static partial byte* sqlite3_column_text(nint stmt, int index);
    [LibraryImport(Lib)] public static partial int sqlite3_column_bytes(nint stmt, int index);
    [LibraryImport(Lib)] public static partial long sqlite3_column_int64(nint stmt, int index);
}

readonly unsafe struct Row(nint stmt)
{
    public string Text(int i)
    {
        var p = Sqlite3.sqlite3_column_text(stmt, i);
        return p == null ? "" : Marshal.PtrToStringUTF8((nint)p, Sqlite3.sqlite3_column_bytes(stmt, i));
    }

    public long Long(int i) => Sqlite3.sqlite3_column_int64(stmt, i);
}

sealed unsafe class Db : IDisposable
{
    static readonly nint Transient = -1;
    nint handle;

    public Db(string path)
    {
        fixed (byte* p = Utf8(path))
            Check(Sqlite3.sqlite3_open_v2(p, out handle, 0x2 | 0x4, null)); // READWRITE | CREATE
        Sqlite3.sqlite3_busy_timeout(handle, 5000);
    }

    // Runs statements that take no parameters, one after another.
    public void Script(string sql)
    {
        fixed (byte* start = Utf8(sql))
        {
            byte* p = start;
            while (*p != 0)
            {
                Check(Sqlite3.sqlite3_prepare_v2(handle, p, -1, out var stmt, out var tail));
                if (stmt == 0) break;
                var rc = Sqlite3.sqlite3_step(stmt);
                if (rc is not (Sqlite3.Row or Sqlite3.Done)) Fail(stmt);
                Sqlite3.sqlite3_finalize(stmt);
                p = (byte*)tail;
            }
        }
    }

    public void Exec(string sql, params object?[] args)
    {
        var stmt = Prepare(sql, args);
        var rc = Sqlite3.sqlite3_step(stmt);
        if (rc is not (Sqlite3.Row or Sqlite3.Done)) Fail(stmt);
        Sqlite3.sqlite3_finalize(stmt);
    }

    public List<T> Query<T>(string sql, Func<Row, T> map, params object?[] args)
    {
        var stmt = Prepare(sql, args);
        var list = new List<T>();
        int rc;
        while ((rc = Sqlite3.sqlite3_step(stmt)) == Sqlite3.Row) list.Add(map(new Row(stmt)));
        if (rc != Sqlite3.Done) Fail(stmt);
        Sqlite3.sqlite3_finalize(stmt);
        return list;
    }

    nint Prepare(string sql, object?[] args)
    {
        nint stmt;
        fixed (byte* p = Utf8(sql)) Check(Sqlite3.sqlite3_prepare_v2(handle, p, -1, out stmt, out _));
        for (int i = 0; i < args.Length; i++)
        {
            var rc = args[i] switch
            {
                null => Sqlite3.sqlite3_bind_null(stmt, i + 1),
                long n => Sqlite3.sqlite3_bind_int64(stmt, i + 1, n),
                int n => Sqlite3.sqlite3_bind_int64(stmt, i + 1, n),
                bool b => Sqlite3.sqlite3_bind_int64(stmt, i + 1, b ? 1 : 0),
                string s => BindText(stmt, i + 1, s),
                var other => throw new ArgumentException("can't bind " + other.GetType().Name),
            };
            if (rc != Sqlite3.Ok) Fail(stmt);
        }
        return stmt;
    }

    static int BindText(nint stmt, int index, string s)
    {
        // Zero-terminated so an empty string still has an address; an empty
        // array would pin to null and bind NULL.
        var bytes = Utf8(s);
        fixed (byte* p = bytes) return Sqlite3.sqlite3_bind_text(stmt, index, p, bytes.Length - 1, Transient);
    }

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    string Error() => Marshal.PtrToStringUTF8((nint)Sqlite3.sqlite3_errmsg(handle)) ?? "unknown error";

    void Check(int rc)
    {
        if (rc != Sqlite3.Ok) throw new HiveException("sqlite: " + Error());
    }

    void Fail(nint stmt)
    {
        var message = Error();
        Sqlite3.sqlite3_finalize(stmt);
        throw new HiveException("sqlite: " + message);
    }

    public void Dispose()
    {
        if (handle != 0) Sqlite3.sqlite3_close_v2(handle);
        handle = 0;
    }
}
