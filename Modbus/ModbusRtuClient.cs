using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace ScanningPowerCylinder.Modbus
{
    /// <summary>
    /// 简易 Modbus RTU 客户端 (基于 System.IO.Ports.SerialPort)。
    /// 用于 C2000A 电流源：写电流到保持寄存器 (0x0000)，读电压自输入寄存器 (0x0000)。
    /// 帧格式: [从站地址(1)][功能码(1)][数据][CRC16(2, 小端)]
    /// </summary>
    public sealed class ModbusRtuClient : IDisposable
    {
        public const byte FuncReadInputRegisters = 0x04;
        public const byte FuncReadHoldingRegisters = 0x03;
        public const byte FuncWriteSingleRegister = 0x06;

        private const byte DefaultSlaveId = 1;

        private SerialPort? _port;
        private readonly object _ioLock = new();

        public bool IsConnected => _port is not null && _port.IsOpen;
        public int TimeoutMs { get; set; } = 1000;

        /// <summary>
        /// 连接到指定串口。
        /// </summary>
        public void Connect(string portName, int baudRate, int dataBits = 8, Parity parity = Parity.None, StopBits stopBits = StopBits.One)
        {
            Disconnect();
            var port = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
            {
                ReadTimeout = TimeoutMs,
                WriteTimeout = TimeoutMs,
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = true,
                ReadBufferSize = 4096,
                WriteBufferSize = 4096
            };
            port.Open();
            // 清空接收缓冲
            port.DiscardInBuffer();
            _port = port;
        }

        public void Disconnect()
        {
            try
            {
                if (_port is not null && _port.IsOpen)
                    _port.Close();
            }
            catch { /* ignore */ }
            _port = null;
        }

        public void Dispose()
        {
            Disconnect();
            _port?.Dispose();
        }

        /// <summary>
        /// 读取输入寄存器 (功能码 0x04)，用于 C2000A 读电压。
        /// </summary>
        public ushort[] ReadInputRegisters(byte slaveId, ushort startAddr, ushort count)
        {
            if (count == 0) return Array.Empty<ushort>();
            if (count > 125) throw new ArgumentOutOfRangeException(nameof(count));

            // 请求: 从站(1) + 功能码(1) + 起始地址(2) + 数量(2) + CRC(2)
            byte[] request = new byte[8];
            request[0] = slaveId;
            request[1] = FuncReadInputRegisters;
            request[2] = (byte)(startAddr >> 8);
            request[3] = (byte)(startAddr & 0xFF);
            request[4] = (byte)(count >> 8);
            request[5] = (byte)(count & 0xFF);
            AppendCrc(request, 6);

            byte[] response = Transact(request, expectedDataLen: 1 + 1 + count * 2 + 2);
            // 响应: 从站(1) + 功能码(1) + 字节数(1) + 数据(count*2) + CRC(2)
            if (response[1] == (FuncReadInputRegisters | 0x80))
                throw new IOException($"Modbus RTU 异常响应: 功能码 0x{response[2]:X2}");

            ushort[] values = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                int idx = 3 + i * 2;
                values[i] = (ushort)((response[idx] << 8) | response[idx + 1]);
            }
            return values;
        }

        /// <summary>
        /// 写单个保持寄存器 (功能码 0x06)，用于 C2000A 写电流。
        /// </summary>
        public void WriteSingleRegister(byte slaveId, ushort addr, ushort value)
        {
            // 请求/响应: 从站(1) + 功能码(1) + 地址(2) + 值(2) + CRC(2)
            byte[] request = new byte[8];
            request[0] = slaveId;
            request[1] = FuncWriteSingleRegister;
            request[2] = (byte)(addr >> 8);
            request[3] = (byte)(addr & 0xFF);
            request[4] = (byte)(value >> 8);
            request[5] = (byte)(value & 0xFF);
            AppendCrc(request, 6);

            byte[] response = Transact(request, expectedDataLen: 8);
            if (response[1] == (FuncWriteSingleRegister | 0x80))
                throw new IOException($"Modbus RTU 异常响应: 功能码 0x{response[2]:X2}");
        }

        // ---- 便捷重载 ----
        public ushort[] ReadInputRegisters(ushort startAddr, ushort count) => ReadInputRegisters(DefaultSlaveId, startAddr, count);
        public void WriteSingleRegister(ushort addr, ushort value) => WriteSingleRegister(DefaultSlaveId, addr, value);

        // ---- 内部：发送请求并读取响应帧 ----
        private byte[] Transact(byte[] request, int expectedDataLen)
        {
            var port = _port;
            if (port is null || !port.IsOpen)
                throw new IOException("Modbus RTU 串口未打开。");

            lock (_ioLock)
            {
                port.DiscardInBuffer();
                port.Write(request, 0, request.Length);
                // 3.5 个字符间隔 (RTU 帧间隔)。9600bps 约 4ms，统一用 5ms 兜底。
                Thread.Sleep(5);

                // 读取响应：按 expectedDataLen 读取，外加可能存在的从站/功能码等。
                byte[] buffer = new byte[expectedDataLen];
                int totalRead = 0;
                var deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMs);
                while (totalRead < expectedDataLen)
                {
                    if (port.BytesToRead > 0)
                    {
                        int read = port.Read(buffer, totalRead, expectedDataLen - totalRead);
                        totalRead += read;
                    }
                    else
                    {
                        if (DateTime.UtcNow > deadline)
                            throw new IOException($"Modbus RTU 读取超时 (已读 {totalRead}/{expectedDataLen})。");
                        Thread.Sleep(1);
                    }
                }

                // 校验 CRC
                if (!VerifyCrc(buffer, expectedDataLen))
                    throw new IOException("Modbus RTU 响应 CRC 校验失败。");

                return buffer;
            }
        }

        // ---- CRC16 (Modbus 多项式 0xA001) ----
        private static ushort ComputeCrc(byte[] data, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < length; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++)
                {
                    bool lsb = (crc & 0x0001) != 0;
                    crc >>= 1;
                    if (lsb) crc ^= 0xA001;
                }
            }
            return crc;
        }

        private static void AppendCrc(byte[] frame, int dataLength)
        {
            ushort crc = ComputeCrc(frame, dataLength);
            frame[dataLength] = (byte)(crc & 0xFF);     // 低字节在前
            frame[dataLength + 1] = (byte)(crc >> 8);   // 高字节在后
        }

        private static bool VerifyCrc(byte[] frame, int totalLength)
        {
            if (totalLength < 3) return false;
            ushort crc = ComputeCrc(frame, totalLength - 2);
            ushort received = (ushort)((frame[totalLength - 2]) | (frame[totalLength - 1] << 8));
            return crc == received;
        }
    }
}
