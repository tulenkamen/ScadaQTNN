
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using S7.Net;
using S7.Net.Types;
using System.Data;
using System.Data.SqlClient;

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
        // ===== INSERT / UPDATE / DELETE =====
        public static void ExecuteNonQuery(string query, params SqlParameter[] parameters)
        {
            using (SqlConnection conn = new SqlConnection(GetConnectionString()))
            {
                conn.Open();

                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    cmd.CommandTimeout = 3;
                    if (parameters != null)
                        cmd.Parameters.AddRange(parameters);

                    cmd.ExecuteNonQuery();
                }
            }
        }

        // ===== SELECT trả về DataTable =====
        public static DataTable ExecuteQuery(string query, params SqlParameter[] parameters)
        {
            using (SqlConnection conn = new SqlConnection(GetConnectionString()))
            {
                conn.Open();

                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    cmd.CommandTimeout = 3;
                    if (parameters != null)
                        cmd.Parameters.AddRange(parameters);

                    using (SqlDataAdapter da = new SqlDataAdapter(cmd))
                    {
                        DataTable dt = new DataTable();
                        da.Fill(dt);
                        return dt;
                    }
                }
            }
        }
    }
}


