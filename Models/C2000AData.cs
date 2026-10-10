using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScanningPowerCylinder.Models
{
    /// <summary>
    /// C2000A 电流源数据模型。
    /// 对应 Qt 工程 c2000a_widget: Modbus RTU 串口通信，
    /// 写电流到保持寄存器 0x0000 (float, word-swap),
    /// 读电压自输入寄存器 0x0000 (float, word-swap, 单位 mV)。
    /// </summary>
    public sealed class C2000AData : INotifyPropertyChanged
    {
        private string _portName = "COM1";
        private int _baudRate = 9600;
        private bool _connected;
        private double _writtenCurrent;
        private double _voltageMv;

        public string PortName { get => _portName; set => Set(ref _portName, value); }
        public int BaudRate { get => _baudRate; set => Set(ref _baudRate, value); }
        public bool Connected { get => _connected; set => Set(ref _connected, value); }
        public double WrittenCurrent { get => _writtenCurrent; set => Set(ref _writtenCurrent, value); }
        public double VoltageMv { get => _voltageMv; set => Set(ref _voltageMv, value); }

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
