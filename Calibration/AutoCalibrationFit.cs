using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ScanningPowerCylinder.Calibration
{
    /// <summary>
    /// 自动校准三次多项式拟合，移植自 auto_calibration_fit.cpp。
    /// 按 法桶/法桶档位 分组，对“实测平均(uA) → 设定输入(uA)”拟合三次多项式，
    /// 得到以 nA 为单位的三次系数 [c3, c2, c1, c0]。
    /// </summary>
    public static class AutoCalibrationFit
    {
        public sealed class RangeFitResult
        {
            public int CupIndex { get; set; } = -1;
            public int RangeIndex { get; set; } = -1;
            public string RangeName { get; set; } = string.Empty;
            public double[] CoeffNA { get; set; } = Array.Empty<double>();
            public int PointCount { get; set; }
            public double MeanAbsBeforeUA { get; set; }
            public double MeanAbsAfterUA { get; set; }
        }

        private struct CsvRow
        {
            public int CupIndex;
            public int RangeIndex;
            public string RangeName;
            public double TargetUA;
            public double MeasuredAvgUA;
        }

        private static readonly char[] CsvSplitChars = { ',' };

        private static string CleanupCell(string text)
            => text.Replace("\uFEFF", string.Empty).Trim();

        private static string NormalizeRangeName(string text)
        {
            string lower = CleanupCell(text).ToLowerInvariant();
            if (lower.Contains("4.7")) return "4.7K";
            if (lower.Contains("20")) return "20K";
            if (lower.Contains("47")) return "47K";
            if (lower.Contains("499") || lower.Contains("500")) return "499K";
            return string.Empty;
        }

        public static int RangeIndexFromText(string text)
        {
            return NormalizeRangeName(text) switch
            {
                "4.7K" => 0,
                "20K" => 1,
                "47K" => 2,
                "499K" => 3,
                _ => -1
            };
        }

        private static int FindHeaderIndex(IList<string> headers, params string[] candidates)
        {
            for (int i = 0; i < headers.Count; i++)
            {
                string header = CleanupCell(headers[i]).ToLowerInvariant();
                for (int c = 0; c < candidates.Length; c++)
                {
                    if (header == candidates[c].ToLowerInvariant())
                        return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// 高斯消元法求解线性方程组 M·x = rhs (列主元消去)。
        /// </summary>
        public static double[]? SolveLinearSystem(double[][] matrix, double[] rhs, out bool ok)
        {
            ok = false;
            int size = rhs.Length;
            if (matrix.Length != size)
                return null;

            // 复制以避免修改入参
            double[][] m = new double[size][];
            for (int i = 0; i < size; i++)
            {
                m[i] = (double[])matrix[i].Clone();
            }
            double[] b = (double[])rhs.Clone();

            for (int pivot = 0; pivot < size; pivot++)
            {
                int pivotRow = pivot;
                double bestAbs = Math.Abs(m[pivot][pivot]);
                for (int row = pivot + 1; row < size; row++)
                {
                    double candidate = Math.Abs(m[row][pivot]);
                    if (candidate > bestAbs)
                    {
                        bestAbs = candidate;
                        pivotRow = row;
                    }
                }

                if (bestAbs < 1e-18)
                    return null;

                if (pivotRow != pivot)
                {
                    (m[pivot], m[pivotRow]) = (m[pivotRow], m[pivot]);
                    (b[pivot], b[pivotRow]) = (b[pivotRow], b[pivot]);
                }

                double pivotValue = m[pivot][pivot];
                for (int col = pivot; col < size; col++)
                    m[pivot][col] /= pivotValue;
                b[pivot] /= pivotValue;

                for (int row = 0; row < size; row++)
                {
                    if (row == pivot) continue;
                    double factor = m[row][pivot];
                    if (factor == 0.0) continue;
                    for (int col = pivot; col < size; col++)
                        m[row][col] -= factor * m[pivot][col];
                    b[row] -= factor * b[pivot];
                }
            }

            ok = true;
            return b;
        }

        /// <summary>
        /// 对 (xValues, yValues) 拟合三次多项式，返回 [c3, c2, c1, c0] (以 UA 为单位)。
        /// </summary>
        public static double[]? FitCubicUA(IList<double> xValues, IList<double> yValues, out bool ok)
        {
            ok = false;
            if (xValues.Count != yValues.Count || xValues.Count < 4)
                return null;

            double[] sums = new double[7];
            double[] rhs = new double[4];
            for (int i = 0; i < xValues.Count; i++)
            {
                double x = xValues[i];
                double y = yValues[i];
                double[] powers = new double[7];
                powers[0] = 1.0;
                for (int p = 1; p < 7; p++)
                    powers[p] = powers[p - 1] * x;
                for (int p = 0; p < 7; p++)
                    sums[p] += powers[p];
                for (int degree = 0; degree < 4; degree++)
                    rhs[degree] += y * powers[degree];
            }

            double[][] matrix = new double[4][];
            for (int row = 0; row < 4; row++)
            {
                matrix[row] = new double[4];
                for (int col = 0; col < 4; col++)
                    matrix[row][col] = sums[row + col];
            }

            double[]? solution = SolveLinearSystem(matrix, rhs, out bool solveOk);
            if (!solveOk || solution is null || solution.Length != 4)
                return null;

            ok = true;
            // [c3, c2, c1, c0]
            return new[] { solution[3], solution[2], solution[1], solution[0] };
        }

        private static double EvalCubic(double[] coeffs, double x)
        {
            if (coeffs.Length != 4) return x;
            return ((coeffs[0] * x + coeffs[1]) * x + coeffs[2]) * x + coeffs[3];
        }

        /// <summary>
        /// 将以 uA 为单位的系数转换为以 nA 为单位的系数。
        /// </summary>
        public static double[] CoeffUAtoCoeffNA(double[] coeffUA)
        {
            return new[]
            {
                coeffUA.Length > 0 ? coeffUA[0] / 1000000.0 : 0.0,
                coeffUA.Length > 1 ? coeffUA[1] / 1000.0 : 0.0,
                coeffUA.Length > 2 ? coeffUA[2] : 0.0,
                coeffUA.Length > 3 ? coeffUA[3] * 1000.0 : 0.0,
            };
        }

        private static string GroupKey(int cupIndex, int rangeIndex) => $"{cupIndex}:{rangeIndex}";

        /// <summary>
        /// 从自动校准 CSV 拟合系数。移植自 fitFromAutoCalibrationCsv。
        /// </summary>
        public static bool FitFromAutoCalibrationCsv(string fileName, out List<RangeFitResult> results, out string? errorMessage)
        {
            results = new List<RangeFitResult>();
            errorMessage = null;

            string text;
            try
            {
                text = File.ReadAllText(fileName, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                errorMessage = $"无法打开自动校准 CSV: {ex.Message}";
                return false;
            }

            // 按 LF/CR 拆行 (保留空行以兼容 Qt 的 SkipEmptyParts)
            string[] allLines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            if (allLines.Length == 0)
            {
                errorMessage = "自动校准 CSV 为空。";
                return false;
            }

            int lineIdx = 0;
            // 找到首个非空行作为表头
            while (lineIdx < allLines.Length && string.IsNullOrWhiteSpace(allLines[lineIdx]))
                lineIdx++;

            if (lineIdx >= allLines.Length)
            {
                errorMessage = "自动校准 CSV 为空。";
                return false;
            }

            string headerLine = allLines[lineIdx];
            lineIdx++;

            string[] headers = headerLine.Split(CsvSplitChars);
            int cupIndexCol = FindHeaderIndex(headers, "法桶", "channel");
            int rangeCol = FindHeaderIndex(headers, "法桶档位", "range");
            int targetCol = FindHeaderIndex(headers, "设定输入(uA)", "target(uA)", "target");
            int measuredCol = FindHeaderIndex(headers, "实测平均(uA)", "meanmeasured(uA)", "meanmeasured");

            // 兼容当前 Qt 自动校准导出 CSV 的固定列顺序兜底
            if ((cupIndexCol < 0 || rangeCol < 0 || targetCol < 0 || measuredCol < 0) && headers.Length >= 13)
            {
                cupIndexCol = 1;
                rangeCol = 2;
                targetCol = 3;
                measuredCol = 5;
            }

            if (cupIndexCol < 0 || rangeCol < 0 || targetCol < 0 || measuredCol < 0)
            {
                errorMessage = "自动校准 CSV 缺少必要列，至少需要 法桶/法桶档位/设定输入(uA)/实测平均(uA)，或满足当前自动校准导出的固定列顺序。";
                return false;
            }

            var grouped = new SortedDictionary<string, List<CsvRow>>();
            int minRequired = Math.Max(Math.Max(cupIndexCol, rangeCol), Math.Max(targetCol, measuredCol));
            int validRowCount = 0;

            var digitRe = new Regex(@"\d+", RegexOptions.Compiled);

            for (; lineIdx < allLines.Length; lineIdx++)
            {
                string rawLine = allLines[lineIdx];
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                string[] columns = rawLine.Split(CsvSplitChars);
                if (columns.Length <= minRequired) continue;

                // 解析法桶号 (1..6)
                int cupIndex = -1;
                Match cupMatch = digitRe.Match(CleanupCell(columns[cupIndexCol]));
                if (cupMatch.Success && int.TryParse(cupMatch.Value, out int cupNumber) && cupNumber >= 1 && cupNumber <= 6)
                    cupIndex = cupNumber - 1;

                if (cupIndex < 0) continue;

                string rangeName = NormalizeRangeName(columns[rangeCol]);
                int rangeIndex = RangeIndexFromText(rangeName);
                if (rangeIndex < 0) continue;

                if (!double.TryParse(CleanupCell(columns[targetCol]), NumberStyles.Float, CultureInfo.InvariantCulture, out double targetUA))
                    continue;
                if (!double.TryParse(CleanupCell(columns[measuredCol]), NumberStyles.Float, CultureInfo.InvariantCulture, out double measuredAvgUA))
                    continue;

                var row = new CsvRow
                {
                    CupIndex = cupIndex,
                    RangeIndex = rangeIndex,
                    RangeName = rangeName,
                    TargetUA = targetUA,
                    MeasuredAvgUA = measuredAvgUA
                };
                string key = GroupKey(cupIndex, rangeIndex);
                if (!grouped.TryGetValue(key, out var list))
                {
                    list = new List<CsvRow>();
                    grouped[key] = list;
                }
                list.Add(row);
                validRowCount++;
            }

            if (validRowCount == 0)
            {
                errorMessage = "自动校准 CSV 中没有可用于拟合的有效数据行。";
                return false;
            }

            var fitted = new List<RangeFitResult>();
            foreach (var pair in grouped)
            {
                var rows = pair.Value;
                if (rows.Count < 4)
                {
                    errorMessage = $"{rows[0].RangeName} 仅有 {rows.Count} 个点，至少需要 4 个点才能拟合三次多项式。";
                    return false;
                }

                var measuredPoints = rows.Select(r => r.MeasuredAvgUA).ToList();
                var targetPoints = rows.Select(r => r.TargetUA).ToList();

                double[]? coeffUA = FitCubicUA(measuredPoints, targetPoints, out bool fitOk);
                if (!fitOk || coeffUA is null)
                {
                    errorMessage = $"拟合 {rows[0].RangeName} 失败，矩阵不可解。";
                    return false;
                }

                double sumAbsBefore = 0.0;
                double sumAbsAfter = 0.0;
                for (int i = 0; i < rows.Count; i++)
                {
                    double correctedUA = EvalCubic(coeffUA, rows[i].MeasuredAvgUA);
                    sumAbsBefore += Math.Abs(rows[i].MeasuredAvgUA - rows[i].TargetUA);
                    sumAbsAfter += Math.Abs(correctedUA - rows[i].TargetUA);
                }

                fitted.Add(new RangeFitResult
                {
                    CupIndex = rows[0].CupIndex,
                    RangeIndex = rows[0].RangeIndex,
                    RangeName = rows[0].RangeName,
                    CoeffNA = CoeffUAtoCoeffNA(coeffUA),
                    PointCount = rows.Count,
                    MeanAbsBeforeUA = sumAbsBefore / rows.Count,
                    MeanAbsAfterUA = sumAbsAfter / rows.Count
                });
            }

            fitted.Sort((lhs, rhs) =>
            {
                if (lhs.CupIndex != rhs.CupIndex) return lhs.CupIndex.CompareTo(rhs.CupIndex);
                return lhs.RangeIndex.CompareTo(rhs.RangeIndex);
            });

            results = fitted;
            return true;
        }

        /// <summary>
        /// 构建拟合摘要文本。
        /// </summary>
        public static string BuildSummaryText(IList<RangeFitResult> results)
        {
            var lines = new List<string>();
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                lines.Add($"法桶 #{r.CupIndex + 1} {r.RangeName}: 平均绝对误差 {r.MeanAbsBeforeUA.ToString("F4", CultureInfo.InvariantCulture)} uA -> {r.MeanAbsAfterUA.ToString("F4", CultureInfo.InvariantCulture)} uA, 点数 {r.PointCount}");
            }
            return string.Join("\n", lines);
        }
    }
}
