using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScanningPowerCylinder.Models
{
    /// <summary>
    /// 扫描电源数据模型，对应 Qt 工程 PowerWidgets (寄存器 0x0300~0x030D, 共 14 个)。
    /// 布局:
    ///   0x0300 freq_cup   u16
    ///   0x0301 control    u16 (0=停止,1=手动,2=自动)
    ///   0x0302 status     u16 (0=正常)
    ///   0x0303 recover    (写)
    ///   0x0304-05 wave1   u32
    ///   0x0306-07 wave2   u32
    ///   0x0308 out_freq   u16 (×0.1 = Hz)
    ///   0x0309 peak1      i16 (×0.001 = V)
    ///   0x030A valley1    i16 (×0.001 = V)
    ///   0x030B peak2      i16 (×0.001 = V)
    ///   0x030C valley2    i16 (×0.001 = V)
    ///   0x030D phase      i16 (°)
    /// </summary>
    public sealed class PowerData : INotifyPropertyChanged
    {
        private int _freqCup;
        private int _control;
        private int _status;
        private uint _wave1;
        private uint _wave2;
        private double _outFreq;
        private double _peak1;
        private double _valley1;
        private double _peak2;
        private double _valley2;
        private int _phase = 180;

        public int FreqCup { get => _freqCup; set => Set(ref _freqCup, value); }
        public int Control { get => _control; set => Set(ref _control, value); }
        public int Status { get => _status; set => Set(ref _status, value); }
        public uint Wave1 { get => _wave1; set => Set(ref _wave1, value); }
        public uint Wave2 { get => _wave2; set => Set(ref _wave2, value); }
        public double OutFreq { get => _outFreq; set => Set(ref _outFreq, value); }
        public double Peak1 { get => _peak1; set => Set(ref _peak1, value); }
        public double Valley1 { get => _valley1; set => Set(ref _valley1, value); }
        public double Peak2 { get => _peak2; set => Set(ref _peak2, value); }
        public double Valley2 { get => _valley2; set => Set(ref _valley2, value); }
        public int Phase { get => _phase; set => Set(ref _phase, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }
    }
}
