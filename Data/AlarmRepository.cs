using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using ScadaQTNN.Models;

namespace ScadaQTNN.Data
{
    public class AlarmRepository : IAlarmRepository
    {
        public async Task<IReadOnlyList<Alarm>> GetLatestAsync(int top = 200)
        {
            const string query = @"
                SELECT TOP (@Top) Id, ErrorTime, WellId, ErrorCode, Description, IsHandled
                FROM dbo.Well_Alarm
                ORDER BY ErrorTime DESC";

            var dt = await ClassSQL.ExecuteQueryAsync(query, new SqlParameter("@Top", top)).ConfigureAwait(false);

            var result = new List<Alarm>(dt.Rows.Count);
            foreach (DataRow row in dt.Rows)
            {
                result.Add(new Alarm
                {
                    Id = Convert.ToInt32(row["Id"]),
                    ErrorTime = Convert.ToDateTime(row["ErrorTime"]),
                    WellId = Convert.ToInt32(row["WellId"]),
                    ErrorCode = Convert.ToInt32(row["ErrorCode"]),
                    Description = row["Description"] as string ?? string.Empty,
                    IsHandled = row["IsHandled"] != DBNull.Value && Convert.ToBoolean(row["IsHandled"])
                });
            }
            return result;
        }
    }
}
