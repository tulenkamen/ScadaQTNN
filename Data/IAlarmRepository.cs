using System.Collections.Generic;
using System.Threading.Tasks;
using ScadaQTNN.Models;

namespace ScadaQTNN.Data
{
    public interface IAlarmRepository
    {
        Task<IReadOnlyList<Alarm>> GetLatestAsync(int top = 200);
    }
}
