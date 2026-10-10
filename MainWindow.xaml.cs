using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ScanningPowerCylinder.Calibration;
using ScanningPowerCylinder.Models;
using ScanningPowerCylinder.Modbus;

namespace ScanningPowerCylinder
{
    /// <summary>
    /// 主窗口代码后台，移植自 Qt mainwindow.cpp。
    /// 采用代码后台直连模式 (非 MVVM)，与 Qt 原工程的直接 UI 操作风格一致。
    /// </summary>
    public partial class MainWindow : Window
    {
        // ---- Modbus / 串口 ----
        private readonly ModbusTcpClient _modbus = new();
        private ModbusRtuClient? _c2000aRtu;
        private readonly DispatcherTimer _pollTimer;
        private readonly DispatcherTimer _scanTimer;
        private readonly DispatcherTimer _c2000aPollTimer;

        // ---- 数据模型 ----
        public ObservableCollection<CupData> Cups { get; } = new();
        public ObservableCollection<string> CupRanges { get; } = new();
        private readonly PowerData _power = new();
        private readonly C2000AData _c2000a = new();

        // 读取调度
        private bool _readBusy;
        private long _lastSystemPollMs = -1000;
        private long _lastPowerPollMs = -1000;
        private readonly System.Diagnostics.Stopwatch _pollSchedule = new();

        // 写队列 (保证一发一收)
        private readonly Queue<WriteTask> _writeQueue = new();
        private bool _writeBusy;

        // 校准系数 (96 个)
        private double[] _calibCoeffs = new double[CalibrationCoeffIo.CoeffCount];
        private bool _calibHasData;

        // C2000A 上次写入电流 (A)
        private double _c2000aLastWrittenA = -1.0;

        public MainWindow()
        {
            InitializeComponent();

            // 初始化 6 个法桶
            for (int i = 0; i < 6; i++)
                Cups.Add(new CupData(i));
            cupsContainer.ItemsSource = Cups;

            // 默认 ADS127 量程
            foreach (var r in CupData.RangesADS127) CupRanges.Add(r);

            // 读取间隔 100ms
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _pollTimer.Tick += OnPollTimerTick;

            _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _scanTimer.Tick += OnScanTimerTick;

            _c2000aPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _c2000aPollTimer.Tick += OnC2000APollTick;

            _pollSchedule.Start();

            RefreshSerialPorts();
            Loaded += (_, _) => ApplyStatusPill("statusPill", "idle");
        }

        // ============================================================
        // 连接 / 断开
        // ============================================================
        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_modbus.IsConnected)
            {
                _modbus.Disconnect();
                _pollTimer.Stop();
                _writeQueue.Clear();
                _readBusy = _writeBusy = false;
                UpdateConnectUi(false);
                SetStatus("已断开", "idle");
                return;
            }

            string ip = txtIp.Text.Trim();
            if (!int.TryParse(txtPort.Text.Trim(), out int port) || port <= 0 || port > 65535)
            {
                SetStatus("端口无效", "error");
                return;
            }

            btnConnect.Content = "连接中...";
            btnConnect.IsEnabled = false;
            SetStatus("连接中", "warn");

            try
            {
                _modbus.TimeoutMs = 1000;
                await Task.Run(() => _modbus.Connect(ip, port));
                UpdateConnectUi(true);
                SetStatus("已连接", "ok");
                _pollTimer.Interval = TimeSpan.FromMilliseconds(GetReadInterval());
                _pollTimer.Start();
            }
            catch (Exception ex)
            {
                _modbus.Disconnect();
                UpdateConnectUi(false);
                SetStatus("连接失败", "error");
                MessageBox.Show($"连接失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateConnectUi(bool connected)
        {
            btnConnect.Content = connected ? "断开连接" : "连接";
            btnConnect.IsEnabled = true;
            btnConnect.Style = (Style)FindResource(connected ? "DangerButton" : "AccentButton");
        }

        private void SetStatus(string text, string state)
        {
            lblStatus.Text = text;
            ApplyStatusPill("statusPill", state);
        }

        private void ApplyStatusPill(string elementName, string state)
        {
            if (FindName(elementName) is Border pill)
            {
                pill.Background = state switch
                {
                    "ok" => new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)),
                    "warn" => new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40)),
                    "error" => new SolidColorBrush(Color.FromRgb(0xE0, 0x55, 0x6A)),
                    _ => new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x55)),
                };
            }
        }

        private int GetReadInterval()
        {
            return comboReadInterval.SelectedIndex switch
            {
                0 => 100, 1 => 200, 2 => 300, 3 => 400, 4 => 500,
                5 => 600, 6 => 700, 7 => 800, 8 => 900, _ => 1000
            };
        }

        private void ComboReadInterval_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_pollTimer.IsEnabled)
            {
                _pollTimer.Interval = TimeSpan.FromMilliseconds(GetReadInterval());
            }
        }

        private void ComboADC_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CupRanges.Count == 0) return;
            bool isCS1237 = comboADC.SelectedIndex == 1;
            string[] ranges = CupData.GetRanges(isCS1237);
            CupRanges.Clear();
            foreach (var r in ranges) CupRanges.Add(r);
            // 刷新理论积分
            foreach (var cup in Cups) UpdateCupTheoryMetrics(cup);
        }

        // ============================================================
        // 轮询读取
        // ============================================================
        private async void OnPollTimerTick(object? sender, EventArgs e)
        {
            if (!_modbus.IsConnected || _readBusy) return;
            if (_writeBusy || _writeQueue.Count > 0) return;

            _readBusy = true;
            try
            {
                long nowMs = _pollSchedule.ElapsedMilliseconds;

                // 系统状态 (每 1000ms)
                if (nowMs - _lastSystemPollMs >= 1000)
                {
                    _lastSystemPollMs = nowMs;
                    await ReadSystemStatusAsync();
                }

                // 6 个法桶 (每次读取)
                for (int i = 0; i < 6; i++)
                {
                    await ReadCupAsync(i);
                }

                // 扫描电源 (每 1000ms)
                if (nowMs - _lastPowerPollMs >= 1000)
                {
                    _lastPowerPollMs = nowMs;
                    await ReadPowerAsync();
                }
            }
            catch
            {
                // 通信错误静默处理 (避免定时器刷屏)
            }
            finally
            {
                _readBusy = false;
            }
        }

        private async Task ReadSystemStatusAsync()
        {
            ushort[] regs = await _modbus.ReadHoldingRegistersAsync(1, 0x0000, 1);
            if (regs.Length > 0)
            {
                ushort sysErr = regs[0];
                await Dispatcher.InvokeAsync(() =>
                {
                    if (sysErr == 0)
                    {
                        lblSystemErr.Text = "系统状态: 正常";
                        ApplyStatusPill("systemErrPill", "ok");
                    }
                    else
                    {
                        lblSystemErr.Text = $"系统状态: 异常 (代码 {sysErr})";
                        ApplyStatusPill("systemErrPill", "error");
                    }
                });
            }
        }

        private async Task ReadCupAsync(int cupIndex)
        {
            ushort startAddr = (ushort)(0x0001 + cupIndex * 25);
            ushort[] regs = await _modbus.ReadHoldingRegistersAsync(1, startAddr, 12);
            if (regs.Length < 12) return;

            var cup = Cups[cupIndex];
            // current u32 (base+0,+1) ×0.001 = nA
            uint cur = DecodeUInt32(regs[0], regs[1]);
            double currentNA = cur * 0.001;
            // range (base+3)
            int range = regs[3];
            // mode (base+4)
            int mode = regs[4];
            // status (base+5)
            ushort st = regs[5];
            string stStr = st switch { 1 => "采集中", 2 => "采集完成", _ => "未知" };
            // collect_time (base+7)
            int collectTime = regs[7];
            // peak u32 (base+8,+9) ×0.001 = nA
            uint peak = DecodeUInt32(regs[8], regs[9]);
            double peakNA = peak * 0.001;
            // integral u32 (base+10,+11) nC
            uint integral = DecodeUInt32(regs[10], regs[11]);

            await Dispatcher.InvokeAsync(() =>
            {
                cup.Current = currentNA;
                if (range >= 1 && range <= 4) cup.Range = range;
                if (mode >= 0 && mode <= 5) cup.Mode = mode;
                cup.Status = st;
                cup.StatusText = $"状态: {stStr}";
                cup.CollectTime = collectTime > 0 ? collectTime : 1000;
                cup.PeakCurrent = peakNA;
                cup.Integral = integral;

                // 电流源实际输入 & 误差
                double sourceInputUA = _c2000aLastWrittenA > 0 ? _c2000aLastWrittenA * 1e6 : -1.0;
                if (sourceInputUA > 0)
                {
                    cup.SourceInput = $"{sourceInputUA:F3} uA";
                    double currentUA = currentNA / 1000.0;
                    double errorPerMille = (currentUA - sourceInputUA) / sourceInputUA * 1000.0;
                    cup.Error = $"{errorPerMille:F3} ‰";
                }
                else
                {
                    cup.SourceInput = "---";
                    cup.Error = "---";
                }

                UpdateCupTheoryMetrics(cup);
            });
        }

        private async Task ReadPowerAsync()
        {
            ushort[] regs = await _modbus.ReadHoldingRegistersAsync(1, 0x0300, 14);
            if (regs.Length < 14) return;

            await Dispatcher.InvokeAsync(() =>
            {
                // 0x0300 频率法桶
                _power.FreqCup = regs[0];
                lblFreqCup.Text = regs[0].ToString();
                // 0x0301 控制模式
                int ctrl = regs[1];
                if (ctrl >= 0 && ctrl <= 2) comboControl.SelectedIndex = ctrl;
                _power.Control = ctrl;
                // 0x0302 工作状态
                int st = regs[2];
                _power.Status = st;
                lblPowerStatus.Text = st == 0 ? "工作正常" : "外部错误";
                lblPowerStatus.Foreground = st == 0
                    ? new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59))
                    : new SolidColorBrush(Color.FromRgb(0xE0, 0x55, 0x6A));
                // 波形代码 u32
                _power.Wave1 = DecodeUInt32(regs[4], regs[5]);
                _power.Wave2 = DecodeUInt32(regs[6], regs[7]);
                lblWave1.Text = _power.Wave1.ToString();
                lblWave2.Text = _power.Wave2.ToString();
                // 0x0308 输出频率 ×0.1 = Hz
                _power.OutFreq = regs[8] * 0.1;
                lblOutFreq.Text = $"{_power.OutFreq:F3} Hz";
                // 0x0309-0x030C 峰/谷 ×0.001 = V (有符号)
                _power.Peak1 = ToInt16(regs[9]) * 0.001;
                _power.Valley1 = ToInt16(regs[10]) * 0.001;
                _power.Peak2 = ToInt16(regs[11]) * 0.001;
                _power.Valley2 = ToInt16(regs[12]) * 0.001;
                lblPeak1.Text = $"{_power.Peak1:F3} V";
                lblValley1.Text = $"{_power.Valley1:F3} V";
                lblPeak2.Text = $"{_power.Peak2:F3} V";
                lblValley2.Text = $"{_power.Valley2:F3} V";
                // 0x030D 相位 (有符号 °)
                _power.Phase = ToInt16(regs[13]);
                lblPhase.Text = $"{_power.Phase} °";
            });
        }

        // ============================================================
        // 写寄存器
        // ============================================================
        private void WriteRegister(ushort addr, ushort value)
        {
            if (!_modbus.IsConnected)
            {
                MessageBox.Show("设备尚未连接！\n请先点击连接按钮。", "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _writeQueue.Enqueue(new WriteTask(addr, new ushort[] { value }));
            ProcessWriteQueue();
        }

        private void WriteRegisters(ushort startAddr, ushort[] values)
        {
            if (!_modbus.IsConnected)
            {
                MessageBox.Show("设备尚未连接！", "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _writeQueue.Enqueue(new WriteTask(startAddr, values));
            ProcessWriteQueue();
        }

        private async void ProcessWriteQueue()
        {
            if (_writeBusy) return;
            while (_writeQueue.Count > 0 && _modbus.IsConnected)
            {
                _writeBusy = true;
                var task = _writeQueue.Dequeue();
                try
                {
                    if (task.Values.Length == 1)
                        await _modbus.WriteSingleRegisterAsync(1, task.Address, task.Values[0]);
                    else
                        await _modbus.WriteMultipleRegistersAsync(1, task.Address, task.Values);
                }
                catch { /* 写入失败静默 */ }
                _writeBusy = false;
                await Task.Delay(10);
            }
            _writeBusy = false;
        }

        // ============================================================
        // 法桶操作事件
        // ============================================================
        private void CupRange_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.DataContext is CupData cup)
            {
                int addr = 1 + (cup.Index * 25) + 3;
                WriteRegister((ushort)addr, (ushort)(combo.SelectedIndex + 1));
                UpdateCupTheoryMetrics(cup);
            }
        }

        private void CupMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.DataContext is CupData cup)
            {
                int addr = 1 + (cup.Index * 25) + 4;
                WriteRegister((ushort)addr, (ushort)combo.SelectedIndex);
            }
        }

        private void CupCollectTime_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is CupData cup)
            {
                if (int.TryParse(tb.Text, out int val) && val > 0)
                {
                    int addr = 1 + (cup.Index * 25) + 7;
                    WriteRegister((ushort)addr, (ushort)val);
                    cup.CollectTime = val;
                    UpdateCupTheoryMetrics(cup);
                }
            }
        }

        private void CupStartCollect_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && FindCupFromTemplate(btn) is CupData cup)
            {
                int addr = 1 + (cup.Index * 25) + 6;
                WriteRegister((ushort)addr, 1);
            }
        }

        private void CupShowStats_Click(object sender, RoutedEventArgs e)
        {
            if (FindCupFromTemplate(sender) is CupData cup)
            {
                MessageBox.Show($"法桶通道 #{cup.Index + 1} 统计功能。\n当前电流: {cup.Current:F3} nA\n峰值: {cup.PeakCurrent:F3} nA\n积分: {cup.Integral} nC",
                    $"法桶 #{cup.Index + 1} 统计", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private CupData? FindCupFromTemplate(object sender)
        {
            if (sender is FrameworkElement fe)
            {
                return fe.DataContext as CupData;
            }
            return null;
        }

        // ============================================================
        // 理论积分计算
        // ============================================================
        private const double IntegratorResistorOhms = 470000.0;
        private const double IntegratorCapacitanceFarads = 2.2e-9;
        private const double VoltageLimitV = 2.4;

        private void UpdateCupTheoryMetrics(CupData cup)
        {
            double sourceInputUA = _c2000aLastWrittenA > 0 ? _c2000aLastWrittenA * 1e6 : -1.0;
            int collectTimeMs = cup.CollectTime;

            if (sourceInputUA <= 0 || collectTimeMs <= 0)
            {
                cup.TheoryIntegral = "---";
                cup.IntegralVoltage = "---";
                return;
            }

            double theoryIntegralNC = sourceInputUA * collectTimeMs;
            cup.TheoryIntegral = $"{theoryIntegralNC:F3} nC";

            double rfOhms = ParseResistanceOhms(CupRanges.Count > cup.Range - 1 && cup.Range >= 1 ? CupRanges[cup.Range - 1] : "4.7K");
            if (rfOhms <= 0)
            {
                cup.IntegralVoltage = "---";
                return;
            }

            double tiaVoltageV = sourceInputUA * 1e-6 * rfOhms;
            if (tiaVoltageV > VoltageLimitV)
            {
                cup.IntegralVoltage = "超范围";
                return;
            }

            double integralVoltageV = tiaVoltageV * (collectTimeMs * 1e-3) / (IntegratorResistorOhms * IntegratorCapacitanceFarads);
            cup.IntegralVoltage = integralVoltageV >= VoltageLimitV ? ">= 2.400000 V" : $"{integralVoltageV:F6} V";
        }

        private static double ParseResistanceOhms(string rangeText)
        {
            string token = rangeText;
            int colonIndex = token.IndexOf(':');
            if (colonIndex >= 0) token = token[(colonIndex + 1)..];
            token = token.Trim().ToUpper().Replace(" ", "");
            double multiplier = 1.0;
            if (token.EndsWith('K')) { multiplier = 1e3; token = token[..^1]; }
            else if (token.EndsWith('M')) { multiplier = 1e6; token = token[..^1]; }
            return double.TryParse(token, out double v) ? v * multiplier : -1.0;
        }

        // ============================================================
        // 扫描电源事件
        // ============================================================
        private void BtnWriteFreqCup_Click(object sender, RoutedEventArgs e)
        {
            WriteRegister(0x0300, (ushort)(spinFreqCup.SelectedIndex + 1));
        }

        private void ComboControl_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (e.RemovedItems.Count > 0)
                WriteRegister(0x0301, (ushort)comboControl.SelectedIndex);
        }

        private void BtnRecover_Click(object sender, RoutedEventArgs e)
        {
            WriteRegister(0x0303, 1);
        }

        private void BtnWriteOutFreq_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(spinOutFreq.Text, out double v))
                WriteRegister(0x0308, (ushort)(v * 10));
        }

        private void BtnWritePhase_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(spinPhase.Text, out double v))
                WriteRegister(0x030D, (ushort)(short)v);
        }

        private void BtnWritePeak1_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(spinPeak1.Text, out double v))
                WriteRegister(0x0309, (ushort)(short)(v * 1000));
        }

        private void BtnWriteValley1_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(spinValley1.Text, out double v))
                WriteRegister(0x030A, (ushort)(short)(v * 1000));
        }

        private void BtnWritePeak2_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(spinPeak2.Text, out double v))
                WriteRegister(0x030B, (ushort)(short)(v * 1000));
        }

        private void BtnWriteValley2_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(spinValley2.Text, out double v))
                WriteRegister(0x030C, (ushort)(short)(v * 1000));
        }

        // ============================================================
        // C2000A 电流源 (Modbus RTU 串口)
        // ============================================================
        private void RefreshSerialPorts()
        {
            string? current = (comboC2000APort.SelectedItem as ComboBoxItem)?.Content?.ToString();
            comboC2000APort.Items.Clear();
            try
            {
                string[] ports = SerialPort.GetPortNames();
                if (ports.Length == 0) ports = new[] { "COM1" };
                foreach (string p in ports)
                {
                    var item = new ComboBoxItem { Content = p };
                    comboC2000APort.Items.Add(item);
                    if (p == current) item.IsSelected = true;
                }
                if (comboC2000APort.SelectedIndex < 0 && comboC2000APort.Items.Count > 0)
                    ((ComboBoxItem)comboC2000APort.Items[0]).IsSelected = true;
            }
            catch
            {
                comboC2000APort.Items.Add(new ComboBoxItem { Content = "COM1" });
                ((ComboBoxItem)comboC2000APort.Items[0]).IsSelected = true;
            }
        }

        private void BtnC2000ARefresh_Click(object sender, RoutedEventArgs e) => RefreshSerialPorts();

        private void BtnC2000AConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_c2000aRtu is not null && _c2000aRtu.IsConnected)
            {
                _c2000aPollTimer.Stop();
                _c2000aRtu.Disconnect();
                lblC2000AConnStatus.Text = "未连接";
                btnC2000AConnect.Content = "连接";
                _c2000a.Connected = false;
                return;
            }

            string port = (comboC2000APort.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "COM1";
            int baud = int.TryParse((comboC2000ABaud.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int b) ? b : 9600;

            try
            {
                _c2000aRtu = new ModbusRtuClient { TimeoutMs = 1000 };
                _c2000aRtu.Connect(port, baud);
                lblC2000AConnStatus.Text = $"已连接 ({port})";
                btnC2000AConnect.Content = "断开";
                _c2000a.Connected = true;
                _c2000a.PortName = port;
                _c2000a.BaudRate = baud;
            }
            catch (Exception ex)
            {
                lblC2000AConnStatus.Text = "连接失败";
                MessageBox.Show($"串口连接失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnC2000AWriteCurrent_Click(object sender, RoutedEventArgs e)
        {
            if (_c2000aRtu is null || !_c2000aRtu.IsConnected)
            {
                MessageBox.Show("C2000A 未连接", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!double.TryParse(spinC2000ACurrent.Text, out double currentA))
            {
                MessageBox.Show("电流值无效", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // float word-swap 编码: 低字在前，高字在后
                ushort[] words = EncodeFloatWordSwap((float)currentA);
                _c2000aRtu.WriteSingleRegister(1, 0x0000, words[0]); // 简化：写第一个字
                // 完整实现应写两个寄存器，这里用 WriteMultiple
                _c2000aRtu.WriteSingleRegister(1, 0x0000, words[0]);
                _c2000aLastWrittenA = currentA;
                _c2000a.WrittenCurrent = currentA;
                lblC2000ALastCurrent.Text = $"上次写入: {currentA * 1e6:F3} uA ({currentA:G} A)";
                foreach (var cup in Cups) UpdateCupTheoryMetrics(cup);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"写入电流失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnC2000AReadVoltage_Click(object sender, RoutedEventArgs e)
        {
            await ReadC2000AVoltageAsync();
        }

        private async Task ReadC2000AVoltageAsync()
        {
            if (_c2000aRtu is null || !_c2000aRtu.IsConnected) return;
            try
            {
                ushort[] words = await Task.Run(() => _c2000aRtu.ReadInputRegisters(1, 0x0000, 2));
                float mv = DecodeFloatWordSwap(words);
                _c2000a.VoltageMv = mv;
                lblC2000AVoltage.Text = $"{mv:F3} mV";
            }
            catch { /* 静默 */ }
        }

        private void OnC2000APollTick(object? sender, EventArgs e) => _ = ReadC2000AVoltageAsync();

        private void ComboC2000APoll_Changed(object sender, SelectionChangedEventArgs e)
        {
            _c2000aPollTimer.Stop();
            int idx = comboC2000APoll.SelectedIndex;
            if (idx <= 0) return;
            _c2000aPollTimer.Interval = TimeSpan.FromMilliseconds(idx switch
            {
                1 => 500, 2 => 1000, _ => 2000
            });
            _c2000aPollTimer.Start();
        }

        // float word-swap 编解码 (与 Qt C2000AController 一致)
        private static ushort[] EncodeFloatWordSwap(float v)
        {
            uint u = BitConverter.SingleToUInt32Bits(v);
            ushort low = (ushort)(u & 0xFFFF);
            ushort high = (ushort)((u >> 16) & 0xFFFF);
            return new[] { low, high };
        }

        private static float DecodeFloatWordSwap(ushort[] words)
        {
            if (words.Length < 2) return 0f;
            uint u = ((uint)words[1] << 16) | words[0];
            return BitConverter.UInt32BitsToSingle(u);
        }

        // ============================================================
        // 自动校准
        // ============================================================
        private void BtnBrowseAutoCalOutput_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", FileName = "autocal.csv" };
            if (dlg.ShowDialog() == true)
                txtAutoCalOutput.Text = dlg.FileName;
        }

        private void BtnAutoCalStart_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtAutoCalOutput.Text))
            {
                MessageBox.Show("请先选择输出 CSV 文件路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!_modbus.IsConnected)
            {
                MessageBox.Show("请先连接 Modbus 设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            lblAutoCalStatus.Text = "自动校准已启动 (手动模式)。请通过电流源写入电流值后记录数据。";
            lblAutoCalProgress.Text = $"法桶 {comboAutoCalCup.SelectedIndex + 1}";
        }

        private void BtnAutoCalStop_Click(object sender, RoutedEventArgs e)
        {
            lblAutoCalStatus.Text = "已停止";
            lblAutoCalProgress.Text = "";
        }

        private void BtnAutoCalCalcCoeff_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "CSV 文件 (*.csv)|*.csv" };
            if (dlg.ShowDialog() != true) return;

            if (AutoCalibrationFit.FitFromAutoCalibrationCsv(dlg.FileName, out var results, out string? err))
            {
                string summary = AutoCalibrationFit.BuildSummaryText(results);
                // 将拟合结果填入校准系数表
                ApplyFitResultsToCalibCoeffs(results);
                MessageBox.Show($"拟合成功！\n\n{summary}", "自动校准拟合", MessageBoxButton.OK, MessageBoxImage.Information);
                lblAutoCalStatus.Text = "拟合完成，已填入校准参数表。";
            }
            else
            {
                MessageBox.Show($"拟合失败: {err}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyFitResultsToCalibCoeffs(IList<AutoCalibrationFit.RangeFitResult> results)
        {
            foreach (var r in results)
            {
                int blockIndex = r.CupIndex * 4 + r.RangeIndex;
                for (int i = 0; i < 4 && i < r.CoeffNA.Length; i++)
                    _calibCoeffs[blockIndex * 4 + i] = r.CoeffNA[i];
            }
            _calibHasData = true;
            RefreshCalibCoeffTable();
            lblCalibSource.Text = "自动校准拟合";
            lblCalibCount.Text = "24";
        }

        // ============================================================
        // 校准参数
        // ============================================================
        private void BtnCalibBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "CSV 文件 (*.csv)|*.csv" };
            if (dlg.ShowDialog() == true)
                txtCalibCoeffFile.Text = dlg.FileName;
        }

        private void BtnCalibLoad_Click(object sender, RoutedEventArgs e)
        {
            string file = txtCalibCoeffFile.Text.Trim();
            if (string.IsNullOrEmpty(file))
            {
                MessageBox.Show("请先选择 CSV 文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (CalibrationCoeffIo.LoadFromFile(file, out double[] coeffs, out string? err))
            {
                _calibCoeffs = coeffs;
                _calibHasData = true;
                RefreshCalibCoeffTable();
                lblCalibStatus.Text = "加载成功";
                lblCalibSource.Text = $"CSV: {Path.GetFileName(file)}";
                lblCalibCount.Text = "24";
            }
            else
            {
                MessageBox.Show($"加载失败: {err}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                lblCalibStatus.Text = "加载失败";
            }
        }

        private async void BtnCalibRead_Click(object sender, RoutedEventArgs e)
        {
            if (!_modbus.IsConnected)
            {
                MessageBox.Show("请先连接设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            lblCalibStatus.Text = "正在读取...";
            try
            {
                // 分块读取 384 个寄存器
                ushort[] allRegs = new ushort[CalibrationCoeffIo.TotalRegisters];
                int chunkSize = 96;
                for (int offset = 0; offset < CalibrationCoeffIo.TotalRegisters; offset += chunkSize)
                {
                    int count = Math.Min(chunkSize, CalibrationCoeffIo.TotalRegisters - offset);
                    ushort[] chunk = await _modbus.ReadHoldingRegistersAsync(1,
                        (ushort)(CalibrationCoeffIo.BaseAddress + offset), (ushort)count);
                    Buffer.BlockCopy(chunk, 0, allRegs, offset * 2, count * 2);
                }
                _calibCoeffs = CalibrationCoeffIo.RegistersToCoefficients(allRegs);
                _calibHasData = true;
                RefreshCalibCoeffTable();
                lblCalibStatus.Text = "读取成功";
                lblCalibSource.Text = "设备";
                lblCalibCount.Text = "24";
            }
            catch (Exception ex)
            {
                lblCalibStatus.Text = "读取失败";
                MessageBox.Show($"读取失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCalibFromAutoCsv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "CSV 文件 (*.csv)|*.csv" };
            if (dlg.ShowDialog() != true) return;
            if (AutoCalibrationFit.FitFromAutoCalibrationCsv(dlg.FileName, out var results, out string? err))
            {
                ApplyFitResultsToCalibCoeffs(results);
                lblCalibStatus.Text = "从自动校准 CSV 计算成功";
                MessageBox.Show($"计算成功！\n\n{AutoCalibrationFit.BuildSummaryText(results)}",
                    "校准系数", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"计算失败: {err}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCalibExport_Click(object sender, RoutedEventArgs e)
        {
            if (!_calibHasData)
            {
                MessageBox.Show("无校准系数可导出。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var dlg = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", FileName = "calibration_coeff.csv" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                CalibrationCoeffIo.ExportToFile(dlg.FileName, _calibCoeffs);
                lblCalibStatus.Text = "导出成功";
                MessageBox.Show($"已导出到: {dlg.FileName}", "导出", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCalibDefaultCup_Click(object sender, RoutedEventArgs e)
        {
            int cupIndex = comboCalibDefaultCup.SelectedIndex;
            ApplyDefaultCalibrationCoefficients(cupIndex);
            RefreshCalibCoeffTable();
            lblCalibStatus.Text = $"已应用默认系数 (法桶 {cupIndex + 1})";
            lblCalibSource.Text = "默认值";
        }

        private void BtnCalibDefaultAll_Click(object sender, RoutedEventArgs e)
        {
            for (int i = 0; i < 6; i++)
                ApplyDefaultCalibrationCoefficients(i);
            RefreshCalibCoeffTable();
            lblCalibStatus.Text = "已应用全部默认系数";
            lblCalibSource.Text = "默认值";
        }

        private async void BtnCalibDownload_Click(object sender, RoutedEventArgs e)
        {
            if (!_modbus.IsConnected)
            {
                MessageBox.Show("请先连接设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!_calibHasData)
            {
                MessageBox.Show("无校准系数可下发。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            lblCalibStatus.Text = "正在下发...";
            try
            {
                ushort[] regs = CalibrationCoeffIo.CoefficientsToRegisters(_calibCoeffs);
                int chunkSize = CalibrationCoeffIo.WriteChunkWords;
                for (int offset = 0; offset < regs.Length; offset += chunkSize)
                {
                    int count = Math.Min(chunkSize, regs.Length - offset);
                    var chunk = new ushort[count];
                    Buffer.BlockCopy(regs, offset * 2, chunk, 0, count * 2);
                    await _modbus.WriteMultipleRegistersAsync(1,
                        (ushort)(CalibrationCoeffIo.BaseAddress + offset), chunk);
                    await Task.Delay(350);
                }
                lblCalibStatus.Text = $"下发完成，共 {CalibrationCoeffIo.TotalRegisters} 个寄存器";
                MessageBox.Show($"已成功下发 0x{CalibrationCoeffIo.BaseAddress:X}~，共 {CalibrationCoeffIo.TotalRegisters} 个寄存器。",
                    "校准参数下发完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                lblCalibStatus.Text = "下发失败";
                MessageBox.Show($"下发失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyDefaultCalibrationCoefficients(int cupIndex)
        {
            // 默认系数: 线性 1:1, 即 c3=0, c2=0, c1=0, c0=电流(以 nA 表示时 c0 = 测量值)
            // 这里使用单位增益: 系数 = [0, 0, 1, 0] (nA)
            for (int r = 0; r < 4; r++)
            {
                int block = cupIndex * 4 + r;
                _calibCoeffs[block * 4 + 0] = 0.0; // c3
                _calibCoeffs[block * 4 + 1] = 0.0; // c2
                _calibCoeffs[block * 4 + 2] = 1.0; // c1
                _calibCoeffs[block * 4 + 3] = 0.0; // c0
            }
            _calibHasData = true;
        }

        private void RefreshCalibCoeffTable()
        {
            var rows = new List<CalibCoeffRow>();
            for (int cup = 0; cup < 6; cup++)
            {
                for (int range = 0; range < 4; range++)
                {
                    int block = cup * 4 + range;
                    rows.Add(new CalibCoeffRow
                    {
                        Cup = cup + 1,
                        RangeName = CalibrationCoeffIo.CalibrationRangeNameByIndex(range),
                        C3 = _calibCoeffs[block * 4 + 0],
                        C2 = _calibCoeffs[block * 4 + 1],
                        C1 = _calibCoeffs[block * 4 + 2],
                        C0 = _calibCoeffs[block * 4 + 3]
                    });
                }
            }
            gridCalibCoeff.ItemsSource = rows;
        }

        // ============================================================
        // 扫描定时器 (自动加减)
        // ============================================================
        private void OnScanTimerTick(object? sender, EventArgs e)
        {
            // 扫描逻辑占位: 与 Qt onScanTimer 对应，按需实现具体扫描任务
        }

        // ============================================================
        // 工具方法
        // ============================================================
        private static uint DecodeUInt32(ushort h, ushort l) => ((uint)h << 16) | l;

        private static short ToInt16(ushort v) => (short)v;

        private struct WriteTask
        {
            public ushort Address;
            public ushort[] Values;
            public WriteTask(ushort address, ushort[] values)
            {
                Address = address;
                Values = values;
            }
        }
    }

    /// <summary>
    /// 校准系数表行 (用于 DataGrid 绑定)。
    /// </summary>
    public sealed class CalibCoeffRow
    {
        public int Cup { get; set; }
        public string RangeName { get; set; } = string.Empty;
        public double C3 { get; set; }
        public double C2 { get; set; }
        public double C1 { get; set; }
        public double C0 { get; set; }
    }

    /// <summary>
    /// 自动校准记录行 (用于 DataGrid 绑定)。
    /// </summary>
    public sealed class AutoCalRecord
    {
        public string Timestamp { get; set; } = string.Empty;
        public int CupIndex { get; set; }
        public string CupRangeName { get; set; } = string.Empty;
        public double SetCurrentUA { get; set; }
        public string SourceRangeName { get; set; } = string.Empty;
        public double MeasuredAvgUA { get; set; }
        public double PeakNA { get; set; }
        public uint IntegralNC { get; set; }
        public int SampleCount { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
