using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Conduit.DataAccess
{
    /// <summary>
    /// Base repository class providing common data access functionality
    /// </summary>
    public abstract class BaseRepository
    {
        protected readonly DatabaseConfig _config;

        /// <summary>
        /// Initializes a new instance of the BaseRepository class
        /// </summary>
        /// <param name="config">Database configuration</param>
        protected BaseRepository(DatabaseConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Creates a new database connection
        /// </summary>
        /// <returns>An open database connection</returns>
        protected IDbConnection CreateConnection()
        {
            var connection = new SqlConnection(_config.ConnectionString);
            connection.Open();
            return connection;
        }

        /// <summary>
        /// Executes a query and returns the results
        /// </summary>
        /// <typeparam name="T">The type of results to return</typeparam>
        /// <param name="sql">The SQL query</param>
        /// <param name="param">Query parameters</param>
        /// <returns>Query results</returns>
        protected async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null)
        {
            using var connection = CreateConnection();
            return await connection.QueryAsync<T>(sql, param, commandTimeout: _config.CommandTimeout);
        }

        /// <summary>
        /// Executes a query and returns a single result
        /// </summary>
        /// <typeparam name="T">The type of result to return</typeparam>
        /// <param name="sql">The SQL query</param>
        /// <param name="param">Query parameters</param>
        /// <returns>Single query result or null</returns>
        protected async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null)
        {
            using var connection = CreateConnection();
            return await connection.QuerySingleOrDefaultAsync<T>(sql, param, commandTimeout: _config.CommandTimeout);
        }

        /// <summary>
        /// Executes a command and returns the number of affected rows
        /// </summary>
        /// <param name="sql">The SQL command</param>
        /// <param name="param">Command parameters</param>
        /// <returns>Number of affected rows</returns>
        protected async Task<int> ExecuteAsync(string sql, object? param = null)
        {
            using var connection = CreateConnection();
            return await connection.ExecuteAsync(sql, param, commandTimeout: _config.CommandTimeout);
        }

        /// <summary>
        /// Executes a scalar query and returns the result
        /// </summary>
        /// <typeparam name="T">The type of result to return</typeparam>
        /// <param name="sql">The SQL query</param>
        /// <param name="param">Query parameters</param>
        /// <returns>Scalar result</returns>
        protected async Task<T?> ExecuteScalarAsync<T>(string sql, object? param = null)
        {
            using var connection = CreateConnection();
            return await connection.ExecuteScalarAsync<T>(sql, param, commandTimeout: _config.CommandTimeout);
        }

        /// <summary>
        /// Executes multiple queries and returns multiple result sets
        /// </summary>
        /// <param name="sql">The SQL query</param>
        /// <param name="param">Query parameters</param>
        /// <returns>Grid reader for multiple result sets</returns>
        protected async Task<SqlMapper.GridReader> QueryMultipleAsync(string sql, object? param = null)
        {
            var connection = CreateConnection();
            return await connection.QueryMultipleAsync(sql, param, commandTimeout: _config.CommandTimeout);
        }

        /// <summary>
        /// Begins a database transaction
        /// </summary>
        /// <returns>Database transaction</returns>
        protected IDbTransaction BeginTransaction()
        {
            var connection = CreateConnection();
            return connection.BeginTransaction();
        }

        /// <summary>
        /// Executes an action within a transaction
        /// </summary>
        /// <param name="action">The action to execute</param>
        protected async Task ExecuteInTransactionAsync(Func<IDbConnection, IDbTransaction, Task> action)
        {
            using var connection = CreateConnection();
            using var transaction = connection.BeginTransaction();
            
            try
            {
                await action(connection, transaction);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Executes a function within a transaction and returns the result
        /// </summary>
        /// <typeparam name="T">The type of result to return</typeparam>
        /// <param name="func">The function to execute</param>
        /// <returns>Function result</returns>
        protected async Task<T> ExecuteInTransactionAsync<T>(Func<IDbConnection, IDbTransaction, Task<T>> func)
        {
            using var connection = CreateConnection();
            using var transaction = connection.BeginTransaction();
            
            try
            {
                var result = await func(connection, transaction);
                transaction.Commit();
                return result;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }
}