using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using S7.Net;
using S7.Net.Types;
using SymbolFactoryDotNet;
using System.Data.SqlClient;
using System.Threading;

namespace ScadaQTNN
{

    public partial class Form1 : Form
    {
        // ===== BIẾN TOÀN CỤC =====
        PlcService plc;
        WellStatus[] Wells;
        bool isEditting = false;
        short mode;
        private DateTime _lastErrorShown = DateTime.MinValue;
        private readonly TimeSpan _errorCooldown = TimeSpan.FromSeconds(20);
        private bool _errorShowing = false;
        private ushort currentstatusOnT1 = 0;
        private ushort currentstatusOnT2 = 0;
        Dictionary<Panel, (int top, int height)> panelOrigin = new Dictionary<Panel, (int, int)>();
        Dictionary<int, StandardControl> standardControls;
        Dictionary<int, List<int>> wellToControls = new Dictionary<int, List<int>>
        {
            {2, new List<int>{79,67,80,81,92,91,93,34,37}},
            {3, new List<int>{80,81,92,91,93,34,37}},
            {4, new List<int>{81,34,92,91,93}},
            {5, new List<int>{79,67,80,37,81,34,92,91,93}},
            {6, new List<int>{79,67,80,37,81,34,92,91,93}},
            {7, new List<int>{80,37,81,34,92,91,93}},
            {1, new List<int>{81,34,92,91,93}},
        };
        float[] MaxWaterLevels = new float[]
{
            46.0f, // Giếng 1
            15.0f, // Giếng 2
            34.0f, // Giếng 3
            42.0f, // Giếng 4
            40.0f, // Giếng 5
            25.0f, // Giếng 6
            43.0f  // Giếng 7
};
        Panel[] waterPanels;
        private int _lastLevel = -1;
        private bool _isReading = false;
        private List<WellUI> wellUIs = new List<WellUI>();

        private Dictionary<Panel, Color> originalColors = new Dictionary<Panel, Color>();
        private Color editBackColor = Color.DarkCyan;
        private Color editBorderColor = Color.Cyan;

        private bool[] wellFaultState = new bool[7];       // Trạng thái fault trước đó
        private ushort[] lastErrorCode = new ushort[7];    // Mã lỗi trước đó
        private bool[] wellCommState = new bool[7];

        private PollingService _pollingService;
        private CancellationTokenSource _readLoopCts;
        private readonly object _wellsLock = new object();

        private void ShowErrorOnce(string message)
        {
            if (_errorShowing) return;

            if (DateTime.Now - _lastErrorShown < _errorCooldown)
                return;

            _errorShowing = true;
            _lastErrorShown = DateTime.Now;

            MessageBox.Show(
                message,
                "Lỗi truyền thông PLC",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );

            _errorShowing = false;
        }
        public Form1()
        {
            InitializeComponent();
            this.DoubleBuffered = true;
        }
        #region BUFFER_KHOI_TAO_GIA_TRI_GIENG
        private int GetPriority(byte state)
        {
            switch (state)
            {
                case 1: return 3; // cao nhất
                case 2: return 2;
                case 0: return 1;
                default: return 0;
            }
        }
        private string ParseStatus(short status)
        {
            switch (status)
            {
                case 0: return "STOP";
                case 1: return "START";
                case 2: return "FAULT";
                default: return "UNKNOWN";
            }
        }
        public static string RunModeToText(ushort runMode)
        {
            switch (runMode)
            {
                case 0: return "STOP";
                case 1: return "START";
                default: return "FAULT";
            }
        }
        private void ShowEditableControlMode(ComboBox cb, string currentValue)
        {
            cb.Items.Clear();
            cb.Items.Add("AUTO");
            cb.Items.Add("REMOTE");

            // Nếu PLC đang AUTO/REMOTE thì select theo
            if (cb.Items.Contains(currentValue))
                cb.SelectedItem = currentValue;
            else
                cb.SelectedIndex = 0; // mặc định AUTO

            cb.Enabled = true;
        }
        private void ShowEditableRunMode(ComboBox cb, string currentValue)
        {
            cb.Items.Clear();
            cb.Items.Add("START");
            cb.Items.Add("STOP");

            if (cb.Items.Contains(currentValue))
                cb.SelectedItem = currentValue;
            else
                cb.SelectedIndex = 0; // mặc định AUTO

            cb.Enabled = true;
        }
        private void ShowPlcControlMode(ComboBox cb, string plcValue)
        {
            if (cb.Text != plcValue)
            {
                cb.Items.Clear();
                cb.Items.Add(plcValue);
                cb.SelectedIndex = 0;
            }
            if (cb.Enabled) cb.Enabled = false;
        }
        private void UpdateAllSymbols()
        {
            Dictionary<int, byte> aggregatedStates = new Dictionary<int, byte>();

            for (int i = 0; i < Wells.Length; i++)
            {
                int wellNumber = i + 1;
                byte state = (byte)Wells[i].RunMode;

                if (!wellToControls.ContainsKey(wellNumber))
                    continue;

                foreach (int controlId in wellToControls[wellNumber])
                {
                    if (!aggregatedStates.ContainsKey(controlId))
                    {
                        aggregatedStates[controlId] = state;
                    }
                    else
                    {
                        byte current = aggregatedStates[controlId];

                        if (GetPriority(state) > GetPriority(current))
                        {
                            aggregatedStates[controlId] = state;
                        }
                    }

                }
            }

            // Update symbol 1 lần duy nhất
            foreach (var kv in aggregatedStates)
            {
                if (standardControls.ContainsKey(kv.Key))
                {
                    multipeState(standardControls[kv.Key], kv.Value);
                }
            }
        }
        // 1. Symbol with 2 state (bit state symbol)
        public static void twoState(StandardControl symbol, bool tag_value)
        {
            if (tag_value == true)
            {
                symbol.DiscreteValue1 = true;
            }
            else
            {
                symbol.DiscreteValue1 = false;
            }
        }
        // 2. Symbol with multipe state (word state symbol)
        public static void multipeState(StandardControl symbol, byte tag_value)
        {
            // Nếu trạng thái không đổi thì khỏi update
            if (symbol.Tag is byte oldValue && oldValue == tag_value)
                return;

            symbol.Tag = tag_value;

            symbol.DiscreteValue1 = tag_value == 1;
            symbol.DiscreteValue2 = tag_value == 2;
            symbol.DiscreteValue3 = tag_value == 3;
            symbol.DiscreteValue4 = tag_value == 4;
            symbol.DiscreteValue5 = tag_value == 5;
        }
        private void UpdateWaterLevel(
    Panel waterPanel,
    float currentLevel,
    float maxLevel)
        {
            if (waterPanel == null) return;

            if (!panelOrigin.ContainsKey(waterPanel)) return;
            var origin = panelOrigin[waterPanel];

            // ===== CHECK GIÁ TRỊ LỖI =====
            if (maxLevel <= 0 ||
                float.IsNaN(currentLevel) ||
                float.IsInfinity(currentLevel) ||
                currentLevel < 0 ||
                currentLevel > maxLevel)
            {
                waterPanel.Height = 10;
                waterPanel.Top = origin.top + (origin.height - 10);
                return;
            }

            // ===== SCALE =====
            int newHeight = (int)(currentLevel / maxLevel * origin.height);
            newHeight = Math.Max(10, newHeight);

            waterPanel.Top = origin.top;     // GIỮ ĐỈNH
            waterPanel.Height = newHeight;   // SCALE XUỐNG

        }
        private void UpdateWaterLevel2(Panel waterPanel, int level)
        {
            if (waterPanel == null) return;
            if (!panelOrigin.ContainsKey(waterPanel)) return;
            if (level < 0 || level > 3) return;

            var origin = panelOrigin[waterPanel];

            int newHeight;
            switch (level)
            {
                case 0: newHeight = origin.height; break;           // 100%
                case 1: newHeight = origin.height * 75 / 100; break;
                case 2: newHeight = origin.height * 45 / 100; break;
                case 3: newHeight = origin.height * 15 / 100; break;
                default: return;
            }

            waterPanel.Height = newHeight;
            waterPanel.Top = origin.top;     // GIỮ ĐỈNH
        }
        private void UpdateWaterLevelBool(Panel waterPanel, bool levelHigh)
        {
            if (waterPanel == null) return;
            if (!panelOrigin.ContainsKey(waterPanel)) return;

            var origin = panelOrigin[waterPanel];

            int newHeight = levelHigh ? 10 : 120;
            newHeight = Math.Min(newHeight, origin.height);

            waterPanel.Top = origin.top;     // GIỮ ĐỈNH
            waterPanel.Height = newHeight;   // CO XUỐNG
        }

        private void OnlyNumber_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (!char.IsControl(e.KeyChar) &&
                !char.IsDigit(e.KeyChar) &&
                e.KeyChar != '.')
            {
                e.Handled = true;
            }

            // Không cho nhập nhiều dấu .
            TextBox tb = sender as TextBox;
            if (e.KeyChar == '.' && tb.Text.Contains("."))
            {
                e.Handled = true;
            }
        }
                private static readonly Dictionary<ushort, string> FaultMap =
            new Dictionary<ushort, string>
        {
    {0x0000, "Không lỗi"},
    {0x0F01, "[COMMUNICATION FAULT] Mất truyền thông giếng"},

    {0x0010, "[INVERTER FAULT] E.OC1 - Quá dòng"},
    {0x0011, "[INVERTER FAULT] E.OC2 - Quá dòng"},
    {0x0012, "[INVERTER FAULT] E.OC3 - Quá dòng"},

    {0x0020, "[INVERTER FAULT] E.OV1 - Quá áp"},
    {0x0021, "[INVERTER FAULT] E.OV2 - Quá áp"},
    {0x0022, "[INVERTER FAULT] E.OV3 - Quá áp"},

    {0x0030, "[INVERTER FAULT] E.THT - Quá nhiệt"},
    {0x0031, "[INVERTER FAULT] E.THM - Quá nhiệt"},

    {0x0040, "[INVERTER FAULT] E.FIN - Lỗi quạt"},
    {0x0052, "[INVERTER FAULT] E.ILF - Mất pha"},
    {0x0060, "[INVERTER FAULT] E.OLT - Quá tải"},
    {0x0070, "[INVERTER FAULT] E.BE - Lỗi hãm"},
    {0x0080, "[INVERTER FAULT] E.GF - Chạm đất"},
    {0x0081, "[INVERTER FAULT] E.LF - Mất pha"},

    {0x0090, "[INVERTER FAULT] E.OHT - Quá nhiệt"},
    {0x0091, "[INVERTER FAULT] E.PTC - Lỗi PTC"},

    {0x00B0, "[INVERTER FAULT] E.PE - Lỗi tham số"},
    {0x00B1, "[INVERTER FAULT] E.PUE - Lỗi tham số"},
    {0x00B2, "[INVERTER FAULT] E.RET - Lỗi AutoTune"},

    {0x00C0, "[INVERTER FAULT] E.CPU - Lỗi CPU"},
    {0x00C4, "[INVERTER FAULT] E.CDO - Lỗi Digital Out"},
    {0x00C5, "[INVERTER FAULT] E.IOH - Lỗi I/O"},
    {0x00C7, "[INVERTER FAULT] E.AIE - Lỗi Analog"},
    {0x00C9, "[INVERTER FAULT] E.SAF - Lỗi an toàn"},

    {0x00F5, "[INVERTER FAULT] E.5 - Lỗi không xác định"}
        };

        private string GetFaultText(ushort code)
        {
            Console.WriteLine($"ErrorCode DEC: {code}");
            Console.WriteLine($"ErrorCode HEX: 0x{code:X4}");

            if (FaultMap.ContainsKey(code))
                return FaultMap[code];

            return $"UNKNOWN (0x{code:X4})";
        }

        private void SetupAlarmGrid()
        {
            dataGridView1.Dock = DockStyle.Fill; // full khung cố định
            dataGridView1.AllowUserToResizeRows = false;
            dataGridView1.AllowUserToResizeColumns = false;
            dataGridView1.AllowUserToAddRows = false;
            dataGridView1.ReadOnly = true;
            dataGridView1.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

            dataGridView1.ScrollBars = ScrollBars.Vertical; // có thanh kéo dọc
            dataGridView1.ColumnHeadersDefaultCellStyle.BackColor = Color.DarkRed;
            dataGridView1.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            dataGridView1.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            dataGridView1.EnableHeadersVisualStyles = false;

            dataGridView1.RowTemplate.Height = 28;
        }
        private Dictionary<int, string> wellNameMap = new Dictionary<int, string>
{
    {1, "Giếng NL.08"},
    {2, "Giếng NL.02"},
    {3, "Giếng NL.03"},
    {4, "Giếng NL.04"},
    {5, "Giếng NL.05"},
    {6, "Giếng NL.06"},
    {7, "Giếng NL.07"}
};
        private BindingSource alarmSource = new BindingSource();

        private void LoadAlarmGrid()
        {
            try
            {
                string query = @"
        SELECT 
            Id,
            ErrorTime,
            WellId,
            ErrorCode,
            Description,
            IsHandled
        FROM dbo.Well_Alarm
        ORDER BY ErrorTime DESC";

                DataTable dt = ClassSQL.ExecuteQuery(query);

                // ==========================
                // 🔥 MAP WELL ID → WELL NAME
                // ==========================
                dt.Columns.Add("WellName", typeof(string));

                foreach (DataRow row in dt.Rows)
                {
                    int id = Convert.ToInt32(row["WellId"]);

                    if (wellNameMap.ContainsKey(id))
                        row["WellName"] = wellNameMap[id];
                    else
                        row["WellName"] = "Unknown";
                }

                dt.Columns.Remove("WellId");          // bỏ cột id cũ
                dt.Columns["WellName"].SetOrdinal(2); // đưa về vị trí thứ 3

                alarmSource.DataSource = dt;
                dataGridView1.DataSource = alarmSource;


                // ==========================
                // 🔥 CẤU HÌNH GRID
                // ==========================

                dataGridView1.Columns["Id"].HeaderText = "ID";
                dataGridView1.Columns["ErrorTime"].HeaderText = "Thời gian";
                dataGridView1.Columns["WellName"].HeaderText = "Tên trạm";
                dataGridView1.Columns["ErrorCode"].HeaderText = "Mã lỗi";
                dataGridView1.Columns["Description"].HeaderText = "Mô tả";
                dataGridView1.Columns["IsHandled"].HeaderText = "Đã xử lý";

                // Thanh cuộn
                dataGridView1.ScrollBars = ScrollBars.Vertical;

                // Tô màu trạng thái
                foreach (DataGridViewRow row in dataGridView1.Rows)
                {
                    bool handled = Convert.ToBoolean(row.Cells["IsHandled"].Value);

                    if (!handled)
                        row.DefaultCellStyle.BackColor = Color.MistyRose;   // chưa xử lý
                    else
                        row.DefaultCellStyle.BackColor = Color.Honeydew;    // đã xử lý
                }
            }
            catch (Exception ex)
            {
                ShowErrorOnce(ex.Message);
            }
        }

        private void MarkAlarmHandled(int alarmId)
        {
            foreach (DataGridViewRow row in dataGridView1.Rows)
            {
                if ((int)row.Cells["Id"].Value == alarmId)
                {
                    row.Cells["IsHandled"].Value = true;
                    row.DefaultCellStyle.BackColor = Color.Honeydew;
                    break;
                }
            }
        }


        #endregion
        #region BUFFER_KHOI_TAO_HAM_DIEU_KHIEN
        public static class PlcConvert
        {
            public static ushort ToUInt16(byte[] buffer, int index)
            {
                return (ushort)((buffer[index] << 8) + buffer[index + 1]);
            }



            public static float ToFloat(byte[] buffer, int index)
            {
                byte[] temp = new byte[4];
                temp[0] = buffer[index + 3];
                temp[1] = buffer[index + 2];
                temp[2] = buffer[index + 1];
                temp[3] = buffer[index + 0];
                return BitConverter.ToSingle(temp, 0);
            }
        }
        public class WellStatus
        {
            public ushort RunMode;
            public float Frequency;
            public ushort ControlMode;
            public ushort BitCheck;
            public float WaterLevel;
            public float Flow;
            public float TotalFlow;
            public ushort TankFloatLevel;
            public float TankCurrent;
            public float TankVoltage;
            public ushort ErrorCode;
            public bool IsRemoteFlag;
            public bool IsEditable => ControlMode == 1 && IsRemoteFlag;

            public string ControlModeText
            {
                get
                {
                    if (ControlMode == 0)
                        return "MANUAL";
                    else if (ControlMode == 1)
                        return IsRemoteFlag ? "REMOTE" : "AUTO";
                    else
                        return "UNKNOWN";
                }
            }
            public string RunModeText
            {
                get
                {
                    switch (RunMode)
                    {
                        case 0: return "STOP";
                        case 1: return "START";
                        default: return "FAULT";
                    }
                }
            }
        }
        public class PlcService
        {
            private Plc _plc;

            public const int DB_NUMBER = 5;
            public const int DB_NUMBER_WRITE = 28;
            public const int WELL_BASE_OFFSET = 56;
            public const int WELL_BLOCK_SIZE = 34;

            public PlcService(string ip)
            {
                _plc = new Plc(CpuType.S71200, ip, 0, 1);
            }
            public void WriteBit(string address, bool value)
            {
                if (_plc.IsConnected)
                    _plc.Write(address, value);
            }
            public void WriteInt(string dbAddress, ushort value)
            {
                if (_plc.IsConnected)
                    _plc.Write(dbAddress, value);
            }
            public void WriteFloat(string dbAddress, double value)
            {
                if (_plc.IsConnected)
                    _plc.Write(dbAddress, value.ConvertToUInt());
            }

            public bool Connect()
            {
                return _plc.Open() == ErrorCode.NoError;
            }

            public void Disconnect()
            {
                if (_plc.IsConnected)
                    _plc.Close();
            }
            public byte[] ReadDBRange(int dbNumber, int start, int end)
            {
                if (!_plc.IsConnected) return null;
                int length = end - start + 1;
                return (byte[])_plc.ReadBytes(DataType.DataBlock, dbNumber, start, length);
            }
            public bool ReadBoolDB(int dbNumber, int byteIndex, int bitIndex)
            {
                if (!_plc.IsConnected) return false;

                byte[] buffer = (byte[])_plc.ReadBytes(
                    DataType.DataBlock,
                    dbNumber,
                    byteIndex,
                    1);

                if (buffer == null || buffer.Length < 1)
                    return false;

                return (buffer[0] & (1 << bitIndex)) != 0;
            }

            public void ReadWells(WellStatus[] wells)
            {
                int[] wellOffsets =
                {
                56, // Well 1
                90, // Well 2
                124, // Well 3
                158, // Well 4
                192, // Well 5
                226, // Well 6
                260  // Well 7
                };

                int firstOffset = wellOffsets[0];
                int lastOffset = wellOffsets[wells.Length - 1] + 33;
                int totalBytes = lastOffset - firstOffset +1;

                byte[] buffer = (byte[])_plc.ReadBytes(
                    DataType.DataBlock,
                    DB_NUMBER,
                    firstOffset,
                    totalBytes
                );

                for (int i = 0; i < wells.Length; i++)
                {
                    int o = wellOffsets[i] - firstOffset;

                    wells[i].RunMode = PlcConvert.ToUInt16(buffer, o + 0);
                    wells[i].Frequency = PlcConvert.ToFloat(buffer, o + 2);
                    wells[i].ControlMode = PlcConvert.ToUInt16(buffer, o + 6);
                    wells[i].BitCheck = PlcConvert.ToUInt16(buffer, o + 8);
                    wells[i].WaterLevel = PlcConvert.ToFloat(buffer, o + 10);
                    wells[i].Flow = PlcConvert.ToFloat(buffer, o + 14);
                    wells[i].TotalFlow = PlcConvert.ToFloat(buffer, o + 18);
                    wells[i].TankFloatLevel = PlcConvert.ToUInt16(buffer, o + 22);
                    wells[i].TankCurrent = PlcConvert.ToFloat(buffer, o + 24);
                    wells[i].TankVoltage = PlcConvert.ToFloat(buffer, o + 28);
                    wells[i].ErrorCode = PlcConvert.ToUInt16(buffer, o + 32);

                    Console.WriteLine($"===== WELL {i + 1} =====");
                    Console.WriteLine($"RunMode        : {wells[i].RunMode}");
                    Console.WriteLine($"Frequency      : {wells[i].Frequency}");
                    Console.WriteLine($"ControlMode    : {wells[i].ControlMode}");
                    Console.WriteLine($"BitCheck       : {wells[i].BitCheck}");
                    Console.WriteLine($"WaterLevel     : {wells[i].WaterLevel}");
                    Console.WriteLine($"Flow           : {wells[i].Flow}");
                    Console.WriteLine($"TotalFlow      : {wells[i].TotalFlow}");
                    Console.WriteLine($"TankFloatLevel : {wells[i].TankFloatLevel}");
                    Console.WriteLine($"TankCurrent    : {wells[i].TankCurrent}");
                    Console.WriteLine($"TankVoltage    : {wells[i].TankVoltage}");
                    Console.WriteLine($"ErrorCode      : {wells[i].ErrorCode}");
                    Console.WriteLine();

                }

            }
        }
        public class WellUI
        {
            public Panel Panel;
            public ComboBox CbControlMode;
            public ComboBox CbRunMode;
            public TextBox TbFreq;
            public List<Button> Buttons;
        }


        private void EditPanel_Paint(object sender, PaintEventArgs e)
        {
            Panel p = sender as Panel;

            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            int borderWidth = 2;

            Rectangle rect = new Rectangle(
                borderWidth / 2,
                borderWidth / 2,
                p.ClientSize.Width - borderWidth,
                p.ClientSize.Height - borderWidth);

            using (Pen pen = new Pen(editBorderColor, borderWidth))
            {
                pen.Alignment = System.Drawing.Drawing2D.PenAlignment.Center;
                e.Graphics.DrawRectangle(pen, rect);
            }
        }


        #endregion
        #region SQL_CONNECT

        // Chèn vào Form1.cs (private method)
        // 2) Thay thế ReadAndProcessOnceAsync bằng (thay cho method hiện có)
        private async Task ReadAndProcessOnceAsync(System.Threading.CancellationToken token)
        {
            try
            {
                if (isEditting) return; // nếu đang edit thì không đọc/ghi

                // 1) Đọc PLC vào mảng local (tránh ghi trực tiếp vào shared Wells)
                var localWells = new WellStatus[Wells.Length];
                for (int i = 0; i < localWells.Length; i++)
                    localWells[i] = new WellStatus();

                plc.ReadWells(localWells);

                // 2) Đọc db5 (remote bytes + tank + comm)
                byte[] db5 = plc.ReadDBRange(5, 296, 336);
                if (db5 == null || db5.Length == 0) return;

                // 3) Parse remote bytes → set IsRemoteFlag (giống logic trong timer1_Tick)
                int remoteStart = 322 - 296; // = 26
                int remoteLength = 336 - 322; // = 14
                if (db5.Length >= remoteStart + remoteLength)
                {
                    // remote bytes layout: pairs of bytes per well (same as trong timer1_Tick)
                    for (int i = 0; i < Math.Min(7, localWells.Length); i++)
                    {
                        int byteIndex = remoteStart + i * 2;
                        ushort val = (ushort)((db5[byteIndex] << 8) | db5[byteIndex + 1]);
                        localWells[i].IsRemoteFlag = val != 0;
                    }
                }

                // 4) Snapshot gồm wells copy + db5
                var snapshot = new
                {
                    WellsCopy = localWells,
                    Db5 = (byte[])db5.Clone()
                };

                // 5) Xử lý alarm/ghi DB (chạy ở background)
                await UpdateAlarmsAsync(snapshot).ConfigureAwait(false);

                // 6) Cập nhật UI / shared Wells trên UI thread
                this.BeginInvoke((Action)(() =>
                {
                    // Cập nhật shared Wells để các code khác tham chiếu (trên UI thread)
                    lock (_wellsLock)
                    {
                        Wells = CloneWellStatuses(snapshot.WellsCopy);
                    }

                    // Cập nhật UI (mình sẽ gọi ApplySnapshotToUI - bạn có thể mở rộng nội dung để match timer1_Tick)
                    ApplySnapshotToUI(snapshot);
                }));
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception ex)
            {
                this.BeginInvoke((Action)(() => ShowErrorOnce(ex.Message)));
            }
        }

        private WellStatus[] CloneWellStatuses(WellStatus[] source)
        {
            var arr = new WellStatus[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                var s = source[i];
                arr[i] = new WellStatus
                {
                    RunMode = s.RunMode,
                    Frequency = s.Frequency,
                    ControlMode = s.ControlMode,
                    BitCheck = s.BitCheck,
                    WaterLevel = s.WaterLevel,
                    Flow = s.Flow,
                    TotalFlow = s.TotalFlow,
                    TankFloatLevel = s.TankFloatLevel,
                    TankCurrent = s.TankCurrent,
                    TankVoltage = s.TankVoltage,
                    ErrorCode = s.ErrorCode,
                    IsRemoteFlag = s.IsRemoteFlag
                };
            }
            return arr;
        }

        // 3) Thay thế UpdateAlarmsAsync để parse commByte từ snapshot.Db5
        private async Task UpdateAlarmsAsync(object snapshotObj)
        {
            dynamic snapshot = snapshotObj;
            WellStatus[] wellsCopy = snapshot.WellsCopy;
            byte[] db5 = snapshot.Db5 as byte[] ?? new byte[0];

            // parse commByte if present
            byte commByte = 0;
            int offset316 = 316 - 296;
            if (db5.Length > offset316) commByte = db5[offset316];

            for (int i = 0; i < wellsCopy.Length; i++)
            {
                ushort currentError = wellsCopy[i].ErrorCode;
                bool inverterFault = currentError != 0 && wellsCopy[i].RunMode >= 2;
                bool commFault = (commByte & (1 << (i + 1))) != 0; // same logic as timer1_Tick

                if (inverterFault && !wellFaultState[i])
                {
                    string sql = @"
                INSERT INTO dbo.Well_Alarm (WellId, ErrorCode, ErrorTime, Description, IsHandled)
                VALUES (@WellId, @ErrorCode, @ErrorTime, @Description, 0)";
                    await ClassSQL.ExecuteNonQueryAsync(sql,
                        new SqlParameter("@WellId", i + 1),
                        new SqlParameter("@ErrorCode", (int)currentError),
                        new SqlParameter("@ErrorTime", DateTime.Now),
                        new SqlParameter("@Description", GetFaultText(currentError))
                    ).ConfigureAwait(false);

                    wellFaultState[i] = true;
                    lastErrorCode[i] = currentError;
                }
                else if (inverterFault && wellFaultState[i] && currentError != lastErrorCode[i])
                {
                    string sql = @"
                INSERT INTO dbo.Well_Alarm (WellId, ErrorCode, ErrorTime, Description, IsHandled)
                VALUES (@WellId, @ErrorCode, @ErrorTime, @Description, 0)";
                    await ClassSQL.ExecuteNonQueryAsync(sql,
                        new SqlParameter("@WellId", i + 1),
                        new SqlParameter("@ErrorCode", (int)currentError),
                        new SqlParameter("@ErrorTime", DateTime.Now),
                        new SqlParameter("@Description", GetFaultText(currentError))
                    ).ConfigureAwait(false);

                    lastErrorCode[i] = currentError;
                }

                if (commFault && !wellCommState[i])
                {
                    // insert comm alarm
                    string sql = @"
                INSERT INTO dbo.Well_Alarm (WellId, ErrorCode, ErrorTime, Description, IsHandled)
                VALUES (@WellId, @ErrorCode, @ErrorTime, @Description, 0)";
                    await ClassSQL.ExecuteNonQueryAsync(sql,
                        new SqlParameter("@WellId", i + 1),
                        new SqlParameter("@ErrorCode", 0x0F01),
                        new SqlParameter("@ErrorTime", DateTime.Now),
                        new SqlParameter("@Description", GetFaultText(0x0F01))
                    ).ConfigureAwait(false);
                    wellCommState[i] = true;
                }

                // update local states
                wellFaultState[i] = inverterFault;
                wellCommState[i] = commFault;
            }

            // reload grid on UI thread
            this.BeginInvoke((Action)(() => LoadAlarmGrid()));
        }

        private void ApplySnapshotToUI(dynamic snapshot)
        {
            WellStatus[] wellsCopy = snapshot.WellsCopy;
            byte[] db5 = snapshot?.Db5 as byte[] ?? new byte[0];
            if (wellsCopy.Length > 0)
            {
                // Hiển thị tần số giếng 1 (nếu control tồn tại)
                try
                {
                    // Cập nhật tank/freq/status tương tự như code cũ nhưng dùng wellsCopy
                    // Ví dụ cập nhật một số controls:

                    // ==== Hiển thị GIẾNG 8 ====
                    ShowPlcControlMode(comboBox38, wellsCopy[0].RunModeText);
            textBox37.Text = wellsCopy[0].Frequency.ToString("0.0");
            ShowPlcControlMode(comboBox39, wellsCopy[0].ControlModeText);
            textBox42.Text = wellsCopy[0].WaterLevel.ToString("0.00");
            textBox41.Text = wellsCopy[0].Flow.ToString("0.00");
            textBox40.Text = wellsCopy[0].TotalFlow.ToString("0.0");
            textBox49.Text = $"{wellsCopy[0].Flow:0.00} m3/h";
            textBox50.Text = $"{wellsCopy[0].WaterLevel:0.00} m";

            // ==== Hiển thị GIẾNG 2 ====
            ShowPlcControlMode(comboBox5, wellsCopy[1].RunModeText);
            textBox4.Text = wellsCopy[1].Frequency.ToString("0.0");
            ShowPlcControlMode(comboBox6, wellsCopy[1].ControlModeText);
            textBox1.Text = wellsCopy[1].WaterLevel.ToString("0.00");
            textBox2.Text = wellsCopy[1].Flow.ToString("0.00");
            textBox3.Text = wellsCopy[1].TotalFlow.ToString("0.0");
            textBox57.Text = $"{wellsCopy[1].Flow:0.00} m3/h";
            textBox58.Text = $"{wellsCopy[1].WaterLevel:0.00} m";

            // ==== Hiển thị GIẾNG 3 ====
            ShowPlcControlMode(comboBox8, wellsCopy[2].RunModeText);
            textBox7.Text = wellsCopy[2].Frequency.ToString("0.0");
            ShowPlcControlMode(comboBox9, wellsCopy[2].ControlModeText);
            textBox12.Text = wellsCopy[2].WaterLevel.ToString("0.00");
            textBox11.Text = wellsCopy[2].Flow.ToString("0.00");
            textBox10.Text = wellsCopy[2].TotalFlow.ToString("0.0");
            textBox59.Text = $"{wellsCopy[2].Flow:0.00} m3/h";
            textBox60.Text = $"{wellsCopy[2].WaterLevel:0.00} m";

            // ==== Hiển thị GIẾNG 4 ====
            ShowPlcControlMode(comboBox14, wellsCopy[3].RunModeText);
            textBox13.Text = wellsCopy[3].Frequency.ToString("0.0");
            ShowPlcControlMode(comboBox15, wellsCopy[3].ControlModeText);
            textBox18.Text = wellsCopy[3].WaterLevel.ToString("0.00");
            textBox17.Text = wellsCopy[3].Flow.ToString("0.00");
            textBox16.Text = wellsCopy[3].TotalFlow.ToString("0.0");
            textBox61.Text = $"{wellsCopy[3].Flow:0.00} m3/h";
            textBox62.Text = $"{wellsCopy[3].WaterLevel:0.00} m";

            // ==== Hiển thị GIẾNG 5 ====
            ShowPlcControlMode(comboBox20, wellsCopy[4].RunModeText);
            textBox19.Text = wellsCopy[4].Frequency.ToString("0.0");
            ShowPlcControlMode(comboBox21, wellsCopy[4].ControlModeText);
            textBox24.Text = wellsCopy[4].WaterLevel.ToString("0.00");
            textBox23.Text = wellsCopy[4].Flow.ToString("0.00");
            textBox22.Text = wellsCopy[4].TotalFlow.ToString("0.0");
            textBox55.Text = $"{wellsCopy[4].Flow:0.00} m3/h";
            textBox56.Text = $"{wellsCopy[4].WaterLevel:0.00} m";

            // ==== Hiển thị GIẾNG 6 ====
            ShowPlcControlMode(comboBox26, wellsCopy[5].RunModeText);
            textBox25.Text = wellsCopy[5].Frequency.ToString("0.0");
            ShowPlcControlMode(comboBox27, wellsCopy[5].ControlModeText);
            textBox30.Text = wellsCopy[5].WaterLevel.ToString("0.00");
            textBox29.Text = wellsCopy[5].Flow.ToString("0.00");
            textBox28.Text = wellsCopy[5].TotalFlow.ToString("0.0");
            textBox53.Text = $"{wellsCopy[5].Flow:0.00} m3/h";
            textBox54.Text = $"{wellsCopy[5].WaterLevel:0.00} m";

            // ==== Hiển thị GIẾNG 7 ====
            ShowPlcControlMode(comboBox32, wellsCopy[6].RunModeText);
            textBox31.Text = wellsCopy[6].Frequency.ToString("0.0");
            ShowPlcControlMode(comboBox33, wellsCopy[6].ControlModeText);
            textBox36.Text = wellsCopy[6].WaterLevel.ToString("0.00");
            textBox35.Text = wellsCopy[6].Flow.ToString("0.00");
            textBox34.Text = wellsCopy[6].TotalFlow.ToString("0.0");
            textBox52.Text = $"{wellsCopy[6].Flow:0.00} m3/h";
            textBox51.Text = $"{wellsCopy[6].WaterLevel:0.00} m";
                }
                catch { /* nếu control không tồn tại, bỏ qua */ }
            }
            // Cập nhật water panels:
            for (int i = 0; i < wellsCopy.Length && i < waterPanels.Length; i++)
            {
                UpdateWaterLevel(waterPanels[i], wellsCopy[i].WaterLevel, MaxWaterLevels[i]);
            }

            // Cập nhật các multipeState — chúng đã có check "if unchanged => return" nên không quá tốn
            multipeState(standardControl63, (byte)wellsCopy[0].RunMode);
            // ... các multipeState kh��c ...
            UpdateAllSymbols(); // nếu vẫn cần
        }

        public void InsertHistory(int wellId, double freq, double flow, double level)
        {
            string query = @"INSERT INTO Well_History
                     (WellId, TimeStamp, Frequency, Flow, WaterLevel)
                     VALUES
                     (@WellId, @TimeStamp, @Frequency, @Flow, @WaterLevel)";

            ClassSQL.ExecuteNonQuery(query,
                new SqlParameter("@WellId", wellId),
                new SqlParameter("@TimeStamp", DateTime.Now),
                new SqlParameter("@Frequency", freq),
                new SqlParameter("@Flow", flow),
                new SqlParameter("@WaterLevel", level)
            );
        }
        public DataTable GetHistory(int wellId)
        {
            string query = "SELECT * FROM Well_History WHERE WellId = @WellId ORDER BY TimeStamp DESC";

            return ClassSQL.ExecuteQuery(query,
                new SqlParameter("@WellId", wellId)
            );
        }
   
        private void InsertWellAlarm(int wellId, ushort errorCode)
        {

            if (errorCode == 0) return;
            string query = @"
        INSERT INTO dbo.Well_Alarm
        (WellId, ErrorCode, ErrorTime, Description, IsHandled)
        VALUES
        (@WellId, @ErrorCode, @ErrorTime, @Description, 0)";
            try
            {
                ClassSQL.ExecuteNonQuery(query,
                    new SqlParameter("@WellId", wellId),
                    new SqlParameter("@ErrorCode", (int)errorCode),
                    new SqlParameter("@ErrorTime", DateTime.Now),
                    new SqlParameter("@Description", GetFaultText(errorCode))
                );
            }
            catch (Exception ex)
            {
                ShowErrorOnce(ex.Message);
            }
            }


        #endregion
        private void Form1_Load(object sender, EventArgs e) 
        {

            int wellCount = 7; // số giếng
            Wells = new WellStatus[wellCount];

            for (int i = 0; i < wellCount; i++)
                Wells[i] = new WellStatus();

            plc = new PlcService("192.168.1.15");

            if (plc.Connect())
            {
                // Sử dụng timer designer (giữ logic UI lớn trong timer1_Tick)
                timer1.Interval = 250;
                timer1.Enabled = true;

                // Nếu có _pollingService đã tạo, dừng/hủy nó:
                _pollingService?.Stop();

            }
            waterPanels = new Panel[]
            {
                panel22, panel23, panel24, panel25, panel26, panel27, panel28, panel29, panel30, panel31, panel32
            };

            foreach (var p in waterPanels)
            {
                panelOrigin[p] = (p.Top, p.Height);
            }

            standardControls = new Dictionary<int, StandardControl>
            {
                {79, standardControl79},
                {80, standardControl80},
                {81, standardControl81},
                {67, standardControl67},
                {34, standardControl34},
                {37, standardControl37},
                {92, standardControl92},
                {91, standardControl91},
                {93, standardControl93},
                // thêm các control khác nếu cần
            };
            #region define_gieng
            //ADD TUAN TU CAC GIENG, THEO DUNG THU TU
            //GIENG 8
            wellUIs.Add(new WellUI
            {
                Panel = panel15,
                CbControlMode = comboBox39,
                CbRunMode = comboBox38,
                TbFreq = textBox37,
                Buttons = new List<Button> { button49, button48, button47, button21, button20, button19 }
            });
            //GIENG 2
            wellUIs.Add(new WellUI
            {
                Panel = panel3,
                CbControlMode = comboBox6,
                CbRunMode = comboBox5,
                TbFreq = textBox4,
                Buttons = new List<Button> { button31, button29, button28, button1, button2, button3 }
            });
            //GIENG 3
            wellUIs.Add(new WellUI
            {
                Panel = panel5,
                CbControlMode = comboBox9,
                CbRunMode = comboBox8,
                TbFreq = textBox7,
                Buttons = new List<Button> { button34, button33, button32, button6, button5, button4 }
            });
            //GIENG 4
            wellUIs.Add(new WellUI
            {
                Panel = panel7,
                CbControlMode = comboBox15,
                CbRunMode = comboBox14,
                TbFreq = textBox13,
                Buttons = new List<Button> { button37, button36, button35, button9, button8, button7 }
            });
            //GIENG 5
            wellUIs.Add(new WellUI
            {
                Panel = panel9,
                CbControlMode = comboBox21,
                CbRunMode = comboBox20,
                TbFreq = textBox19,
                Buttons = new List<Button> { button40, button39, button38, button12, button11, button10 }
            });
            //GIENG 6
            wellUIs.Add(new WellUI
            {
                Panel = panel11,
                CbControlMode = comboBox27,
                CbRunMode = comboBox26,
                TbFreq = textBox25,
                Buttons = new List<Button> { button43, button42, button41, button15, button14, button13 }
            });
            //GIENG 7
            wellUIs.Add(new WellUI
            {
                Panel = panel13,
                CbControlMode = comboBox33,
                CbRunMode = comboBox32,
                TbFreq = textBox31,
                Buttons = new List<Button> { button46, button45, button44, button18, button17, button16 }
            });
            #endregion

            SetupAlarmGrid();
            LoadAlarmGrid();
        }
        private void timer1_Tick(object sender, EventArgs e)
        {
            if (_isReading) return;
            _isReading = true;
                try
                {
                if (isEditting == false)
                {
                    plc.ReadWells(Wells);
                    byte[] db5 = plc.ReadDBRange(5, 296, 336);
                    //Remote Byte
                    int remoteStart = 322 - 296; // = 26
                    int remoteLength = 336 - 322; // = 14 byte

                    byte[] remoteBytes = new byte[remoteLength];
                    Array.Copy(db5, remoteStart, remoteBytes, 0, remoteLength);
                    //Tank Byte
                    int tankStart = 296 - 296; // = 0
                    int tankLength = 314 - 296; // = 18 byte

                    byte[] bufTank = new byte[tankLength];
                    Array.Copy(db5, tankStart, bufTank, 0, tankLength);

                    bool interLockRemoteG12 = (db5[314 - 296] & (1 << 7)) != 0;

                    int offset318 = 318 - 296;
                    bool ffStatus = (db5[offset318] & (1 << 0)) != 0;
                    bool van1 = (db5[offset318] & (1 << 1)) != 0;
                    bool van2 = (db5[offset318] & (1 << 2)) != 0;
                    bool bomTankTG = (db5[offset318] & (1 << 3)) != 0;
                    for (int i = 0; i < 7; i++)
                    {
                        int byteIndex = i * 2;
                        ushort val = (ushort)((remoteBytes[byteIndex] << 8) | remoteBytes[byteIndex + 1]);
                        Wells[i].IsRemoteFlag = val != 0; // 1 = REMOTE, 0 = AUTO
                    }

                    #region Capnhat_LoiSQL

                    int offset316 = 316 - 296;
                    byte commByte = db5[offset316];

                    for (int i = 0; i < Wells.Length; i++)
                    {
                        ushort currentError = Wells[i].ErrorCode;
                        bool inverterFault = currentError != 0 && Wells[i].RunMode >= 2;

                        bool commFault = (commByte & (1 << (i + 1))) != 0;

                        // =============================
                        // 1️⃣ LỖI BIẾN TẦN
                        // =============================

                        if (inverterFault && !wellFaultState[i])
                        {
                            InsertWellAlarm(i + 1, currentError);
                            LoadAlarmGrid();
                        }

                        if (inverterFault && wellFaultState[i] &&
                            currentError != lastErrorCode[i])
                        {
                            InsertWellAlarm(i + 1, currentError);
                            LoadAlarmGrid();
                        }

                        wellFaultState[i] = inverterFault;
                        lastErrorCode[i] = currentError;

                        // =============================
                        // 2️⃣ LỖI TRUYỀN THÔNG
                        // =============================

                        if (commFault && !wellCommState[i])
                        {
                            InsertWellAlarm(i + 1, 0x0F01);
                            LoadAlarmGrid();
                        }

                        wellCommState[i] = commFault;
                        Console.WriteLine($"Ki {i}      : {commFault}, {wellCommState[i]}");

                    }

                    #endregion



                    #region hienthi_trangthai_tanso_tank
                    if (bufTank == null || bufTank.Length < 18)
                        return;
                    // ---- INT ----
                    short statusOnT1 = (short)((bufTank[0] << 8) | bufTank[1]);   // 296-297
                    short statusOnT2 = (short)((bufTank[2] << 8) | bufTank[3]);   // 297-298
                    // ---- REAL ----
                    float freq1 = PlcConvert.ToFloat(bufTank, 4);   // 300-303
                    float freq2 = PlcConvert.ToFloat(bufTank, 8);   // 304-307
                    // ---- MODE INT ----
                    mode = (short)((bufTank[12] << 8) | bufTank[13]); // 308-309
                    button27.Visible = interLockRemoteG12;
                    button24.Visible = !interLockRemoteG12;
                    button25.Visible = true;
                    button22.Visible = true;
                    switch (mode)
                    {
                        case 0:
                            textBox45.Text = "MANUAL";
                            textBox46.Text = "MANUAL";
                            if (interLockRemoteG12 == true)
                            {
                                button26.Visible = false;
                                button23.Visible = false;
                            }
                            else
                            {
                                button26.Visible = false;
                                button23.Visible = false;
                            }
                            break;

                        case 1:
                            textBox45.Text = "AUTO";
                            textBox46.Text = "AUTO";
                            if (interLockRemoteG12 == true)
                            {
                                button26.Visible = true;
                                button23.Visible = true;
                            }
                            else
                            {
                                button26.Visible = false;
                                button23.Visible = false;
                            }
                            break;

                        case 2:
                            textBox45.Text = "MANUAL";
                            textBox46.Text = "AUTO";
                            if (interLockRemoteG12 == true)
                            {
                                button26.Visible = true;
                                button23.Visible = false;
                            }
                            else
                            {
                                button26.Visible = false;
                                button23.Visible = false;
                            }
                            break;

                        case 3:
                            textBox45.Text = "AUTO";
                            textBox46.Text = "MANUAL";
                            if (interLockRemoteG12 == true)
                            {
                                button26.Visible = false;
                                button23.Visible = true;
                            }
                            else
                            {
                                button26.Visible = false;
                                button23.Visible = false;
                            }
                            break;

                        default:
                            textBox45.Text = "MANUAL";
                            textBox46.Text = "MANUAL";
                            button26.Visible = false;
                            button23.Visible = false;
                            break;
                    }
                    textBox43.Text = freq1.ToString("0.0");
                    textBox44.Text = freq2.ToString("0.0");
                    comboBox1.Text = ParseStatus(statusOnT1);
                    comboBox3.Text = ParseStatus(statusOnT2);
                    currentstatusOnT1 = (ushort)statusOnT1;
                    currentstatusOnT2 = (ushort)statusOnT2;
                    #endregion
                    #region hienthi_level_tank
                    int level = Wells[1].TankFloatLevel;
                    UpdateWaterLevel2(panel29, level); UpdateWaterLevel2(panel30, level);
                    //Update trang thai nha may
                    UpdateWaterLevelBool(panel31, ffStatus); UpdateWaterLevelBool(panel32, ffStatus);
                    if (level != _lastLevel)
                    {
                        label80.Visible = false;
                        label81.Visible = false;
                        label82.Visible = false;

                        switch (level)
                        {
                            case 3:
                                label80.Visible = true;
                                break;

                            case 2:
                                label81.Visible = true;
                                break;

                            case 1:
                            case 0:
                                label82.Visible = true;
                                break;
                        }

                        _lastLevel = level;
                    }
                    label91.Visible = ffStatus;     // ff = 1 → hiện 91
                    label92.Visible = !ffStatus;    // ff = 0 → hiện 92
                    twoState(standardControl120, van1);
                    twoState(standardControl121, van2);
                    twoState(standardControl3, bomTankTG);
                    //Hien Thi Trang thai Tank
                    multipeState(standardControl96, (byte)statusOnT1);
                    multipeState(standardControl101, (byte)statusOnT1);
                    multipeState(standardControl98, (byte)statusOnT1);
                    multipeState(standardControl61, (byte)statusOnT1);

                    multipeState(standardControl97, (byte)statusOnT2);
                    multipeState(standardControl99, (byte)statusOnT2);
                    multipeState(standardControl100, (byte)statusOnT2);
                    if (statusOnT1 == 1 || statusOnT2 == 1)
                    {
                        multipeState(standardControl102, 1);
                        multipeState(standardControl94, 1);
                        multipeState(standardControl103, 1);
                        multipeState(standardControl110, 1);
                        multipeState(standardControl111, 1);
                        multipeState(standardControl118, 1);
                        multipeState(standardControl119, 1);
                        multipeState(standardControl113, 1);
                        multipeState(standardControl114, 1);
                    }
                    else
                    {
                        multipeState(standardControl102, 0);
                        multipeState(standardControl94, 0);
                        multipeState(standardControl103, 0);
                        multipeState(standardControl110, 0);
                        multipeState(standardControl111, 0);
                        multipeState(standardControl118, 0);
                        multipeState(standardControl119, 0);
                        multipeState(standardControl113, 0);
                        multipeState(standardControl114, 0);
                    }
                    #endregion
                    #region hienthi_button_chucnang
                    button20.Visible = Wells[0].IsEditable;
                    button2.Visible = Wells[1].IsEditable;
                    button5.Visible = Wells[2].IsEditable;
                    button8.Visible = Wells[3].IsEditable;
                    button11.Visible = Wells[4].IsEditable;
                    button14.Visible = Wells[5].IsEditable;
                    button17.Visible = Wells[6].IsEditable;

                    if (Wells[0].ControlMode == 1)
                    {
                        button21.Visible = true; 
                        button19.Visible = true;

                    }
                    else
                    {
                        button21.Visible = false;
                        button19.Visible = false;

                    }
                    if (Wells[1].ControlMode == 1)
                    {
                        button1.Visible = true;
                        button3.Visible = true;
                    }
                    else
                    {
                        button1.Visible = false;
                        button3.Visible = false;

                    }
                    if (Wells[2].ControlMode == 1)
                    {
                        button6.Visible = true;
                        button4.Visible = true;

                    }
                    else
                    {
                        button6.Visible = false;
                        button4.Visible = false;

                    }
                    if (Wells[3].ControlMode == 1)
                    {
                        button9.Visible = true;
                        button7.Visible = true;

                    }
                    else
                    {
                        button9.Visible = false;
                        button7.Visible = false;

                    }
                    if (Wells[4].ControlMode == 1)
                    {
                        button12.Visible = true;
                        button10.Visible = true;

                    }
                    else
                    {
                        button12.Visible = false;
                        button10.Visible = false;

                    }
                    if (Wells[5].ControlMode == 1)
                    {
                        button15.Visible = true;
                        button13.Visible = true;

                    }
                    else
                    {
                        button15.Visible = false;
                        button13.Visible = false;

                    }
                    if (Wells[6].ControlMode == 1)
                    {
                        button18.Visible = true;
                        button16.Visible = true;

                    }
                    else
                    {
                        button18.Visible = false;
                        button16.Visible = false;

                    }
                    #endregion
                    #region hienthi_gieng
                    // ==== Hiển thị GIẾNG 8 ====
                    ShowPlcControlMode(comboBox38, Wells[0].RunModeText);
                    textBox37.Text = Wells[0].Frequency.ToString("0.0");
                    ShowPlcControlMode(comboBox39, Wells[0].ControlModeText);
                    textBox42.Text = Wells[0].WaterLevel.ToString("0.00");
                    textBox41.Text = Wells[0].Flow.ToString("0.00");
                    textBox40.Text = Wells[0].TotalFlow.ToString("0.0");
                    textBox49.Text = $"{Wells[0].Flow:0.00} m3/h";
                    textBox50.Text = $"{Wells[0].WaterLevel:0.00} m";

                    // ==== Hiển thị GIẾNG 2 ====
                    ShowPlcControlMode(comboBox5, Wells[1].RunModeText);
                    textBox4.Text = Wells[1].Frequency.ToString("0.0");
                    ShowPlcControlMode(comboBox6, Wells[1].ControlModeText);
                    textBox1.Text = Wells[1].WaterLevel.ToString("0.00");
                    textBox2.Text = Wells[1].Flow.ToString("0.00");
                    textBox3.Text = Wells[1].TotalFlow.ToString("0.0");
                    textBox57.Text = $"{Wells[1].Flow:0.00} m3/h";
                    textBox58.Text = $"{Wells[1].WaterLevel:0.00} m";

                    // ==== Hiển thị GIẾNG 3 ====
                    ShowPlcControlMode(comboBox8, Wells[2].RunModeText);
                    textBox7.Text = Wells[2].Frequency.ToString("0.0");
                    ShowPlcControlMode(comboBox9, Wells[2].ControlModeText);
                    textBox12.Text = Wells[2].WaterLevel.ToString("0.00");
                    textBox11.Text = Wells[2].Flow.ToString("0.00");
                    textBox10.Text = Wells[2].TotalFlow.ToString("0.0");
                    textBox59.Text = $"{Wells[2].Flow:0.00} m3/h";
                    textBox60.Text = $"{Wells[2].WaterLevel:0.00} m";

                    // ==== Hiển thị GIẾNG 4 ====
                    ShowPlcControlMode(comboBox14, Wells[3].RunModeText);
                    textBox13.Text = Wells[3].Frequency.ToString("0.0");
                    ShowPlcControlMode(comboBox15, Wells[3].ControlModeText);
                    textBox18.Text = Wells[3].WaterLevel.ToString("0.00");
                    textBox17.Text = Wells[3].Flow.ToString("0.00");
                    textBox16.Text = Wells[3].TotalFlow.ToString("0.0");
                    textBox61.Text = $"{Wells[3].Flow:0.00} m3/h";
                    textBox62.Text = $"{Wells[3].WaterLevel:0.00} m";

                    // ==== Hiển thị GIẾNG 5 ====
                    ShowPlcControlMode(comboBox20, Wells[4].RunModeText);
                    textBox19.Text = Wells[4].Frequency.ToString("0.0");
                    ShowPlcControlMode(comboBox21, Wells[4].ControlModeText);
                    textBox24.Text = Wells[4].WaterLevel.ToString("0.00");
                    textBox23.Text = Wells[4].Flow.ToString("0.00");
                    textBox22.Text = Wells[4].TotalFlow.ToString("0.0");
                    textBox55.Text = $"{Wells[4].Flow:0.00} m3/h";
                    textBox56.Text = $"{Wells[4].WaterLevel:0.00} m";

                    // ==== Hiển thị GIẾNG 6 ====
                    ShowPlcControlMode(comboBox26, Wells[5].RunModeText);
                    textBox25.Text = Wells[5].Frequency.ToString("0.0");
                    ShowPlcControlMode(comboBox27, Wells[5].ControlModeText);
                    textBox30.Text = Wells[5].WaterLevel.ToString("0.00");
                    textBox29.Text = Wells[5].Flow.ToString("0.00");
                    textBox28.Text = Wells[5].TotalFlow.ToString("0.0");
                    textBox53.Text = $"{Wells[5].Flow:0.00} m3/h";
                    textBox54.Text = $"{Wells[5].WaterLevel:0.00} m";

                    // ==== Hiển thị GIẾNG 7 ====
                    ShowPlcControlMode(comboBox32, Wells[6].RunModeText);
                    textBox31.Text = Wells[6].Frequency.ToString("0.0");
                    ShowPlcControlMode(comboBox33, Wells[6].ControlModeText);
                    textBox36.Text = Wells[6].WaterLevel.ToString("0.00");
                    textBox35.Text = Wells[6].Flow.ToString("0.00");
                    textBox34.Text = Wells[6].TotalFlow.ToString("0.0");
                    textBox52.Text = $"{Wells[6].Flow:0.00} m3/h";
                    textBox51.Text = $"{Wells[6].WaterLevel:0.00} m";

                    for (int i = 0; i < Wells.Length; i++)
                    {
                        UpdateWaterLevel(
                            waterPanels[i],
                            Wells[i].WaterLevel,
                            MaxWaterLevels[i]
                        );
                    }
                    //GIENG 08
                    multipeState(standardControl63, (byte)Wells[0].RunMode);
                    multipeState(standardControl59, (byte)Wells[0].RunMode);
                    multipeState(standardControl54, (byte)Wells[0].RunMode);
                    multipeState(standardControl58, (byte)Wells[0].RunMode);
                    multipeState(standardControl60, (byte)Wells[0].RunMode);
                    multipeState(standardControl62, (byte)Wells[0].RunMode);
                    //Gieng 02
                    multipeState(standardControl57, (byte)Wells[1].RunMode);
                    multipeState(standardControl15, (byte)Wells[1].RunMode);
                    multipeState(standardControl6, (byte)Wells[1].RunMode);
                    multipeState(standardControl12, (byte)Wells[1].RunMode);
                    multipeState(standardControl64, (byte)Wells[1].RunMode);
                    multipeState(standardControl65, (byte)Wells[1].RunMode);
                    multipeState(standardControl66, (byte)Wells[1].RunMode);
                    //GIENG 03
                    multipeState(standardControl10, (byte)Wells[2].RunMode);
                    multipeState(standardControl18, (byte)Wells[2].RunMode);
                    multipeState(standardControl13, (byte)Wells[2].RunMode);
                    multipeState(standardControl17, (byte)Wells[2].RunMode);
                    multipeState(standardControl9, (byte)Wells[2].RunMode);
                    multipeState(standardControl8, (byte)Wells[2].RunMode);
                    multipeState(standardControl4, (byte)Wells[2].RunMode);
                    //GIENG 04
                    multipeState(standardControl22, (byte)Wells[3].RunMode);
                    multipeState(standardControl21, (byte)Wells[3].RunMode);
                    multipeState(standardControl19, (byte)Wells[3].RunMode);
                    multipeState(standardControl20, (byte)Wells[3].RunMode);
                    multipeState(standardControl27, (byte)Wells[3].RunMode);
                    multipeState(standardControl24, (byte)Wells[3].RunMode);
                    //GIENG 05
                    multipeState(standardControl32, (byte)Wells[4].RunMode);
                    multipeState(standardControl30, (byte)Wells[4].RunMode);
                    multipeState(standardControl28, (byte)Wells[4].RunMode);
                    multipeState(standardControl29, (byte)Wells[4].RunMode);
                    multipeState(standardControl31, (byte)Wells[4].RunMode);
                    //GIENG 06
                    multipeState(standardControl42, (byte)Wells[5].RunMode);
                    multipeState(standardControl41, (byte)Wells[5].RunMode);
                    multipeState(standardControl39, (byte)Wells[5].RunMode);
                    multipeState(standardControl40, (byte)Wells[5].RunMode);
                    multipeState(standardControl84, (byte)Wells[5].RunMode);
                    multipeState(standardControl83, (byte)Wells[5].RunMode);
                    multipeState(standardControl38, (byte)Wells[5].RunMode);
                    //GIENG 07
                    multipeState(standardControl52, (byte)Wells[6].RunMode);
                    multipeState(standardControl48, (byte)Wells[6].RunMode);
                    multipeState(standardControl44, (byte)Wells[6].RunMode);
                    multipeState(standardControl47, (byte)Wells[6].RunMode);
                    multipeState(standardControl50, (byte)Wells[6].RunMode);
                    multipeState(standardControl49, (byte)Wells[6].RunMode);
                    multipeState(standardControl51, (byte)Wells[6].RunMode);

                    UpdateAllSymbols();
                    #endregion
                }
                }
                catch (Exception ex)
                {
                    ShowErrorOnce(ex.Message);
                }
                finally
                {
                _isReading = false;
                }

        }

        #region BUTTON_MAU_GIENG_2
        private void button1_Click(object sender, EventArgs e) //ControlModeButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                // BẬT
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;

                comboBox6.Enabled = true;
                ShowEditableControlMode(comboBox6, Wells[1].ControlModeText);
                button31.Visible = true;
                isEditting = true;
            }
            else
            {
                // TẮT
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                comboBox6.Enabled = false;
                ShowPlcControlMode(comboBox6, Wells[1].ControlModeText);
                button31.Visible = false;
                isEditting = false;

            }

        }

        private void button31_Click(object sender, EventArgs e) //Xac nhan ControlModeButton Auto Man Remote
        {
            DialogResult result = MessageBox.Show(
                "Xác nhận chỉnh sửa?",
                "Xác nhận",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                // 🔹 YES → THỰC HIỆN ĐOẠN MÃ NÀY
                if (Wells[1].ControlMode == 1 && comboBox6.SelectedItem?.ToString() == "REMOTE")
                {
                    plc.WriteInt("DB28.DBW10", 1); // Ghi xuống PLC REMOTE 
                }
                else
                {
                    plc.WriteInt("DB28.DBW10", 2);
                }
                button1.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox6.Enabled = false;
                isEditting = false;
                button31.Visible = false;

            }
            else
            {
                // 🔹 NO → THỰC HIỆN ĐOẠN MÃ KHÁC (hoặc để trống)
                button1.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox6.Enabled = false;
                isEditting = false;
                button31.Visible = false;

            }
        }

        private void button2_Click(object sender, EventArgs e) //RunModeButton ON OFF
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                // BẬT
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;
                ShowEditableRunMode(comboBox5, Wells[1].RunModeText);
                comboBox5.Enabled = true;
                button29.Visible = true;
                isEditting = true;
            }
            else
            {
                // TẮT
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                comboBox5.Enabled = false;
                button29.Visible = false;
                isEditting = false;

            }

        }

        private void button29_Click(object sender, EventArgs e) //XacNhanRunModeButton 
        {
            DialogResult result = MessageBox.Show(
               "Xác nhận chỉnh sửa?",
               "Xác nhận",
               MessageBoxButtons.YesNo,
               MessageBoxIcon.Question
           );

            if (result == DialogResult.Yes)
            {
                // 🔹 YES → THỰC HIỆN ĐOẠN MÃ NÀY
                button2.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox5.Enabled = false;
                isEditting = false;
                button29.Visible = false;

                // xử lý bit plc

                if (comboBox5.SelectedItem != null && comboBox5.SelectedItem.ToString() == "START")
                {
                    // Thực hiện hành động
                    plc.WriteInt("DB28.DBW12", 1);
                }
                else
                {
                    // Thực hiện hành động
                    plc.WriteInt("DB28.DBW12", 2);
                }

            }
            else
            {
                // 🔹 NO → THỰC HIỆN ĐOẠN MÃ KHÁC (hoặc để trống)
                button2.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox5.Enabled = false;
                isEditting = false;
                button29.Visible = false;

            }
        }

        private void button3_Click(object sender, EventArgs e) // FreqButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                // BẬT
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;

                textBox4.Enabled = true;
                button28.Visible = true;
                isEditting = true;
            }
            else
            {
                // TẮT
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                textBox4.Enabled = false;
                button28.Visible = false;
                isEditting = false;

            }
        }

        private void button28_Click(object sender, EventArgs e) //Xac nhan FreqButton
        {
            DialogResult result = MessageBox.Show(
                "Xác nhận chỉnh sửa?",
                "Xác nhận",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                // 🔹 YES → THỰC HIỆN ĐOẠN MÃ NÀY
                button3.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox4.Enabled = false;
                isEditting = false;
                button28.Visible = false;
                // xử lý bit plc
                // 👉 ON bit off read hmi
                plc.WriteInt("DB28.DBW18", 1); // tín hiệu gửi
                plc.WriteFloat("DB28.DBD14", currentFreq);

            }
            else
            {
                // 🔹 NO → THỰC HIỆN ĐOẠN MÃ KHÁC (hoặc để trống)
                button3.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox4.Enabled = false;
                isEditting = false;
                button28.Visible = false;

            }
        }
        private float currentFreq = 30.0f;
        private void textBox4_Leave(object sender, EventArgs e) //Leave Freq Textbox
        {
            if (!isEditting) return;

            if (!float.TryParse(textBox4.Text, out float freq))
            {
                MessageBox.Show("Tần số phải là số", "Lỗi");
                textBox4.Text = "40.0";
                return;
            }

            if (freq < 30 || freq > 50)
            {
                MessageBox.Show("Tần số chỉ trong giá trị từ 30 đến 50 Hz", "Giới hạn");
                textBox4.Text = Math.Max(30, Math.Min(50, freq)).ToString("0.0");
                currentFreq = 50;
            }
            else
            { currentFreq = freq; }
            
        }
        #endregion
        #region BUTTON_GIENG_3
        private void button6_Click(object sender, EventArgs e) //ControlModeButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                // BẬT
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;

                comboBox9.Enabled = true;
                ShowEditableControlMode(comboBox9, Wells[2].ControlModeText);
                button34.Visible = true;
                isEditting = true;
            }
            else
            {
                // TẮT
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                comboBox9.Enabled = false;
                ShowPlcControlMode(comboBox9, Wells[2].ControlModeText);
                button34.Visible = false;
                isEditting = false;

            }

        }

        private void button34_Click(object sender, EventArgs e) //Xac nhan ControlModeButton
        {
            DialogResult result = MessageBox.Show(
                "Xác nhận chỉnh sửa?",
                "Xác nhận",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                // 🔹 YES → THỰC HIỆN ĐOẠN MÃ NÀY
                if (Wells[2].ControlMode == 1 && comboBox9.SelectedItem?.ToString() == "REMOTE")
                {
                    plc.WriteInt("DB28.DBW20", 1); // Ghi xuống PLC
                }
                else
                {
                    plc.WriteInt("DB28.DBW20", 2);
                }
                button6.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox9.Enabled = false;
                isEditting = false;
                button34.Visible = false;
            }
            else
            {
                // 🔹 NO → THỰC HIỆN ĐOẠN MÃ KHÁC (hoặc để trống)
                button6.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox9.Enabled = false;
                isEditting = false;
                button34.Visible = false;

            }
        }

        private void button5_Click(object sender, EventArgs e) //RunModeButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                // BẬT
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;
                ShowEditableRunMode(comboBox8, Wells[2].RunModeText);

                comboBox8.Enabled = true;
                button33.Visible = true;
                isEditting = true;
            }
            else
            {
                // TẮT
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                comboBox8.Enabled = false;
                button33.Visible = false;
                isEditting = false;

            }

        }

        private void button33_Click(object sender, EventArgs e) //XacNhanRunModeButton
        {
            DialogResult result = MessageBox.Show(
               "Xác nhận chỉnh sửa?",
               "Xác nhận",
               MessageBoxButtons.YesNo,
               MessageBoxIcon.Question
           );

            if (result == DialogResult.Yes)
            {
                // 🔹 YES → THỰC HIỆN ĐOẠN MÃ NÀY
                button5.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox8.Enabled = false;
                isEditting = false;
                button33.Visible = false;
                // xử lý bit plc

                if (comboBox8.SelectedItem != null && comboBox8.SelectedItem.ToString() == "START")
                {
                    // Thực hiện hành động
                    plc.WriteInt("DB28.DBW22", 1);
                }
                else
                {
                    // Thực hiện hành động
                    plc.WriteInt("DB28.DBW22", 2);
                }

            }
            else
            {
                // 🔹 NO → THỰC HIỆN ĐOẠN MÃ KHÁC (hoặc để trống)
                button5.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox8.Enabled = false;
                isEditting = false;
                button33.Visible = false;

            }
        }

        private void button4_Click(object sender, EventArgs e) // FreqButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                // BẬT
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;

                textBox7.Enabled = true;
                button32.Visible = true;
                isEditting = true;
            }
            else
            {
                // TẮT
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                textBox7.Enabled = false;
                button32.Visible = false;
                isEditting = false;

            }
        }

        private void button32_Click(object sender, EventArgs e) //Xac nhan FreqButton
        {
            DialogResult result = MessageBox.Show(
                "Xác nhận chỉnh sửa?",
                "Xác nhận",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                // 🔹 YES → THỰC HIỆN ĐOẠN MÃ NÀY
                button4.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox7.Enabled = false;
                isEditting = false;
                button32.Visible = false;
                // xử lý bit plc
                plc.WriteInt("DB28.DBW28", 1);
                plc.WriteFloat("DB28.DBD24", currentFreq3);

            }
            else
            {
                // 🔹 NO → THỰC HIỆN ĐOẠN MÃ KHÁC (hoặc để trống)
                button4.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox7.Enabled = false;
                isEditting = false;
                button32.Visible = false;

            }
        }
        private float currentFreq3 = 30.0f;

        private void textBox7_Leave(object sender, EventArgs e) //Leave Freq Textbox
        {
            if (!isEditting) return;

            if (!float.TryParse(textBox7.Text, out float freq))
            {
                MessageBox.Show("Tần số phải là số", "Lỗi");
                textBox7.Text = "40.0";
                return;
            }

            if (freq < 30 || freq > 50)
            {
                MessageBox.Show("Tần số chỉ trong giá trị từ 30 đến 50 Hz", "Giới hạn");
                textBox7.Text = Math.Max(30, Math.Min(50, freq)).ToString("0.0");
                currentFreq3 = 50;
            }
            else
            { currentFreq3 = freq; }

        }
        #endregion
        #region BUTTON_GIENG_4  
        private void button9_Click(object sender, EventArgs e) //ControlModeButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                // BẬT
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;

                comboBox15.Enabled = true;
                ShowEditableControlMode(comboBox15, Wells[3].ControlModeText);
                button37.Visible = true;
                isEditting = true;
            }
            else
            {
                // TẮT
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                comboBox15.Enabled = false;
                ShowPlcControlMode(comboBox15, Wells[3].ControlModeText);
                button37.Visible = false;
                isEditting = false;
            }
        }

        private void button37_Click(object sender, EventArgs e) //Xac nhan ControlModeButton
        {
            DialogResult result = MessageBox.Show(
                "Xác nhận chỉnh sửa?",
                "Xác nhận",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                if (Wells[3].ControlMode == 1 && comboBox15.SelectedItem?.ToString() == "REMOTE")
                {
                    plc.WriteInt("DB28.DBW30", 1); // Ghi xuống PLC
                }
                else
                {
                    plc.WriteInt("DB28.DBW30", 2);
                }
                button9.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox15.Enabled = false;
                isEditting = false;
                button37.Visible = false;
            }
            else
            {
                button9.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox15.Enabled = false;
                isEditting = false;
                button37.Visible = false;
            }
        }

        private void button8_Click(object sender, EventArgs e) //RunModeButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                // BẬT
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;
                ShowEditableRunMode(comboBox14, Wells[3].RunModeText);

                comboBox14.Enabled = true;
                button36.Visible = true;
                isEditting = true;
            }
            else
            {
                // TẮT
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                comboBox14.Enabled = false;
                button36.Visible = false;
                isEditting = false;
            }
        }

        private void button36_Click(object sender, EventArgs e) //XacNhanRunModeButton
        {
            DialogResult result = MessageBox.Show(
               "Xác nhận chỉnh sửa?",
               "Xác nhận",
               MessageBoxButtons.YesNo,
               MessageBoxIcon.Question
           );

            if (result == DialogResult.Yes)
            {
                button8.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox14.Enabled = false;
                isEditting = false;
                button36.Visible = false;

                // xử lý bit plc
                if (comboBox14.SelectedItem != null && comboBox14.SelectedItem.ToString() == "START")
                {
                    plc.WriteInt("DB28.DBW32", 1);
                }
                else
                {
                    plc.WriteInt("DB28.DBW32", 2);
                }
            }
            else
            {
                button8.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox14.Enabled = false;
                isEditting = false;
                button36.Visible = false;
            }
        }

        private void button7_Click(object sender, EventArgs e) // FreqButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                // BẬT
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;

                textBox13.Enabled = true;
                button35.Visible = true;
                isEditting = true;
            }
            else
            {
                // TẮT
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                textBox13.Enabled = false;
                button35.Visible = false;
                isEditting = false;
            }
        }

        private void button35_Click(object sender, EventArgs e) //Xac nhan FreqButton
        {
            DialogResult result = MessageBox.Show(
                "Xác nhận chỉnh sửa?",
                "Xác nhận",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                button7.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox13.Enabled = false;
                isEditting = false;
                button35.Visible = false;

                // xử lý bit plc
                plc.WriteInt("DB28.DBW38", 1);
                plc.WriteFloat("DB28.DBD34", currentFreq4);
            }
            else
            {
                button7.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox13.Enabled = false;
                isEditting = false;
                button35.Visible = false;
            }
        }

        private float currentFreq4 = 30.0f;
        private void textBox13_Leave(object sender, EventArgs e) //Leave Freq Textbox
        {
            if (!isEditting) return;

            if (!float.TryParse(textBox13.Text, out float freq))
            {
                MessageBox.Show("Tần số phải là số", "Lỗi");
                textBox13.Text = "40.0";
                return;
            }

            if (freq < 30 || freq > 50)
            {
                MessageBox.Show("Tần số chỉ trong giá trị từ 30 đến 50 Hz", "Giới hạn");
                textBox13.Text = Math.Max(30, Math.Min(50, freq)).ToString("0.0");
                currentFreq4 = 50;
            }
            else
            { currentFreq4 = freq; }
        }
        #endregion
        #region BUTTON_GIENG_5 
        private void button12_Click_1(object sender, EventArgs e) //ControlModeButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                // BẬT
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;

                comboBox21.Enabled = true;
                ShowEditableControlMode(comboBox21, Wells[4].ControlModeText);
                button40.Visible = true;
                isEditting = true;
            }
            else
            {
                // TẮT
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                comboBox21.Enabled = false;
                ShowPlcControlMode(comboBox21, Wells[4].ControlModeText);
                button40.Visible = false;
                isEditting = false;
            }
        }

        private void button40_Click_1(object sender, EventArgs e) //Xac nhan ControlModeButton
        {
            DialogResult result = MessageBox.Show(
                "Xác nhận chỉnh sửa?",
                "Xác nhận",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );
            if (Wells[4].ControlMode == 1 && comboBox21.SelectedItem?.ToString() == "REMOTE")
            {
                plc.WriteInt("DB28.DBW40", 1); // Ghi xuống PLC
            }
            else
            {
                plc.WriteInt("DB28.DBW40", 2);
            }
            button12.BackgroundImage = Properties.Resources._02_Tab_2Setting;
            comboBox21.Enabled = false;
            isEditting = false;
            button40.Visible = false;
        }

        private void button11_Click_1(object sender, EventArgs e) //RunModeButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;
                ShowEditableRunMode(comboBox20, Wells[4].RunModeText);

                comboBox20.Enabled = true;
                button39.Visible = true;
                isEditting = true;
            }
            else
            {
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                comboBox20.Enabled = false;
                button39.Visible = false;
                isEditting = false;
            }
        }

        private void button39_Click_1(object sender, EventArgs e) //XacNhanRunModeButton
        {
            DialogResult result = MessageBox.Show(
               "Xác nhận chỉnh sửa?",
               "Xác nhận",
               MessageBoxButtons.YesNo,
               MessageBoxIcon.Question
           );

            if (result == DialogResult.Yes)
            {
                button11.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox20.Enabled = false;
                isEditting = false;
                button39.Visible = false;

                if (comboBox20.SelectedItem != null && comboBox20.SelectedItem.ToString() == "START")
                    plc.WriteInt("DB28.DBW42", 1);
                else
                    plc.WriteInt("DB28.DBW42", 2);
            }
            else
            {
                button11.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox20.Enabled = false;
                isEditting = false;
                button39.Visible = false;
            }
        }

        private void button10_Click_1(object sender, EventArgs e) // FreqButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;

                textBox19.Enabled = true;
                button38.Visible = true;
                isEditting = true;
            }
            else
            {
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                textBox19.Enabled = false;
                button38.Visible = false;
                isEditting = false;
            }
        }

        private void button38_Click_1(object sender, EventArgs e) //Xac nhan FreqButton
        {
            DialogResult result = MessageBox.Show(
                "Xác nhận chỉnh sửa?",
                "Xác nhận",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                button10.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox19.Enabled = false;
                isEditting = false;
                button38.Visible = false;

                plc.WriteInt("DB28.DBW48", 1);
                plc.WriteFloat("DB28.DBD44", currentFreq5);

            }
            else
            {
                button10.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox19.Enabled = false;
                isEditting = false;
                button38.Visible = false;
            }
        }

        private float currentFreq5 = 30.0f;
        private void textBox19_Leave_1(object sender, EventArgs e) //Leave Freq Textbox
        {
            if (!isEditting) return;

            if (!float.TryParse(textBox19.Text, out float freq))
            {
                MessageBox.Show("Tần số phải là số", "Lỗi");
                textBox19.Text = "40.0";
                return;
            }

            if (freq < 30 || freq > 50)
            {
                MessageBox.Show("Tần số chỉ trong giá trị từ 30 đến 50 Hz", "Giới hạn");
                textBox19.Text = Math.Max(30, Math.Min(50, freq)).ToString("0.0");
                currentFreq5 = 50;
            }
            else
            { currentFreq5 = freq; }
        }
        #endregion
        #region BUTTON_GIENG_6
        private void button15_Click_1(object sender, EventArgs e) //ControlModeButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                // BẬT
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;

                comboBox27.Enabled = true;
                ShowEditableControlMode(comboBox27, Wells[5].ControlModeText);
                button43.Visible = true;
                isEditting = true;
            }
            else
            {
                // TẮT
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                comboBox27.Enabled = false;
                ShowPlcControlMode(comboBox27, Wells[5].ControlModeText);
                button43.Visible = false;
                isEditting = false;
            }
        }

        private void button43_Click_1(object sender, EventArgs e) //Xac nhan ControlModeButton
        {
            DialogResult result = MessageBox.Show(
                "Xác nhận chỉnh sửa?",
                "Xác nhận",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );
            if (Wells[5].ControlMode == 1 && comboBox27.SelectedItem?.ToString() == "REMOTE")
            {
                plc.WriteInt("DB28.DBW50", 1); // Ghi xuống PLC
            }
            else
            {
                plc.WriteInt("DB28.DBW50", 2);
            }
            button15.BackgroundImage = Properties.Resources._02_Tab_2Setting;
            comboBox27.Enabled = false;
            isEditting = false;
            button43.Visible = false;
        }

        private void button14_Click_1(object sender, EventArgs e) //RunModeButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;
                ShowEditableRunMode(comboBox26, Wells[5].RunModeText);

                comboBox26.Enabled = true;
                button42.Visible = true;
                isEditting = true;
            }
            else
            {
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                comboBox26.Enabled = false;
                button42.Visible = false;
                isEditting = false;
            }
        }

        private void button42_Click_1(object sender, EventArgs e) //XacNhanRunModeButton
        {
            DialogResult result = MessageBox.Show(
               "Xác nhận chỉnh sửa?",
               "Xác nhận",
               MessageBoxButtons.YesNo,
               MessageBoxIcon.Question
           );

            if (result == DialogResult.Yes)
            {
                button14.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox26.Enabled = false;
                isEditting = false;
                button42.Visible = false;

                if (comboBox26.SelectedItem != null && comboBox26.SelectedItem.ToString() == "START")
                    plc.WriteInt("DB28.DBW52", 1);
                else
                    plc.WriteInt("DB28.DBW52", 2);
            }
            else
            {
                button14.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox26.Enabled = false;
                isEditting = false;
                button42.Visible = false;
            }
        }

        private void button13_Click_1(object sender, EventArgs e) // FreqButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;

                textBox25.Enabled = true;
                button41.Visible = true;
                isEditting = true;
            }
            else
            {
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                textBox25.Enabled = false;
                button41.Visible = false;
                isEditting = false;
            }
        }

        private void button41_Click_1(object sender, EventArgs e) //Xac nhan FreqButton
        {
            DialogResult result = MessageBox.Show(
                "Xác nhận chỉnh sửa?",
                "Xác nhận",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                button13.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox25.Enabled = false;
                isEditting = false;
                button41.Visible = false;

                plc.WriteInt("DB28.DBW58", 1);
                plc.WriteFloat("DB28.DBD54", currentFreq6);
            }
            else
            {
                button13.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox25.Enabled = false;
                isEditting = false;
                button41.Visible = false;
            }
        }

        private float currentFreq6 = 30.0f;
        private void textBox25_Leave_1(object sender, EventArgs e) //Leave Freq Textbox
        {
            if (!isEditting) return;

            if (!float.TryParse(textBox25.Text, out float freq))
            {
                MessageBox.Show("Tần số phải là số", "Lỗi");
                textBox25.Text = "40.0";
                return;
            }

            if (freq < 30 || freq > 50)
            {
                MessageBox.Show("Tần số chỉ trong giá trị từ 30 đến 50 Hz", "Giới hạn");
                textBox25.Text = Math.Max(30, Math.Min(50, freq)).ToString("0.0");
                currentFreq6 = 50;
            }
            else
            { currentFreq6 = freq; }
        }
        #endregion
        #region BUTTON_GIENG_7 
        private void button18_Click_1(object sender, EventArgs e) //ControlModeButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                // BẬT
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;

                comboBox33.Enabled = true;
                ShowEditableControlMode(comboBox33, Wells[6].ControlModeText);
                button46.Visible = true;
                isEditting = true;
            }
            else
            {
                // TẮT
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                comboBox33.Enabled = false;
                ShowPlcControlMode(comboBox33, Wells[6].ControlModeText);
                button46.Visible = false;
                isEditting = false;
            }
        }

        private void button46_Click_1(object sender, EventArgs e) //Xac nhan ControlModeButton
        {
            DialogResult result = MessageBox.Show(
                "Xác nhận chỉnh sửa?",
                "Xác nhận",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );
            if (Wells[6].ControlMode == 1 && comboBox33.SelectedItem?.ToString() == "REMOTE")
            {
                plc.WriteInt("DB28.DBW60", 1); // Ghi xuống PLC
            }
            else
            {
                plc.WriteInt("DB28.DBW60", 2);
            }
            button18.BackgroundImage = Properties.Resources._02_Tab_2Setting;
            comboBox33.Enabled = false;
            isEditting = false;
            button46.Visible = false;
        }

        private void button17_Click_1(object sender, EventArgs e) //RunModeButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;
                ShowEditableRunMode(comboBox32, Wells[6].RunModeText);

                comboBox32.Enabled = true;
                button45.Visible = true;
                isEditting = true;
            }
            else
            {
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                comboBox32.Enabled = false;
                button45.Visible = false;
                isEditting = false;
            }
        }

        private void button45_Click_1(object sender, EventArgs e) //XacNhanRunModeButton
        {
            DialogResult result = MessageBox.Show(
               "Xác nhận chỉnh sửa?",
               "Xác nhận",
               MessageBoxButtons.YesNo,
               MessageBoxIcon.Question
           );

            if (result == DialogResult.Yes)
            {
                button17.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox32.Enabled = false;
                isEditting = false;
                button45.Visible = false;

                if (comboBox32.SelectedItem != null && comboBox32.SelectedItem.ToString() == "START")
                    plc.WriteInt("DB28.DBW62", 1);
                else
                    plc.WriteInt("DB28.DBW62", 2);

            }
            else
            {
                button17.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox32.Enabled = false;
                isEditting = false;
                button45.Visible = false;
            }
        }

        private void button16_Click_1(object sender, EventArgs e) // FreqButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;

                textBox31.Enabled = true;
                button44.Visible = true;
                isEditting = true;
            }
            else
            {
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                textBox31.Enabled = false;
                button44.Visible = false;
                isEditting = false;
            }
        }

        private void button44_Click_1(object sender, EventArgs e) //Xac nhan FreqButton
        {
            DialogResult result = MessageBox.Show(
                "Xác nhận chỉnh sửa?",
                "Xác nhận",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                button16.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox31.Enabled = false;
                isEditting = false;
                button44.Visible = false;

                plc.WriteInt("DB28.DBW68", 1);
                plc.WriteFloat("DB28.DBD64", currentFreq7);

            }
            else
            {
                button16.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox31.Enabled = false;
                isEditting = false;
                button44.Visible = false;
            }
        }

        private float currentFreq7 = 30.0f;
        private void textBox31_Leave_1(object sender, EventArgs e) //Leave Freq Textbox
        {
            if (!isEditting) return;

            if (!float.TryParse(textBox31.Text, out float freq))
            {
                MessageBox.Show("Tần số phải là số", "Lỗi");
                textBox31.Text = "40.0";
                return;
            }

            if (freq < 30 || freq > 50)
            {
                MessageBox.Show("Tần số chỉ trong giá trị từ 30 đến 50 Hz", "Giới hạn");
                textBox31.Text = Math.Max(30, Math.Min(50, freq)).ToString("0.0");
                currentFreq7 = 50;
            }
            else
            { currentFreq7 = freq; }
        }
        #endregion
        #region BUTTON_GIENG_8
        private void button21_Click_1(object sender, EventArgs e) //ControlModeButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                // BẬT
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;

                comboBox39.Enabled = true;
                ShowEditableControlMode(comboBox39, Wells[0].ControlModeText);
                button49.Visible = true;
                isEditting = true;
            }
            else
            {
                // TẮT
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                comboBox39.Enabled = false;
                ShowPlcControlMode(comboBox39, Wells[0].ControlModeText);
                button49.Visible = false;
                isEditting = false;
            }
        }

        private void button49_Click_1(object sender, EventArgs e) //Xac nhan ControlModeButton
        {
            DialogResult result = MessageBox.Show(
                "Xác nhận chỉnh sửa?",
                "Xác nhận",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );
            if (Wells[0].ControlMode == 1 && comboBox39.SelectedItem?.ToString() == "REMOTE")
            {
                plc.WriteInt("DB28.DBW0", 1); // Ghi xuống PLC
            }
            else
            {
                plc.WriteInt("DB28.DBW0", 2);
            }
            button21.BackgroundImage = Properties.Resources._02_Tab_2Setting;
            comboBox39.Enabled = false;
            isEditting = false;
            button49.Visible = false;
        }

        private void button20_Click_1(object sender, EventArgs e) //RunModeButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;
                ShowEditableRunMode(comboBox38, Wells[0].RunModeText);

                comboBox38.Enabled = true;
                button48.Visible = true;
                isEditting = true;
            }
            else
            {
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                comboBox38.Enabled = false;
                button48.Visible = false;
                isEditting = false;
            }
        }

        private void button48_Click_1(object sender, EventArgs e) //XacNhanRunModeButton
        {
            DialogResult result = MessageBox.Show(
               "Xác nhận chỉnh sửa?",
               "Xác nhận",
               MessageBoxButtons.YesNo,
               MessageBoxIcon.Question
           );

            if (result == DialogResult.Yes)
            {
                button20.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox38.Enabled = false;
                isEditting = false;
                button48.Visible = false;

                if (comboBox38.SelectedItem != null && comboBox38.SelectedItem.ToString() == "START")
                    plc.WriteInt("DB28.DBW2", 1);
                else
                    plc.WriteInt("DB28.DBW2", 2);

            }
            else
            {
                button20.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox38.Enabled = false;
                isEditting = false;
                button48.Visible = false;
            }
        }

        private void button19_Click_1(object sender, EventArgs e) // FreqButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;

                textBox37.Enabled = true;
                button47.Visible = true;
                isEditting = true;
            }
            else
            {
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                textBox37.Enabled = false;
                button47.Visible = false;
                isEditting = false;
            }
        }

        private void button47_Click_1(object sender, EventArgs e) //Xac nhan FreqButton
        {
            DialogResult result = MessageBox.Show(
                "Xác nhận chỉnh sửa?",
                "Xác nhận",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                button19.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox37.Enabled = false;
                isEditting = false;
                button47.Visible = false;

                plc.WriteInt("DB28.DBW8", 1);
                plc.WriteFloat("DB28.DBD4", currentFreq8);

            }
            else
            {
                button19.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox37.Enabled = false;
                isEditting = false;
                button47.Visible = false;
            }
        }

        private float currentFreq8 = 30.0f;
        private void textBox37_Leave_1(object sender, EventArgs e) //Leave Freq Textbox
        {
            if (!isEditting) return;

            if (!float.TryParse(textBox37.Text, out float freq))
            {
                MessageBox.Show("Tần số phải là số", "Lỗi");
                textBox37.Text = "40.0";
                return;
            }

            if (freq < 30 || freq > 50)
            {
                MessageBox.Show("Tần số chỉ trong giá trị từ 30 đến 50 Hz", "Giới hạn");
                textBox37.Text = Math.Max(30, Math.Min(50, freq)).ToString("0.0");
                currentFreq8 = 50;
            }
            else
            { currentFreq8 = freq; }
        }


        #endregion
        #region BUTTON_TANK_TG_1


        private void button23_Click(object sender, EventArgs e) //RunModeButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;
                comboBox1.Enabled = true;
                button52.Visible = true;
                isEditting = true;
            }
            else
            {
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                ShowEditableRunMode(comboBox1, RunModeToText(currentstatusOnT1));
                comboBox1.Enabled = false;
                button52.Visible = false;
                isEditting = false;
            }
        }

        private void button52_Click(object sender, EventArgs e) //XacNhanRunModeButton
        {
            DialogResult result = MessageBox.Show(
               "Xác nhận chỉnh sửa?",
               "Xác nhận",
               MessageBoxButtons.YesNo,
               MessageBoxIcon.Question
           );

            if (result == DialogResult.Yes)
            {
                button23.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox1.Enabled = false;
                isEditting = false;
                button52.Visible = false;

                if (comboBox1.SelectedItem != null && comboBox1.SelectedItem.ToString() == "START")
                {
                    plc.WriteInt("DB28.DBW72", 1);
                }
                else
                {
                    plc.WriteInt("DB28.DBW72", 2);
                }

            }
            else
            {
                button23.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox1.Enabled = false;
                isEditting = false;
                button52.Visible = false;
            }
        }

        private void button22_Click(object sender, EventArgs e) //FreqButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;

                textBox43.Enabled = true;
                button51.Visible = true;
                isEditting = true;
            }
            else
            {
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                textBox43.Enabled = false;
                button51.Visible = false;
                isEditting = false;
            }
        }

        private void button51_Click(object sender, EventArgs e) //Xac nhan FreqButton
        {
            DialogResult result = MessageBox.Show(
                "Xác nhận chỉnh sửa?",
                "Xác nhận",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                button22.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox43.Enabled = false;
                isEditting = false;
                button51.Visible = false;
                plc.WriteInt("DB28.DBW78", 1);
                plc.WriteFloat("DB28.DBD74", currentFreq11);
            }
            else
            {
                button22.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox43.Enabled = false;
                isEditting = false;
                button51.Visible = false;
            }
        }

        private float currentFreq11 = 30.0f;

        private void textBox43_Leave(object sender, EventArgs e) //Leave Freq Textbox
        {
            if (!isEditting) return;

            if (!float.TryParse(textBox43.Text, out float freq))
            {
                MessageBox.Show("Tần số phải là số", "Lỗi");
                textBox43.Text = "40.0";
                return;
            }

            if (freq < 30 || freq > 50)
            {
                MessageBox.Show("Tần số chỉ trong giá trị từ 30 đến 50 Hz", "Giới hạn");
                textBox43.Text = Math.Max(30, Math.Min(50, freq)).ToString("0.0");
                currentFreq11 = 50;
            }
            else
            { currentFreq11 = freq; }
        }

        #endregion
        #region BUTTON_TANK_TG_2


        private void button26_Click(object sender, EventArgs e) //RunModeButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;

                ShowEditableRunMode(comboBox3, RunModeToText(currentstatusOnT2));
                comboBox3.Enabled = true;
                button55.Visible = true;
                isEditting = true;
            }
            else
            {
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                comboBox3.Enabled = false;
                button55.Visible = false;
                isEditting = false;
            }
        }

        private void button55_Click(object sender, EventArgs e) //XacNhanRunModeButton
        {
            DialogResult result = MessageBox.Show(
               "Xác nhận chỉnh sửa?",
               "Xác nhận",
               MessageBoxButtons.YesNo,
               MessageBoxIcon.Question
           );

            if (result == DialogResult.Yes)
            {
                button26.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox3.Enabled = false;
                isEditting = false;
                button55.Visible = false;


                if (comboBox3.SelectedItem != null && comboBox3.SelectedItem.ToString() == "START")
                {
                    plc.WriteInt("DB28.DBW80", 1);
                }
                else
                {
                    plc.WriteInt("DB28.DBW80", 2);
                }

            }
            else
            {
                button26.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                comboBox3.Enabled = false;
                isEditting = false;
                button55.Visible = false;
            }
        }

        private void button25_Click(object sender, EventArgs e) //FreqButton
        {
            Button btn = sender as Button;
            if (btn == null) return;

            bool isOn = btn.Tag != null && btn.Tag.ToString() == "ON";

            if (!isOn)
            {
                btn.Tag = "ON";
                btn.BackgroundImage = Properties.Resources._04_Cross;

                textBox44.Enabled = true;
                button54.Visible = true;
                isEditting = true;
            }
            else
            {
                btn.Tag = "OFF";
                btn.BackgroundImage = Properties.Resources._02_Tab_2Setting;

                textBox44.Enabled = false;
                button54.Visible = false;
                isEditting = false;
            }
        }

        private void button54_Click(object sender, EventArgs e) //Xac nhan FreqButton
        {
            DialogResult result = MessageBox.Show(
                "Xác nhận chỉnh sửa?",
                "Xác nhận",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );

            if (result == DialogResult.Yes)
            {
                button25.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox44.Enabled = false;
                isEditting = false;
                button54.Visible = false;

                plc.WriteInt("DB28.DBW86", 1);
                plc.WriteFloat("DB28.DBD82", currentFreq12);

            }
            else
            {
                button25.BackgroundImage = Properties.Resources._02_Tab_2Setting;
                textBox44.Enabled = false;
                isEditting = false;
                button54.Visible = false;
            }
        }

        private float currentFreq12 = 30.0f;

        private void textBox44_Leave(object sender, EventArgs e) //Leave Freq Textbox
        {
            if (!isEditting) return;

            if (!float.TryParse(textBox44.Text, out float freq))
            {
                MessageBox.Show("Tần số phải là số", "Lỗi");
                textBox44.Text = "40.0";
                return;
            }

            if (freq < 30 || freq > 50)
            {
                MessageBox.Show("Tần số chỉ trong giá trị từ 30 đến 50 Hz", "Giới hạn");
                textBox44.Text = Math.Max(30, Math.Min(50, freq)).ToString("0.0");
                currentFreq12 = 50;
            }
            else
            { currentFreq12 = freq; }
        }

        #endregion
        private void button24_Click(object sender, EventArgs e)
        {
            plc.WriteInt("DB28.DBW70", 1);
        }

        private void button27_Click(object sender, EventArgs e)
        {
            plc.WriteInt("DB28.DBW70", 2);
        }
        private void standardControl52_Load(object sender, EventArgs e)
        {

        }

        private void panel24_Paint(object sender, PaintEventArgs e)
        {

        }

        private void panel19_Paint(object sender, PaintEventArgs e)
        {

        }

        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {

        }

        private void panel31_Paint(object sender, PaintEventArgs e)
        {

        }

        private void standardControl3_Load(object sender, EventArgs e)
        {

        }

        private void standardControl100_Load(object sender, EventArgs e)
        {

        }
        private void groupBox1_Enter(object sender, EventArgs e)
        {

        }

        private void standardControl1_Load(object sender, EventArgs e)
        {

        }

        private void label9_Click(object sender, EventArgs e)
        {

        }

        private void tabPage1_Click(object sender, EventArgs e)
        {

        }

        private void label72_Click(object sender, EventArgs e)
        {

        }

        private void panel1_Paint(object sender, PaintEventArgs e)
        {

        }

        private void panel17_Paint(object sender, PaintEventArgs e)
        {

        }

        private void label77_Click(object sender, EventArgs e)
        {

        }

        private void tabPage4_Click(object sender, EventArgs e)
        {

        }

        private void standardControl123_Load(object sender, EventArgs e)
        {

        }
        private void button50_Click(object sender, EventArgs e)
        {
            isEditting = true;

            button30.Visible = true;
            button53.Visible = true;
            button50.Visible = false;

            // Ẩn toàn bộ button trong tất cả panel
            foreach (var ui in wellUIs)
            {
                foreach (var btn in ui.Buttons)
                    btn.Visible = false;
            }
            int count = Math.Min(Wells.Length, wellUIs.Count);

            for (int i = 0; i < count; i++)
                {
                var well = Wells[i];
                var ui = wellUIs[i];

                if (well.ControlMode == 1)   // chỉ check ControlMode như yêu cầu
                {
                    // Đổi màu
                    ui.Panel.BackColor = editBackColor;
                    ui.Panel.Paint -= EditPanel_Paint;
                    ui.Panel.Paint += EditPanel_Paint;
                    ui.Panel.Invalidate();

                    // Enable ComboBox
                    ui.CbControlMode.Enabled = true;
                    ui.CbRunMode.Enabled = true;

                    // Set selected theo PLC
                    ui.CbControlMode.Items.Clear();
                    ui.CbControlMode.Items.Add("REMOTE");
                    ui.CbControlMode.SelectedIndex = 0;
                    ui.CbControlMode.Enabled = false;

                    ui.CbRunMode.Items.Clear();
                    ui.CbRunMode.Items.Add("START");
                    ui.CbRunMode.Items.Add("STOP");

                    ui.CbRunMode.SelectedItem = well.RunMode == 1 ? "START" : "STOP";
                    ui.CbRunMode.Enabled = true;

                    // Enable textbox
                    ui.TbFreq.Enabled = true;

                    // Chỉ cho nhập số
                    ui.TbFreq.KeyPress -= OnlyNumber_KeyPress;
                    ui.TbFreq.KeyPress += OnlyNumber_KeyPress;
                }
            }
            if (mode == 1)
            {
                panel18.BackColor = editBackColor;
                panel18.Paint -= EditPanel_Paint;
                panel18.Paint += EditPanel_Paint;
                panel18.Invalidate();

                panel19.BackColor = editBackColor;
                panel19.Paint -= EditPanel_Paint;
                panel19.Paint += EditPanel_Paint;
                panel19.Invalidate();
                // Enable ComboBox
                textBox45.Enabled = true;
                textBox46.Enabled = true;

                comboBox1.Enabled = true;
                comboBox3.Enabled = true;

                textBox43.Enabled = true;
                textBox44.Enabled = true;
                //Hide Button
                button51.Visible = false;
                button52.Visible = false;
                button22.Visible = false;
                button23.Visible = false;

                button54.Visible = false;
                button55.Visible = false;
                button25.Visible = false;
                button26.Visible = false;
                // Set selected theo PLC
                comboBox1.Items.Clear();
                comboBox1.Items.Add("START");
                comboBox1.Items.Add("STOP");
                comboBox1.SelectedItem = currentstatusOnT1 == 1 ? "START" : "STOP";
                comboBox1.Enabled = true;

                comboBox3.Items.Clear();
                comboBox3.Items.Add("START");
                comboBox3.Items.Add("STOP");
                comboBox3.SelectedItem = currentstatusOnT2 == 1 ? "START" : "STOP";
                comboBox3.Enabled = true;

                textBox43.KeyPress -= OnlyNumber_KeyPress;
                textBox43.KeyPress += OnlyNumber_KeyPress;

                textBox44.KeyPress -= OnlyNumber_KeyPress;
                textBox44.KeyPress += OnlyNumber_KeyPress;
            }
            else if(mode == 2)
            {

                panel19.BackColor = editBackColor;
                panel19.Paint -= EditPanel_Paint;
                panel19.Paint += EditPanel_Paint;
                panel19.Invalidate();
                // Enable ComboBox
                textBox46.Enabled = true;
                comboBox3.Enabled = true;
                textBox44.Enabled = true;
                //Hide Button

                button54.Visible = false;
                button55.Visible = false;
                button25.Visible = false;
                button26.Visible = false;
                // Set selected theo PLC

                comboBox3.Items.Clear();
                comboBox3.Items.Add("START");
                comboBox3.Items.Add("STOP");
                comboBox3.SelectedItem = currentstatusOnT2 == 1 ? "START" : "STOP";
                comboBox3.Enabled = true;

                textBox44.KeyPress -= OnlyNumber_KeyPress;
                textBox44.KeyPress += OnlyNumber_KeyPress;
            }
            else if (mode == 3)
            {
                panel18.BackColor = editBackColor;
                panel18.Paint -= EditPanel_Paint;
                panel18.Paint += EditPanel_Paint;
                panel18.Invalidate();

                // Enable ComboBox
                textBox45.Enabled = true;
                comboBox1.Enabled = true;
                textBox43.Enabled = true;
                //Hide Button
                button51.Visible = false;
                button52.Visible = false;
                button22.Visible = false;
                button23.Visible = false;

                // Set selected theo PLC
                comboBox1.Items.Clear();
                comboBox1.Items.Add("START");
                comboBox1.Items.Add("STOP");
                comboBox1.SelectedItem = currentstatusOnT1 == 1 ? "START" : "STOP";
                comboBox1.Enabled = true;

                textBox43.KeyPress -= OnlyNumber_KeyPress;
                textBox43.KeyPress += OnlyNumber_KeyPress;
            }
        }


        private void button30_Click(object sender, EventArgs e)
        {
            int count = Math.Min(Wells.Length, wellUIs.Count);

            for (int i = 0; i < count; i++)
            {
                var well = Wells[i];
                var ui = wellUIs[i];
                int baseOffset = i * 10;
                if (well.ControlMode == 1)   // chỉ check ControlMode như yêu cầu
                {
                    plc.WriteInt($"DB28.DBW{baseOffset}", 1);
                    ushort runValue = 2; // default STOP
                    if (ui.CbRunMode.SelectedItem?.ToString() == "START")
                    { runValue = 1; }

                    plc.WriteInt($"DB28.DBW{baseOffset + 2}", runValue);

                    // 3️⃣ Frequency
                    if (float.TryParse(ui.TbFreq.Text, out float freq))
                    {
                        plc.WriteFloat($"DB28.DBD{baseOffset + 4}", freq);
                    }
                    plc.WriteInt($"DB28.DBW{baseOffset + 8}", 1);

                }
            }

            foreach (var ui in wellUIs)
            {
                // Trả màu gốc
                ui.Panel.BackColor = Color.Gainsboro;
                ui.Panel.Paint -= EditPanel_Paint;
                ui.Panel.Invalidate();

                // Disable control
                ui.CbControlMode.Enabled = false;
                ui.CbRunMode.Enabled = false;
                ui.TbFreq.Enabled = false;
            }

            panel18.BackColor = Color.Gainsboro;
            panel18.Paint -= EditPanel_Paint;
            panel18.Invalidate();

            panel19.BackColor = Color.Gainsboro;
            panel19.Paint -= EditPanel_Paint;
            panel19.Invalidate();

            textBox45.Enabled = false;
            textBox46.Enabled = false;

            comboBox1.Enabled = false;
            comboBox3.Enabled = false;

            textBox43.Enabled = false;
            textBox44.Enabled = false;

            if (mode == 1)
            {
                plc.WriteInt("DB28.DBW70", 1);

                ushort runValue1 = 2; // default STOP
                ushort runValue2 = 2; // default STOP
                if (comboBox1.SelectedItem?.ToString() == "START")
                { runValue1 = 1; }

                if (comboBox3.SelectedItem?.ToString() == "START")
                { runValue2 = 1; }
                
                plc.WriteInt("DB28.DBW72", runValue1);
                plc.WriteInt("DB28.DBW80", runValue2);

                // 3️⃣ Frequency
                if (float.TryParse(textBox43.Text, out float freq))
                {
                    plc.WriteFloat("DB28.DBD74", freq);
                }
                plc.WriteInt($"DB28.DBW78", 1);
                if (float.TryParse(textBox44.Text, out float freq2))
                {
                    plc.WriteFloat("DB28.DBD82", freq2);
                }
                plc.WriteInt($"DB28.DBW86", 1);
            }
            else if (mode == 2)
            {
                plc.WriteInt("DB28.DBW70", 1);

                ushort runValue2 = 2; // default STOP
                if (comboBox3.SelectedItem?.ToString() == "START")
                { runValue2 = 1; }

                plc.WriteInt("DB28.DBW80", runValue2);

                // 3️⃣ Frequency

                if (float.TryParse(textBox44.Text, out float freq2))
                {
                    plc.WriteFloat("DB28.DBD82", freq2);
                }
                plc.WriteInt($"DB28.DBW86", 1);
            }
            else if (mode == 3)
            {
                plc.WriteInt("DB28.DBW70", 1);

                ushort runValue1 = 2; // default STOP
                if (comboBox1.SelectedItem?.ToString() == "START")
                { runValue1 = 1; }

                plc.WriteInt("DB28.DBW72", runValue1);

                // 3️⃣ Frequency
                if (float.TryParse(textBox43.Text, out float freq))
                {
                    plc.WriteFloat("DB28.DBD74", freq);
                }
                plc.WriteInt("DB28.DBW78", 1);

            }

            button30.Visible = false;
            button53.Visible = false;
            button50.Visible = true;
            isEditting = false;
        }

        private void textBox45_TextChanged(object sender, EventArgs e)
        {

        }

        private void button53_Click(object sender, EventArgs e)
        {
            foreach (var ui in wellUIs)
            {
                // Trả màu gốc
                ui.Panel.BackColor = Color.Gainsboro;
                ui.Panel.Paint -= EditPanel_Paint;
                ui.Panel.Invalidate();

                // Disable control
                ui.CbControlMode.Enabled = false;
                ui.CbRunMode.Enabled = false;
                ui.TbFreq.Enabled = false;
            }

            panel18.BackColor = Color.Gainsboro;
            panel18.Paint -= EditPanel_Paint;
            panel18.Invalidate();

            panel19.BackColor = Color.Gainsboro;
            panel19.Paint -= EditPanel_Paint;
            panel19.Invalidate();

            textBox45.Enabled = false;
            textBox46.Enabled = false;

            comboBox1.Enabled = false;
            comboBox3.Enabled = false;

            textBox43.Enabled = false;
            textBox44.Enabled = false;

            button30.Visible = false;
            button53.Visible = false;
            button50.Visible = true;
            isEditting = false;
        }

        private void button57_Click(object sender, EventArgs e)
        {
            InsertHistory(1, 50.2, 120.5, 3.6);
            MessageBox.Show("Đã ghi dữ liệu");
        }

        private void button56_Click(object sender, EventArgs e)
        {
            dataGridView1.DataSource = GetHistory(1);
        }

        private void dataGridView1_CellDoubleClick_1(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;

            try
            {
                int alarmId = Convert.ToInt32(
                    dataGridView1.Rows[e.RowIndex].Cells["Id"].Value);

                string query = "UPDATE dbo.Well_Alarm SET IsHandled = 1 WHERE Id = @Id";

                ClassSQL.ExecuteNonQuery(query,
                    new SqlParameter("@Id", alarmId));

                LoadAlarmGrid();
            }
            catch (Exception ex)
            {
                ShowErrorOnce(ex.Message);
            }
        }
    }
}
