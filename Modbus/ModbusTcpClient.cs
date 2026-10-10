using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ScanningPowerCylinder.Modbus
{
    /// <summary>
    /// 简易 Modbus TCP 客户端，手工构建 MBAP 头 + PDU，避免依赖第三方 NuGet 包。
    /// 协议参考：https://modbus.org/specs
    /// MBAP 头 (7 字节): 事务ID(2) + 协议ID(2,固定0) + 长度(2) + 单元ID(1)
    /// </summary>
    public sealed class ModbusTcpClient : IDisposable
    {
        private const int ModbusProtocolId = 0;
        // Modbus 功能码
        public const byte FuncReadHoldingRegisters = 0x03;
        public const byte FuncWriteSingleRegister = 0x06;
        public const byte FuncWriteMultipleRegisters = 0x10;

        private const byte DefaultUnitId = 1;

        private TcpClient? _client;
        private NetworkStream? _stream;
        private ushort _transactionId;
        private readonly object _sendLock = new();

        public bool IsConnected => _client is not null && _client.Connected;

        public int TimeoutMs { get; set; } = 1000;

        /// <summary>
        /// 连接到远端 Modbus TCP 从站。
        /// </summary>
        public void Connect(string ipAddress, int port)
        {
            Disconnect();
            var client = new TcpClient();
            var connectTask = client.ConnectAsync(ipAddress, port);
            if (!connectTask.Wait(TimeoutMs))
            {
                try { client.Close(); } catch { /* ignore */ }
                throw new IOException($"连接 {ipAddress}:{port} 超时");
            }
            // 触发异常（如有）
            connectTask.Wait();
            _client = client;
            _stream = client.GetStream();
            _stream.ReadTimeout = TimeoutMs;
            _stream.WriteTimeout = TimeoutMs;
            _transactionId = 0;
        }

        /// <summary>
        /// 断开连接。
        /// </summary>
        public void Disconnect()
        {
            try { _stream?.Close(); } catch { /* ignore */ }
            try { _client?.Close(); } catch { /* ignore */ }
            _stream = null;
            _client = null;
        }

        public void Dispose() => Disconnect();

        /// <summary>
        /// 读取保持寄存器 (功能码 0x03)。
        /// </summary>
        public async Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort startAddr, ushort count)
        {
            if (count == 0) return Array.Empty<ushort>();
            if (count > 125) throw new ArgumentOutOfRangeException(nameof(count), "单次最多读取 125 个寄存器。");

            // PDU: 功能码(1) + 起始地址(2) + 数量(2)
            byte[] pdu = new byte[5];
            pdu[0] = FuncReadHoldingRegisters;
            pdu[1] = (byte)(startAddr >> 8);
            pdu[2] = (byte)(startAddr & 0xFF);
            pdu[3] = (byte)(count >> 8);
            pdu[4] = (byte)(count & 0xFF);

            byte[] response = await SendRequestAsync(slaveId, pdu, expectedPayloadMin: 1 + 1 + count * 2);
            // 响应 PDU: 功能码(1) + 字节数(1) + 数据(count*2)
            byte func = response[0];
            if (func == (FuncReadHoldingRegisters | 0x80))
                throw new IOException($"Modbus 异常响应: 功能码 0x{response[1]:X2}");

            int byteCount = response[1];
            ushort[] values = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                int idx = 2 + i * 2;
                values[i] = (ushort)((response[idx] << 8) | response[idx + 1]);
            }
            return values;
        }

        /// <summary>
        /// 写单个寄存器 (功能码 0x06)。
        /// </summary>
        public async Task WriteSingleRegisterAsync(byte slaveId, ushort addr, ushort value)
        {
            byte[] pdu = new byte[5];
            pdu[0] = FuncWriteSingleRegister;
            pdu[1] = (byte)(addr >> 8);
            pdu[2] = (byte)(addr & 0xFF);
            pdu[3] = (byte)(value >> 8);
            pdu[4] = (byte)(value & 0xFF);

            byte[] response = await SendRequestAsync(slaveId, pdu, expectedPayloadMin: 5);
            if (response[0] == (FuncWriteSingleRegister | 0x80))
                throw new IOException($"Modbus 异常响应: 功能码 0x{response[1]:X2}");
        }

        /// <summary>
        /// 写多个寄存器 (功能码 0x10)。
        /// </summary>
        public async Task WriteMultipleRegistersAsync(byte slaveId, ushort startAddr, ushort[] values)
        {
            if (values is null || values.Length == 0) return;
            if (values.Length > 123) throw new ArgumentOutOfRangeException(nameof(values), "单次最多写 123 个寄存器。");

            // PDU: 功能码(1) + 起始地址(2) + 数量(2) + 字节数(1) + 数据(n*2)
            byte[] pdu = new byte[6 + values.Length * 2];
            pdu[0] = FuncWriteMultipleRegisters;
            pdu[1] = (byte)(startAddr >> 8);
            pdu[2] = (byte)(startAddr & 0xFF);
            pdu[3] = (byte)(values.Length >> 8);
            pdu[4] = (byte)(values.Length & 0xFF);
            pdu[5] = (byte)(values.Length * 2);
            for (int i = 0; i < values.Length; i++)
            {
                pdu[6 + i * 2] = (byte)(values[i] >> 8);
                pdu[7 + i * 2] = (byte)(values[i] & 0xFF);
            }

            byte[] response = await SendRequestAsync(slaveId, pdu, expectedPayloadMin: 5);
            if (response[0] == (FuncWriteMultipleRegisters | 0x80))
                throw new IOException($"Modbus 异常响应: 功能码 0x{response[1]:X2}");
        }

        // ---- 便捷重载（默认单元 ID = 1，同步语义靠异步阻塞实现） ----

        public ushort[] ReadHoldingRegisters(ushort startAddr, ushort count)
            => ReadHoldingRegistersAsync(DefaultUnitId, startAddr, count).GetAwaiter().GetResult();

        public void WriteSingleRegister(ushort addr, ushort value)
            => WriteSingleRegisterAsync(DefaultUnitId, addr, value).GetAwaiter().GetResult();

        public void WriteMultipleRegisters(ushort startAddr, ushort[] values)
            => WriteMultipleRegistersAsync(DefaultUnitId, startAddr, values).GetAwaiter().GetResult();

        // ---- 内部：发送请求并读取完整响应 ----

        private async Task<byte[]> SendRequestAsync(byte slaveId, byte[] pdu, int expectedPayloadMin)
        {
            var stream = _stream;
            var client = _client;
            if (stream is null || client is null || !client.Connected)
                throw new IOException("Modbus TCP 未连接。");

            ushort txId;
            lock (_sendLock)
            {
                txId = ++_transactionId;
            }

            // MBAP 头 7 字节
            byte[] header = new byte[7];
            header[0] = (byte)(txId >> 8);
            header[1] = (byte)(txId & 0xFF);
            header[2] = (byte)(ModbusProtocolId >> 8);
            header[3] = (byte)(ModbusProtocolId & 0xFF);
            int length = 1 + pdu.Length; // 单元ID + PDU
            header[4] = (byte)(length >> 8);
            header[5] = (byte)(length & 0xFF);
            header[6] = slaveId;

            byte[] request = new byte[header.Length + pdu.Length];
            Buffer.BlockCopy(header, 0, request, 0, header.Length);
            Buffer.BlockCopy(pdu, 0, request, header.Length, pdu.Length);

            using var cts = new CancellationTokenSource(TimeoutMs);
            try
            {
                await stream.WriteAsync(request, 0, request.Length, cts.Token).ConfigureAwait(false);
                await stream.FlushAsync(cts.Token).ConfigureAwait(false);

                // 读取 MBAP 头 (7 字节)
                byte[] respHeader = await ReadExactAsync(stream, 7, cts.Token).ConfigureAwait(false);
                ushort respTxId = (ushort)((respHeader[0] << 8) | respHeader[1]);
                if (respTxId != txId)
                    throw new IOException($"Modbus 事务 ID 不匹配 (期望 {txId}, 实际 {respTxId})");
                ushort protoId = (ushort)((respHeader[2] << 8) | respHeader[3]);
                if (protoId != ModbusProtocolId)
                    throw new IOException($"Modbus 协议 ID 非法: {protoId}");
                int respLength = (respHeader[4] << 8) | respHeader[5];
                byte respUnitId = respHeader[6];
                if (respUnitId != slaveId)
                    throw new IOException($"Modbus 单元 ID 不匹配 (期望 {slaveId}, 实际 {respUnitId})");

                // 读取 PDU
                int pduLen = respLength - 1; // 减去单元 ID
                if (pduLen < expectedPayloadMin)
                    throw new IOException($"Modbus 响应 PDU 过短: 需要 {expectedPayloadMin}, 实际 {pduLen}");

                byte[] respPdu = await ReadExactAsync(stream, pduLen, cts.Token).ConfigureAwait(false);
                return respPdu;
            }
            catch (OperationCanceledException)
            {
                throw new IOException("Modbus TCP 读取超时。");
            }
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken token)
        {
            byte[] buffer = new byte[count];
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, totalRead, count - totalRead, token).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException("Modbus TCP 连接已断开 (远端关闭)。");
                totalRead += read;
            }
            return buffer;
        }
    }
}
