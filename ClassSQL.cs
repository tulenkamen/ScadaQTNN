using System;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;

namespace ScadaQTNN
{
    public static class ClassSQL
    {
        public static string GetConnectionString()
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = Properties.Settings.Default.Server,
                InitialCatalog = Properties.Settings.Default.Database,
                UserID = Properties.Settings.Default.User,
                Password = Properties.Settings.Default.Password,
                TrustServerCertificate = true
            };

            return builder.ConnectionString;
        }

        // Sync versions (giữ để tương thích)
        public static void ExecuteNonQuery(string query, params SqlParameter[] parameters)
        {
            using (var conn = new SqlConnection(GetConnectionString()))
            {
                conn.Open();
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.CommandTimeout = 5;
                    if (parameters != null && parameters.Length > 0)
                        cmd.Parameters.AddRange(parameters);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public static DataTable ExecuteQuery(string query, params SqlParameter[] parameters)
        {
            using (var conn = new SqlConnection(GetConnectionString()))
            {
                conn.Open();
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.CommandTimeout = 5;
                    if (parameters != null && parameters.Length > 0)
                        cmd.Parameters.AddRange(parameters);
                    using (var da = new SqlDataAdapter(cmd))
                    {
                        var dt = new DataTable();
                        da.Fill(dt);
                        return dt;
                    }
                }
            }
        }

        // Async versions - compatible với C# 7.3 / .NET Framework
        public static async Task<int> ExecuteNonQueryAsync(string query, params SqlParameter[] parameters)
        {
            using (var conn = new SqlConnection(GetConnectionString()))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.CommandTimeout = 5;
                    if (parameters != null && parameters.Length > 0)
                        cmd.Parameters.AddRange(parameters);
                    return await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }

        public static async Task<DataTable> ExecuteQueryAsync(string query, params SqlParameter[] parameters)
        {
            using (var conn = new SqlConnection(GetConnectionString()))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.CommandTimeout = 5;
                    if (parameters != null && parameters.Length > 0)
                        cmd.Parameters.AddRange(parameters);

                    // SqlDataAdapter.Fill không có API async — chạy trong Task.Run để tránh block thread gọi
                    using (var da = new SqlDataAdapter(cmd))
                    {
                        var dt = new DataTable();
                        await Task.Run(() => da.Fill(dt)).ConfigureAwait(false);
                        return dt;
                    }
                }
            }
        }
    }
}