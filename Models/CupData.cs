using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScanningPowerCylinder.Models
{
    /// <summary>
    /// 法桶通道数据模型，对应 Qt 工程的 CupWidgets + 12 个寄存器解析结果。
    /// 寄存器布局 (起始 0x0001 + cupIndex*25, 共 12 个):
    ///   +0,+1  current    u32  (×0.001 = nA)
    ///   +2     reserved
    ///   +3     range      u16  (1..4)
    ///   +4     mode       u16  (0..5)
    ///   +5     status     u16  (1=采集中, 2=采集完成)
    ///   +6     start collect trigger
    ///   +7     collect_time u16 (ms)
    ///   +8,+9  peak       u32  (×0.001 = nA)
    ///   +10,+11 integral  u32  (nC)
    /// </summary>
    public sealed class CupData : INotifyPropertyChanged
    {
        public int Index { get; }

        private double _current;
        private int _range;
        private int _mode;
        private int _status;
        private int _collectTime = 1000;
        private double _peakCurrent;
        private uint _integral;
        private string _theoryIntegral = "---";
        private string _integralVoltage = "---";
        private string _sourceInput = "---";
        private string _error = "---";
        private string _statusText = "状态未知";

        public CupData(int index)
        {
            Index = index;
        }

        /// <summary>当前电流 (nA)。原始寄存器值 ×0.001。</summary>
        public double Current
        {
            get => _current;
            set => Set(ref _current, value);
        }

        /// <summary>量程档位 (1..4)。</summary>
        public int Range
        {
            get => _range;
            set => Set(ref _range, value);
        }

        /// <summary>采集模式 (0..5)。</summary>
        public int Mode
        {
            get => _mode;
            set => Set(ref _mode, value);
        }

        /// <summary>采集状态原始值 (1=采集中, 2=采集完成)。</summary>
        public int Status
        {
            get => _status;
            set => Set(ref _status, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        /// <summary>采集时间 (ms)。</summary>
        public int CollectTime
        {
            get => _collectTime;
            set => Set(ref _collectTime, value);
        }

        /// <summary>峰值电流 (nA)。原始寄存器值 ×0.001。</summary>
        public double PeakCurrent
        {
            get => _peakCurrent;
            set => Set(ref _peakCurrent, value);
        }

        /// <summary>积分电流 (nC)。</summary>
        public uint Integral
        {
            get => _integral;
            set => Set(ref _integral, value);
        }

        public string TheoryIntegral
        {
            get => _theoryIntegral;
            set => Set(ref _theoryIntegral, value);
        }

        public string IntegralVoltage
        {
            get => _integralVoltage;
            set => Set(ref _integralVoltage, value);
        }

        public string SourceInput
        {
            get => _sourceInput;
            set => Set(ref _sourceInput, value);
        }

        public string Error
        {
            get => _error;
            set => Set(ref _error, value);
        }

        // ---- 静态量程表 ----

        /// <summary>ADS127 量程列表 (默认)。</summary>
        public static readonly string[] RangesADS127 = { "1: 4.7K", "2: 20K", "3: 47K", "4: 499K" };

        /// <summary>CS1237 量程列表。</summary>
        public static readonly string[] RangesCS1237 = { "1: 10K", "2: 50K", "3: 100K", "4: 500K" };

        /// <summary>根据 ADC 类型返回量程列表。</summary>
        public static string[] GetRanges(bool isCS1237) => isCS1237 ? RangesCS1237 : RangesADS127;

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
