﻿using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Medallion.Threading.Sql
{
    internal sealed class OwnedConnectionDistributedLock : IInternalSqlDistributedLock
    {
        private readonly string lockName, connectionString;

        public OwnedConnectionDistributedLock(string lockName, string connectionString)
        {
            this.lockName = lockName;
            this.connectionString = connectionString;
        }

        public IDisposable TryAcquire(int timeoutMillis, SqlApplicationLock.Mode mode, IDisposable contextHandle)
        {
            if (contextHandle != null)
            {
                return this.CreateContextLock(contextHandle).TryAcquire(timeoutMillis, mode, contextHandle: null);
            }

            IDisposable result = null;
            var connection = new SqlConnection(this.connectionString);
            try
            {
                connection.Open();
                if (SqlApplicationLock.ExecuteAcquireCommand(connection, this.lockName, timeoutMillis, mode))
                {
                    result = new LockScope(connection, this.lockName);
                }    
            }
            finally
            {
                // if we fail to acquire or throw, make sure to clean up the connection
                if (result == null)
                {
                    connection.Dispose();
                }
            }

            return result;
        }

        public async Task<IDisposable> TryAcquireAsync(int timeoutMillis, SqlApplicationLock.Mode mode, CancellationToken cancellationToken, IDisposable contextHandle)
        {
            if (contextHandle != null)
            {
                return await this.CreateContextLock(contextHandle).TryAcquireAsync(timeoutMillis, mode, cancellationToken, contextHandle: null).ConfigureAwait(false);
            }

            IDisposable result = null;
            var connection = new SqlConnection(this.connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (await SqlApplicationLock.ExecuteAcquireCommandAsync(connection, this.lockName, timeoutMillis, mode, cancellationToken).ConfigureAwait(false))
                {
                    result = new LockScope(connection, this.lockName);
                }
            }
            finally
            {
                // if we fail to acquire or throw, make sure to clean up the connection
                if (result == null)
                {
                    connection.Dispose();
                }
            }

            return result;
        }

        private IInternalSqlDistributedLock CreateContextLock(IDisposable contextHandle)
        {
            var connection = ((LockScope)contextHandle).Connection;
            if (connection == null) { throw new ObjectDisposedException(nameof(contextHandle), "the provided handle is already disposed"); }

            return new ConnectionScopedSqlDistributedLock(this.lockName, connection);
        }

        private sealed class LockScope : IDisposable
        {
            private SqlConnection connection;
            private readonly string lockName;

            public LockScope(SqlConnection connection, string lockName)
            {
                this.connection = connection;
                this.lockName = lockName;
            }

            public SqlConnection Connection => Volatile.Read(ref this.connection);

            public void Dispose()
            {
                var connection = Interlocked.Exchange(ref this.connection, null);
                if (connection != null && !connection.IsClosedOrBroken())
                {
                    ReleaseLock(connection, this.lockName);
                }
            }

            private static void ReleaseLock(SqlConnection connection, string lockName)
            {
                try
                {
                    // explicit release is required due to connection pooling. For a pooled connection,
                    // simply calling Dispose() will not release the lock: it just returns the connection
                    // to the pool
                    SqlApplicationLock.ExecuteReleaseCommand(connection, lockName);
                }
                finally
                {
                    connection.Dispose();
                }
            }
        }
    }
}
