using System.Collections.Generic;
using ScadaQTNN.Models;

namespace ScadaQTNN.Presentation
{
    public interface IAlarmView
    {
        void ReplaceAll(IReadOnlyList<Alarm> alarms);
        void Upsert(Alarm alarm);
    }
}
