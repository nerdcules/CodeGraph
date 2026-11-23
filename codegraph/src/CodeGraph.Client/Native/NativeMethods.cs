using System.Runtime.InteropServices;

namespace CodeGraph.Client.Native;

/// <summary>
/// The part of the DuckDB C API (duckdb.h) the client needs, bound with source-generated <c>[LibraryImport]</c> so
/// it compiles under Native AOT without warnings. DuckDB.NET.Data's reader raises 10 trim and AOT warnings
/// (IL2067, IL2070, IL2075, IL2093, IL3050 in its list, map and struct readers, measured with DuckDB.NET.Data.Full
/// 1.5.5), so the CLI cannot publish warning-free through it. The library is the libduckdb that
/// DuckDB.NET.Bindings.Full ships, the same one the indexer writes the graph with.
/// </summary>
internal static partial class NativeMethods
{
    private const string Library = "duckdb";

    public const int Success = 0;

    /// <summary>DUCKDB_STATEMENT_TYPE_SELECT in duckdb.h.</summary>
    public const int StatementSelect = 1;

    [LibraryImport(Library)]
    public static partial int duckdb_create_config(out nint config);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int duckdb_set_config(nint config, string name, string option);

    [LibraryImport(Library)]
    public static partial void duckdb_destroy_config(ref nint config);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int duckdb_open_ext(string path, out nint database, nint config, out nint error);

    [LibraryImport(Library)]
    public static partial void duckdb_close(ref nint database);

    [LibraryImport(Library)]
    public static partial int duckdb_connect(nint database, out nint connection);

    [LibraryImport(Library)]
    public static partial void duckdb_disconnect(ref nint connection);

    [LibraryImport(Library)]
    public static partial void duckdb_interrupt(nint connection);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int duckdb_prepare(nint connection, string query, out nint prepared);

    [LibraryImport(Library)]
    public static partial nint duckdb_prepare_error(nint prepared);

    [LibraryImport(Library)]
    public static partial void duckdb_destroy_prepare(ref nint prepared);

    [LibraryImport(Library)]
    public static partial int duckdb_prepared_statement_type(nint prepared);

    [LibraryImport(Library)]
    public static partial ulong duckdb_nparams(nint prepared);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int duckdb_bind_parameter_index(nint prepared, out ulong index, string name);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int duckdb_bind_varchar(nint prepared, ulong index, string value);

    [LibraryImport(Library)]
    public static partial int duckdb_bind_int32(nint prepared, ulong index, int value);

    [LibraryImport(Library)]
    public static partial int duckdb_bind_int64(nint prepared, ulong index, long value);

    [LibraryImport(Library)]
    public static partial int duckdb_bind_double(nint prepared, ulong index, double value);

    [LibraryImport(Library)]
    public static partial int duckdb_bind_boolean(nint prepared, ulong index, [MarshalAs(UnmanagedType.U1)] bool value);

    [LibraryImport(Library)]
    public static partial int duckdb_bind_null(nint prepared, ulong index);

    [LibraryImport(Library)]
    public static partial int duckdb_execute_prepared(nint prepared, out DuckResult result);

    [LibraryImport(Library)]
    public static partial void duckdb_destroy_result(ref DuckResult result);

    [LibraryImport(Library)]
    public static partial nint duckdb_result_error(ref DuckResult result);

    [LibraryImport(Library)]
    public static partial ulong duckdb_row_count(ref DuckResult result);

    [LibraryImport(Library)]
    public static partial ulong duckdb_column_count(ref DuckResult result);

    [LibraryImport(Library)]
    public static partial nint duckdb_column_name(ref DuckResult result, ulong column);

    [LibraryImport(Library)]
    public static partial int duckdb_column_type(ref DuckResult result, ulong column);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool duckdb_value_is_null(ref DuckResult result, ulong column, ulong row);

    [LibraryImport(Library)]
    public static partial nint duckdb_value_varchar(ref DuckResult result, ulong column, ulong row);

    [LibraryImport(Library)]
    public static partial int duckdb_value_int32(ref DuckResult result, ulong column, ulong row);

    [LibraryImport(Library)]
    public static partial long duckdb_value_int64(ref DuckResult result, ulong column, ulong row);

    [LibraryImport(Library)]
    public static partial double duckdb_value_double(ref DuckResult result, ulong column, ulong row);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool duckdb_value_boolean(ref DuckResult result, ulong column, ulong row);

    [LibraryImport(Library)]
    public static partial void duckdb_free(nint pointer);

    [LibraryImport(Library)]
    public static partial nint duckdb_library_version();

    public static string? Text(nint pointer) => pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer);
}

/// <summary>duckdb_result: six pointer-sized fields, all internal to DuckDB; read only through the functions above.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DuckResult
{
    private readonly nint _columnCount;
    private readonly nint _rowCount;
    private readonly nint _rowsChanged;
    private readonly nint _columns;
    private readonly nint _errorMessage;
    private readonly nint _internalData;
}

/// <summary>DUCKDB_TYPE values from duckdb.h that the reader maps to CLR types; anything else is read as text.</summary>
internal enum DuckType
{
    Boolean = 1,
    TinyInt = 2,
    SmallInt = 3,
    Integer = 4,
    BigInt = 5,
    UTinyInt = 6,
    USmallInt = 7,
    UInteger = 8,
    UBigInt = 9,
    Float = 10,
    Double = 11,
    Varchar = 17,
    Decimal = 19,
}
