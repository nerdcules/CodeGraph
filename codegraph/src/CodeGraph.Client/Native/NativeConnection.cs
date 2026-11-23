using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace CodeGraph.Client.Native;

/// <summary>
/// A read-only DuckDB connection over the C API, with just enough of ADO.NET (commands, named parameters, a
/// forward-only reader) for <c>GraphStore</c>'s SQL to run on it unchanged. The database opens with
/// <c>access_mode=READ_ONLY</c> and <c>enable_external_access=false</c>, so no statement can write the graph, read
/// or write another file, or load an extension.
/// </summary>
public sealed class NativeConnection : DbConnection
{
    private nint _database;
    private nint _connection;
    private readonly string _path;

    private NativeConnection(string path) => _path = path;

    /// <summary>
    /// Opens <paramref name="path"/> read-only, retrying for up to <paramref name="wait"/> while another process
    /// (the indexer) holds the write lock.
    /// </summary>
    public static NativeConnection OpenReadOnly(string path, TimeSpan wait)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("No code graph at this path; run codegraph-index first.", path);
        }

        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            var connection = new NativeConnection(path);
            try
            {
                connection.Open();
                return connection;
            }
            catch (NativeDuckDbException e) when (DateTime.UtcNow < deadline && e.Message.Contains("lock", StringComparison.OrdinalIgnoreCase))
            {
                connection.Dispose();
                Thread.Sleep(100);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
    }

    /// <summary>The DuckDB library version, e.g. v1.4.0.</summary>
    public static string LibraryVersion => NativeMethods.Text(NativeMethods.duckdb_library_version()) ?? "unknown";

    internal nint Handle => _connection != 0 ? _connection : throw new InvalidOperationException("The connection is closed.");

    [AllowNull]
    public override string ConnectionString
    {
        get => $"Data Source={_path};ACCESS_MODE=READ_ONLY";
        set => throw new NotSupportedException();
    }

    public override string Database => "main";

    public override string DataSource => _path;

    public override string ServerVersion => LibraryVersion;

    public override ConnectionState State => _connection != 0 ? ConnectionState.Open : ConnectionState.Closed;

    public override void Open()
    {
        if (_connection != 0)
        {
            return;
        }

        if (NativeMethods.duckdb_create_config(out var config) != NativeMethods.Success)
        {
            throw new NativeDuckDbException("could not create a DuckDB configuration");
        }

        try
        {
            Set(config, "access_mode", "READ_ONLY");
            Set(config, "enable_external_access", "false");
            Set(config, "autoinstall_known_extensions", "false");
            Set(config, "autoload_known_extensions", "false");
            Set(config, "lock_configuration", "true");
            if (NativeMethods.duckdb_open_ext(_path, out _database, config, out var error) != NativeMethods.Success)
            {
                var message = NativeMethods.Text(error) ?? "unknown error";
                NativeMethods.duckdb_free(error);
                throw new NativeDuckDbException($"could not open {_path}: {message}");
            }
        }
        finally
        {
            NativeMethods.duckdb_destroy_config(ref config);
        }

        if (NativeMethods.duckdb_connect(_database, out _connection) != NativeMethods.Success)
        {
            NativeMethods.duckdb_close(ref _database);
            throw new NativeDuckDbException($"could not connect to {_path}");
        }
    }

    private static void Set(nint config, string name, string value)
    {
        if (NativeMethods.duckdb_set_config(config, name, value) != NativeMethods.Success)
        {
            throw new NativeDuckDbException($"DuckDB rejected the setting {name}={value}");
        }
    }

    /// <summary>Stops the statement running on this connection; it then fails with an interrupt error.</summary>
    public void Interrupt()
    {
        if (_connection != 0)
        {
            NativeMethods.duckdb_interrupt(_connection);
        }
    }

    public override void Close()
    {
        if (_connection != 0)
        {
            NativeMethods.duckdb_disconnect(ref _connection);
        }

        if (_database != 0)
        {
            NativeMethods.duckdb_close(ref _database);
        }
    }

    protected override void Dispose(bool disposing)
    {
        Close();
        base.Dispose(disposing);
    }

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException("The connection is read-only.");

    protected override DbCommand CreateDbCommand() => new NativeCommand(this);

    public new NativeCommand CreateCommand() => new(this);
}

/// <summary>An error DuckDB reported, with its message.</summary>
public sealed class NativeDuckDbException(string message) : DbException(message);

/// <summary>A statement for a <see cref="NativeConnection"/>; parameters bind by name (<c>$id</c> binds <c>id</c>).</summary>
public sealed class NativeCommand : DbCommand
{
    private NativeConnection _connection;
    private readonly NativeParameterCollection _parameters = [];

    internal NativeCommand(NativeConnection connection) => _connection = connection;

    [AllowNull]
    public override string CommandText { get; set; } = "";

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    /// <summary>When set, the statement must be a SELECT (or WITH … SELECT) as DuckDB's own parser classifies it.</summary>
    public bool SelectOnly { get; set; }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = value as NativeConnection ?? throw new ArgumentException("A NativeCommand needs a NativeConnection.");
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() => _connection.Interrupt();

    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => new NativeParameter();

    public override int ExecuteNonQuery()
    {
        using var reader = ExecuteDbDataReader(CommandBehavior.Default);
        return reader.RecordsAffected;
    }

    public override object? ExecuteScalar()
    {
        using var reader = ExecuteDbDataReader(CommandBehavior.Default);
        return reader.Read() && reader.FieldCount > 0 && !reader.IsDBNull(0) ? reader.GetValue(0) : null;
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        if (NativeMethods.duckdb_prepare(_connection.Handle, CommandText, out var prepared) != NativeMethods.Success)
        {
            var message = NativeMethods.Text(NativeMethods.duckdb_prepare_error(prepared)) ?? "could not prepare the statement";
            NativeMethods.duckdb_destroy_prepare(ref prepared);
            throw new NativeDuckDbException(message);
        }

        try
        {
            if (SelectOnly && NativeMethods.duckdb_prepared_statement_type(prepared) != NativeMethods.StatementSelect)
            {
                throw new NativeDuckDbException("only a SELECT statement (or WITH … SELECT) can run here");
            }

            foreach (NativeParameter parameter in _parameters)
            {
                Bind(prepared, parameter);
            }

            var state = NativeMethods.duckdb_execute_prepared(prepared, out var result);
            if (state != NativeMethods.Success)
            {
                var message = NativeMethods.Text(NativeMethods.duckdb_result_error(ref result)) ?? "the statement failed";
                NativeMethods.duckdb_destroy_result(ref result);
                throw new NativeDuckDbException(message);
            }

            return new NativeReader(result);
        }
        finally
        {
            NativeMethods.duckdb_destroy_prepare(ref prepared);
        }
    }

    private static void Bind(nint prepared, NativeParameter parameter)
    {
        var name = parameter.ParameterName.TrimStart('$');
        if (NativeMethods.duckdb_bind_parameter_index(prepared, out var index, name) != NativeMethods.Success)
        {
            // A parameter the statement does not use, as ADO.NET providers allow.
            return;
        }

        var state = parameter.Value switch
        {
            null or DBNull => NativeMethods.duckdb_bind_null(prepared, index),
            string text => NativeMethods.duckdb_bind_varchar(prepared, index, text),
            bool flag => NativeMethods.duckdb_bind_boolean(prepared, index, flag),
            int number => NativeMethods.duckdb_bind_int32(prepared, index, number),
            long number => NativeMethods.duckdb_bind_int64(prepared, index, number),
            double number => NativeMethods.duckdb_bind_double(prepared, index, number),
            Enum value => NativeMethods.duckdb_bind_varchar(prepared, index, value.ToString()),
            var other => NativeMethods.duckdb_bind_varchar(prepared, index, Convert.ToString(other, CultureInfo.InvariantCulture) ?? ""),
        };
        if (state != NativeMethods.Success)
        {
            throw new NativeDuckDbException($"could not bind ${name}");
        }
    }
}

public sealed class NativeParameter : DbParameter
{
    public override DbType DbType { get; set; }

    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName { get; set; } = "";

    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn { get; set; } = "";

    public override bool SourceColumnNullMapping { get; set; }

    public override object? Value { get; set; }

    public override void ResetDbType()
    {
    }
}

public sealed class NativeParameterCollection : DbParameterCollection, IEnumerable<NativeParameter>
{
    private readonly List<NativeParameter> _items = [];

    public override int Count => _items.Count;

    public override object SyncRoot => _items;

    public void Add(NativeParameter parameter) => _items.Add(parameter);

    public override int Add(object value)
    {
        _items.Add(Cast(value));
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
        {
            Add(value);
        }
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(object value) => value is NativeParameter p && _items.Contains(p);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    IEnumerator<NativeParameter> IEnumerable<NativeParameter>.GetEnumerator() => _items.GetEnumerator();

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];

    public override int IndexOf(object value) => value is NativeParameter p ? _items.IndexOf(p) : -1;

    public override int IndexOf(string parameterName) => _items.FindIndex(p => p.ParameterName == parameterName);

    public override void Insert(int index, object value) => _items.Insert(index, Cast(value));

    public override void Remove(object value) => _items.Remove(Cast(value));

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));

    protected override void SetParameter(int index, DbParameter value) => _items[index] = Cast(value);

    protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = Cast(value);

    private static NativeParameter Cast(object value) =>
        value as NativeParameter ?? throw new ArgumentException("A NativeCommand takes NativeParameters; use CreateParameter().");
}

/// <summary>A forward-only reader over a materialised duckdb_result; destroys the result when disposed.</summary>
public sealed class NativeReader : DbDataReader
{
    private DuckResult _result;
    private readonly ulong _rows;
    private readonly int _columns;
    private long _row = -1;
    private bool _closed;

    internal NativeReader(DuckResult result)
    {
        _result = result;
        _rows = NativeMethods.duckdb_row_count(ref _result);
        _columns = (int)NativeMethods.duckdb_column_count(ref _result);
    }

    /// <summary>How many rows the statement returned in all.</summary>
    public long RowCount => (long)_rows;

    public override int FieldCount => _columns;

    public override bool HasRows => _rows > 0;

    public override bool IsClosed => _closed;

    public override int RecordsAffected => -1;

    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        if ((ulong)(_row + 1) >= _rows)
        {
            _row = (long)_rows;
            return false;
        }

        _row++;
        return true;
    }

    public override bool NextResult() => false;

    private ulong Row => _row >= 0 && (ulong)_row < _rows ? (ulong)_row : throw new InvalidOperationException("No current row; call Read first.");

    internal DuckType TypeOf(int ordinal) => (DuckType)NativeMethods.duckdb_column_type(ref _result, (ulong)ordinal);

    public override string GetName(int ordinal) => NativeMethods.Text(NativeMethods.duckdb_column_name(ref _result, (ulong)ordinal)) ?? "";

    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < _columns; i++)
        {
            if (string.Equals(GetName(i), name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new IndexOutOfRangeException($"no column named {name}");
    }

    public override bool IsDBNull(int ordinal) => NativeMethods.duckdb_value_is_null(ref _result, (ulong)ordinal, Row);

    public override string GetString(int ordinal)
    {
        var pointer = NativeMethods.duckdb_value_varchar(ref _result, (ulong)ordinal, Row);
        if (pointer == 0)
        {
            throw new InvalidCastException($"column {ordinal} is null");
        }

        try
        {
            return NativeMethods.Text(pointer)!;
        }
        finally
        {
            NativeMethods.duckdb_free(pointer);
        }
    }

    public override int GetInt32(int ordinal) => NativeMethods.duckdb_value_int32(ref _result, (ulong)ordinal, Row);

    public override long GetInt64(int ordinal) => NativeMethods.duckdb_value_int64(ref _result, (ulong)ordinal, Row);

    public override short GetInt16(int ordinal) => checked((short)GetInt32(ordinal));

    public override byte GetByte(int ordinal) => checked((byte)GetInt32(ordinal));

    public override bool GetBoolean(int ordinal) => NativeMethods.duckdb_value_boolean(ref _result, (ulong)ordinal, Row);

    public override double GetDouble(int ordinal) => NativeMethods.duckdb_value_double(ref _result, (ulong)ordinal, Row);

    public override float GetFloat(int ordinal) => (float)GetDouble(ordinal);

    public override decimal GetDecimal(int ordinal) => decimal.Parse(GetString(ordinal), CultureInfo.InvariantCulture);

    public override DateTime GetDateTime(int ordinal) => DateTime.Parse(GetString(ordinal), CultureInfo.InvariantCulture);

    public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));

    public override char GetChar(int ordinal) => GetString(ordinal)[0];

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override string GetDataTypeName(int ordinal) => TypeOf(ordinal).ToString().ToUpperInvariant();

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    public override Type GetFieldType(int ordinal) => TypeOf(ordinal) switch
    {
        DuckType.Boolean => typeof(bool),
        DuckType.TinyInt or DuckType.SmallInt or DuckType.Integer or DuckType.UTinyInt or DuckType.USmallInt => typeof(int),
        DuckType.BigInt or DuckType.UInteger or DuckType.UBigInt => typeof(long),
        DuckType.Float or DuckType.Double or DuckType.Decimal => typeof(double),
        _ => typeof(string),
    };

    /// <summary>bool, int, long, double or string by the column's DuckDB type (others as their text); DBNull for null.</summary>
    public override object GetValue(int ordinal)
    {
        if (IsDBNull(ordinal))
        {
            return DBNull.Value;
        }

        return TypeOf(ordinal) switch
        {
            DuckType.Boolean => GetBoolean(ordinal),
            DuckType.TinyInt or DuckType.SmallInt or DuckType.Integer or DuckType.UTinyInt or DuckType.USmallInt => GetInt32(ordinal),
            DuckType.BigInt or DuckType.UInteger or DuckType.UBigInt => GetInt64(ordinal),
            DuckType.Float or DuckType.Double or DuckType.Decimal => GetDouble(ordinal),
            _ => GetString(ordinal),
        };
    }

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, _columns);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public override IEnumerator GetEnumerator() => throw new NotSupportedException("Read the rows with Read().");

    public override void Close()
    {
        if (!_closed)
        {
            NativeMethods.duckdb_destroy_result(ref _result);
            _closed = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        Close();
        base.Dispose(disposing);
    }
}
