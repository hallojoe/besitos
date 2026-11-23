using System.Data;
using System.Data.Common;

namespace Besitos.Storage;

internal sealed class QueueCommandFactory(SqlDialect dialect)
{
    public DbCommand Create(DbConnection connection, string sql,
        IEnumerable<(string Name, object? Value)> values, DbTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 5;
        command.Transaction = transaction;
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@" + name;
            parameter.Value = value ?? DBNull.Value;
            if (value is null) parameter.DbType = DbType.String;
            if (value is DateTime && dialect.Engine == DatabaseEngine.SqlServer) parameter.DbType = DbType.DateTime2;
            command.Parameters.Add(parameter);
        }
        return command;
    }
}
