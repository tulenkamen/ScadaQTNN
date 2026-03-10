using System;

namespace ScadaQTNN.Models
{
    public class Alarm
    {
        public int Id { get; set; }
        public DateTime ErrorTime { get; set; }
        public int WellId { get; set; }
        public string WellName { get; set; }
        public int ErrorCode { get; set; }
        public string Description { get; set; }
        public bool IsHandled { get; set; }
    }
}
