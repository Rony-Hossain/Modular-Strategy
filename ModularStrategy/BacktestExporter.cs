#region Using declarations
using System;
using System.IO;
using System.Text;
using NinjaTrader.Cbi;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// BACKTEST EXPORTER
    ///
    /// Writes a JSON report to a well-known path every time a backtest completes
    /// (State == Terminated). The quant_orchestrator pipeline watches this file
    /// for changes and reads it as the "new backtest report".
    ///
    /// Output path: Documents\NinjaTrader 8\exports\backtest_report.json
    ///
    /// Usage: call ExportBacktestReport() from HostStrategy.OnStateChange()
    /// when State == Terminated.
    /// </summary>
    public static class BacktestExporter
    {
        private static readonly string ExportDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "NinjaTrader 8", "exports");

        private static readonly string ExportPath = Path.Combine(ExportDir, "backtest_report.json");

        /// <summary>
        /// Exports backtest performance metrics as JSON.
        /// Call from OnStateChange when State == Terminated.
        /// </summary>
        public static void ExportBacktestReport(Strategy strategy)
        {
            try
            {
                var perf = strategy.SystemPerformance;
                if (perf == null || perf.AllTrades == null) return;

                var allTrades = perf.AllTrades;
                var summary   = allTrades.TradesPerformance;

                int totalTrades = allTrades.Count;
                if (totalTrades == 0) return;

                int wins = 0;
                double totalPnl = 0;
                double maxDrawdown = 0;
                double peak = 0;
                double equity = 0;

                // Session P&L buckets (keyed by hour ranges as proxy)
                double londonPnl  = 0; // 02:00–07:59 CT
                double nyOpenPnl  = 0; // 08:00–10:59 CT
                double nyMiddayPnl = 0; // 11:00–15:59 CT

                for (int i = 0; i < totalTrades; i++)
                {
                    var trade = allTrades[i];
                    double pnl = trade.ProfitCurrency;
                    totalPnl += pnl;
                    equity += pnl;

                    if (pnl > 0) wins++;

                    if (equity > peak) peak = equity;
                    double dd = peak - equity;
                    if (dd > maxDrawdown) maxDrawdown = dd;

                    // Approximate session from entry time (Central Time)
                    int hour = trade.Entry.Time.Hour;
                    if (hour >= 2 && hour < 8)
                        londonPnl += pnl;
                    else if (hour >= 8 && hour < 11)
                        nyOpenPnl += pnl;
                    else if (hour >= 11 && hour < 16)
                        nyMiddayPnl += pnl;
                }

                double winRate = totalTrades > 0 ? (wins * 100.0 / totalTrades) : 0;
                double profitFactor = summary.ProfitFactor;
                double sharpe = ComputeSharpe(allTrades);

                string strategyId = strategy.Name ?? "UNKNOWN";

                // Build JSON manually — no Newtonsoft dependency in NinjaScript
                var sb = new StringBuilder(512);
                sb.Append("{\n");
                sb.AppendFormat("  \"strategy_id\": \"{0}\",\n", EscapeJson(strategyId));
                sb.AppendFormat("  \"net_profit\": {0:F2},\n", totalPnl);
                sb.AppendFormat("  \"profit_factor\": {0:F4},\n", profitFactor);
                sb.AppendFormat("  \"sharpe_ratio\": {0:F4},\n", sharpe);
                sb.AppendFormat("  \"max_drawdown\": {0:F2},\n", maxDrawdown);
                sb.AppendFormat("  \"win_rate\": {0:F2},\n", winRate);
                sb.AppendFormat("  \"total_trades\": {0},\n", totalTrades);
                sb.Append("  \"session_perf_map\": {\n");
                sb.AppendFormat("    \"LONDON\": {0:F2},\n", londonPnl);
                sb.AppendFormat("    \"NY_OPEN\": {0:F2},\n", nyOpenPnl);
                sb.AppendFormat("    \"NY_MIDDAY\": {0:F2}\n", nyMiddayPnl);
                sb.Append("  },\n");
                sb.AppendFormat("  \"timestamp\": \"{0:O}\"\n", DateTime.UtcNow);
                sb.Append("}");

                Directory.CreateDirectory(ExportDir);
                File.WriteAllText(ExportPath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Silently fail — never break the strategy for an export error
            }
        }

        private static double ComputeSharpe(TradeCollection trades)
        {
            if (trades.Count < 2) return 0;

            double sum = 0;
            double sumSq = 0;
            int n = trades.Count;

            for (int i = 0; i < n; i++)
            {
                double r = trades[i].ProfitCurrency;
                sum += r;
                sumSq += r * r;
            }

            double mean = sum / n;
            double variance = (sumSq / n) - (mean * mean);
            if (variance <= 0) return 0;

            double stdDev = Math.Sqrt(variance);
            return mean / stdDev;
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
