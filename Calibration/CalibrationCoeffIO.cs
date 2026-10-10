using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ScanningPowerCylinder.Calibration
{
    /// <summary>
    /// 校准系数 CSV / Modbus 寄存器 编解码，移植自 calibration_coeff_io.cpp。
    /// 校准系数表: 6 个法桶 × 4 个量程 × 4 个系数 = 24 条记录 / 96 个系数 / 384 个寄存器。
    /// 寄存器基址 0x0400，每个 double 占 4 个 16 位寄存器 (大端字序)。
    /// </summary>
    public static class CalibrationCoeffIo
    {
        public const int BaseAddress = 0x0400;
        public const int TotalRegisters = 0x0180;       // 384
        public const int CoeffCount = 6 * 4 * 4;        // 96
        public const int WriteChunkWords = 96;          // 每块 96 个寄存器

        public static string CalibrationRangeNameByIndex(int rangeIndex)
        {
            return rangeIndex switch
            {
                0 => "4.7K",
                1 => "20K",
                2 => "47K",
                3 => "499K",
                _ => "Unknown"
            };
        }

        public static int CalibrationRangeIndexFromText(string rangeText)
        {
            string normalized = rangeText.Trim().ToLowerInvariant();
            if (normalized.Contains("4.7")) return 0;
            if (normalized.Contains("20")) return 1;
            if (normalized.Contains("47")) return 2;
            if (normalized.Contains("499") || normalized.Contains("500")) return 3;
            return -1;
        }

        /// <summary>
        /// 将 double 编码为 4 个 16 位寄存器 (大端字序，高字在前)。
        /// 与 Qt encodeDoubleToRegisters 行为一致。
        /// </summary>
        public static ushort[] EncodeDoubleToRegisters(double value)
        {
            // 将 double 的 8 字节按大端展开
            ulong raw = BitConverter.DoubleToUInt64Bits(value);
            return new ushort[4]
            {
                (ushort)((raw >> 48) & 0xFFFFUL),
                (ushort)((raw >> 32) & 0xFFFFUL),
                (ushort)((raw >> 16) & 0xFFFFUL),
                (ushort)(raw & 0xFFFFUL)
            };
        }

        /// <summary>
        /// 从 4 个 16 位寄存器解码 double (大端字序，高字在前)。
        /// </summary>
        public static double DecodeDoubleFromRegisters(IList<ushort> regs, int offset, out bool ok)
        {
            ok = false;
            if (offset < 0 || offset + 3 >= regs.Count)
            {
                // 与 Qt 保持一致: 边界检查 (offset+3) >= regs.size() 返回 0
                if (offset < 0 || offset + 3 > regs.Count - 1)
                    return 0.0;
            }

            ulong raw =
                ((ulong)regs[offset] << 48) |
                ((ulong)regs[offset + 1] << 32) |
                ((ulong)regs[offset + 2] << 16) |
                (ulong)regs[offset + 3];

            ok = true;
            return BitConverter.UInt64BitsToDouble(raw);
        }

        // 重载：无 ok 输出
        public static double DecodeDoubleFromRegisters(IList<ushort> regs, int offset)
            => DecodeDoubleFromRegisters(regs, offset, out _);

        /// <summary>
        /// 将 96 个系数编码为 384 个 Modbus 寄存器。
        /// </summary>
        public static ushort[] CoefficientsToRegisters(IList<double> coefficients)
        {
            if (coefficients.Count != CoeffCount)
                throw new ArgumentException($"系数数量应为 {CoeffCount}，实际 {coefficients.Count}", nameof(coefficients));

            ushort[] regs = new ushort[TotalRegisters];
            for (int i = 0; i < CoeffCount; i++)
            {
                ushort[] chunk = EncodeDoubleToRegisters(coefficients[i]);
                Buffer.BlockCopy(chunk, 0, regs, i * 8, 8);
            }
            return regs;
        }

        /// <summary>
        /// 从 384 个 Modbus 寄存器解码出 96 个系数。
        /// </summary>
        public static double[] RegistersToCoefficients(IList<ushort> regs)
        {
            double[] coeff = new double[CoeffCount];
            for (int i = 0; i < CoeffCount; i++)
            {
                coeff[i] = DecodeDoubleFromRegisters(regs, i * 4);
            }
            return coeff;
        }

        private static string CleanupCellText(string text)
            => text.Replace("\uFEFF", string.Empty).Trim();

        /// <summary>
        /// 解析校准系数 CSV 文本，要求完整包含 6 法桶 × 4 量程 = 24 条记录。
        /// 每行: 通道号, 量程名, 量程模式, 系数1, 系数2, 系数3, 系数4
        /// </summary>
        public static bool ParseCalibrationCsvText(string text, out double[] coefficients, out string? errorMessage)
        {
            double[] parsed = new double[CoeffCount];
            bool[] seen = new bool[24];
            string[] lines = Regex.Split(text, @"[\r\n]+");
            int rowCount = 0;
            errorMessage = null;

            foreach (string rawLine in lines)
            {
                string line = CleanupCellText(rawLine);
                if (line.Length == 0) continue;
                if (line.StartsWith("Channel", StringComparison.OrdinalIgnoreCase)) continue;

                string[] columns = line.Split(',');
                if (columns.Length < 7)
                {
                    errorMessage = "CSV 列数不足，至少需要 7 列。";
                    coefficients = parsed;
                    return false;
                }

                string col0 = CleanupCellText(columns[0]);
                if (!int.TryParse(col0, out int channel) || channel < 1 || channel > 6)
                {
                    errorMessage = $"CSV 中存在非法通道号: {columns[0]}";
                    coefficients = parsed;
                    return false;
                }

                int rangeIndex;
                string col2 = CleanupCellText(columns[2]);
                if (int.TryParse(col2, out int rangeMode) && rangeMode >= 0 && rangeMode < 4)
                {
                    rangeIndex = rangeMode;
                }
                else
                {
                    rangeIndex = CalibrationRangeIndexFromText(columns[1]);
                }

                if (rangeIndex < 0 || rangeIndex >= 4)
                {
                    errorMessage = $"CSV 中存在非法量程: {columns[1]}";
                    coefficients = parsed;
                    return false;
                }

                int blockIndex = (channel - 1) * 4 + rangeIndex;
                if (seen[blockIndex])
                {
                    errorMessage = $"CSV 中存在重复的通道/量程记录: 通道 {channel}, 量程 {CalibrationRangeNameByIndex(rangeIndex)}";
                    coefficients = parsed;
                    return false;
                }

                for (int i = 0; i < 4; i++)
                {
                    if (!double.TryParse(CleanupCellText(columns[3 + i]), NumberStyles.Float, CultureInfo.InvariantCulture, out double coeff))
                    {
                        errorMessage = $"CSV 系数解析失败: 通道 {channel}, 量程 {CalibrationRangeNameByIndex(rangeIndex)}, 第 {i + 1} 个系数";
                        coefficients = parsed;
                        return false;
                    }
                    parsed[blockIndex * 4 + i] = coeff;
                }

                seen[blockIndex] = true;
                rowCount++;
            }

            bool allRowsPresent = true;
            for (int i = 0; i < seen.Length; i++)
            {
                if (!seen[i]) { allRowsPresent = false; break; }
            }

            if (rowCount != 24 || !allRowsPresent)
            {
                errorMessage = "CSV 需要完整包含 6 个法桶 x 4 个量程，共 24 条记录。";
                coefficients = parsed;
                return false;
            }

            coefficients = parsed;
            return true;
        }

        /// <summary>
        /// 解析 C 风格数组文本 (如 "const double coeff[] = { 1.0, 2.0, ... };")。
        /// </summary>
        public static bool ParseCalibrationArrayText(string text, out double[] coefficients, out string? errorMessage)
        {
            coefficients = Array.Empty<double>();
            errorMessage = null;

            // 移除行注释
            string sanitized = Regex.Replace(text, @"//[^\r\n]*", string.Empty);

            int assignPos = sanitized.IndexOf('=');
            int beginPos = sanitized.IndexOf('{', assignPos >= 0 ? assignPos : 0);
            int endPos = sanitized.LastIndexOf('}');
            if (beginPos < 0 || endPos <= beginPos)
            {
                errorMessage = "未找到有效的 C 数组内容。";
                return false;
            }

            string body = sanitized.Substring(beginPos, endPos - beginPos + 1);
            var numberRe = new Regex(@"[-+]?(?:(?:\d+\.\d*)|(?:\.\d+)|(?:\d+))(?:[eE][-+]?\d+)?");
            MatchCollection matches = numberRe.Matches(body);

            var parsed = new List<double>(CoeffCount);
            foreach (Match m in matches)
            {
                if (!double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    errorMessage = $"数组中的数值解析失败: {m.Value}";
                    return false;
                }
                parsed.Add(value);
            }

            if (parsed.Count != CoeffCount)
            {
                errorMessage = $"数组中共解析到 {parsed.Count} 个系数，期望为 {CoeffCount} 个。";
                return false;
            }

            coefficients = parsed.ToArray();
            return true;
        }

        /// <summary>
        /// 将 96 个系数导出为 CSV 文本。
        /// </summary>
        public static string ExportCoefficientsToCsv(IList<double> coefficients)
        {
            if (coefficients.Count != CoeffCount)
                throw new ArgumentException($"系数数量应为 {CoeffCount}，实际 {coefficients.Count}", nameof(coefficients));

            var sb = new StringBuilder();
            sb.AppendLine("Channel,Range,RangeMode,Coeff1,Coeff2,Coeff3,Coeff4");
            for (int channel = 1; channel <= 6; channel++)
            {
                for (int rangeIndex = 0; rangeIndex < 4; rangeIndex++)
                {
                    int blockIndex = (channel - 1) * 4 + rangeIndex;
                    string rangeName = CalibrationRangeNameByIndex(rangeIndex);
                    double c1 = coefficients[blockIndex * 4 + 0];
                    double c2 = coefficients[blockIndex * 4 + 1];
                    double c3 = coefficients[blockIndex * 4 + 2];
                    double c4 = coefficients[blockIndex * 4 + 3];
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3:R},{4:R},{5:R},{6:R}",
                        channel, rangeName, rangeIndex, c1, c2, c3, c4));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 从文件读取校准系数 CSV。
        /// </summary>
        public static bool LoadFromFile(string fileName, out double[] coefficients, out string? errorMessage)
        {
            try
            {
                string text = File.ReadAllText(fileName, Encoding.UTF8);
                return ParseCalibrationCsvText(text, out coefficients, out errorMessage);
            }
            catch (Exception ex)
            {
                coefficients = new double[CoeffCount];
                errorMessage = $"读取文件失败: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 将校准系数写入 CSV 文件。
        /// </summary>
        public static void ExportToFile(string fileName, IList<double> coefficients)
        {
            string csv = ExportCoefficientsToCsv(coefficients);
            File.WriteAllText(fileName, csv, new UTF8Encoding(false));
        }
    }
}
