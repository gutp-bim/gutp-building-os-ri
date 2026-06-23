using System.Text;
using System.Web;
using ParityHarness.Core;

namespace ParityHarness.Report;

public static class HtmlReporter
{
    public static string Generate(IEnumerable<ParityResult> results)
    {
        var list = results.ToList();
        var summary = ParitySummary.From(list);
        var sb = new StringBuilder();

        sb.AppendLine("""<!DOCTYPE html>""");
        sb.AppendLine("""<html lang="ja">""");
        sb.AppendLine("<head>");
        sb.AppendLine("""  <meta charset="UTF-8">""");
        sb.AppendLine("""  <meta name="viewport" content="width=device-width, initial-scale=1.0">""");
        sb.AppendLine("  <title>Building OS Parity Report</title>");
        sb.AppendLine("""  <style>""");
        sb.AppendLine("""    body { font-family: monospace; margin: 20px; background: #1e1e1e; color: #d4d4d4; }""");
        sb.AppendLine("""    h1 { color: #569cd6; }""");
        sb.AppendLine("""    .summary { background: #252526; padding: 12px; border-radius: 4px; margin-bottom: 20px; }""");
        sb.AppendLine("""    .pass { color: #4ec9b0; font-weight: bold; }""");
        sb.AppendLine("""    .fail { color: #f44747; font-weight: bold; }""");
        sb.AppendLine("""    .scenario { background: #252526; margin: 8px 0; border-radius: 4px; overflow: hidden; }""");
        sb.AppendLine("""    .scenario-header { padding: 8px 12px; display: flex; justify-content: space-between; }""");
        sb.AppendLine("""    .diff-table { width: 100%; border-collapse: collapse; font-size: 0.85em; }""");
        sb.AppendLine("""    .diff-table th { background: #333; padding: 6px 10px; text-align: left; }""");
        sb.AppendLine("""    .diff-table td { padding: 5px 10px; border-top: 1px solid #3c3c3c; }""");
        sb.AppendLine("""    .path { color: #9cdcfe; }""");
        sb.AppendLine("""    .expected { color: #4ec9b0; }""");
        sb.AppendLine("""    .actual { color: #f44747; }""");
        sb.AppendLine("""    .diff-type { color: #ce9178; }""");
        sb.AppendLine("""  </style>""");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <h1>Building OS Parity Report</h1>");
        sb.AppendLine($"""  <p>Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</p>""");

        sb.AppendLine("  <div class=\"summary\">");
        sb.AppendLine($"    <span>Total: {summary.Total}</span> &nbsp;");
        sb.AppendLine($"""    <span class="pass">PASS: {summary.Passed}</span> &nbsp;""");
        sb.AppendLine($"""    <span class="fail">FAIL: {summary.Failed}</span> &nbsp;""");
        if (summary.AllPassed)
            sb.AppendLine("""    <strong class="pass">✓ All scenarios passed</strong>""");
        else
            sb.AppendLine($"""    <strong class="fail">✗ {summary.Failed} scenario(s) failed</strong>""");
        sb.AppendLine("  </div>");

        foreach (var result in list)
        {
            var statusClass = result.Passed ? "pass" : "fail";
            var statusText = result.Passed ? "PASS" : "FAIL";
            sb.AppendLine("  <div class=\"scenario\">");
            sb.AppendLine("    <div class=\"scenario-header\">");
            sb.AppendLine($"""      <span>{HttpUtility.HtmlEncode(result.ScenarioName)}</span>""");
            sb.AppendLine($"""      <span class="{statusClass}">{statusText}</span>""");
            sb.AppendLine("    </div>");

            if (!result.Passed)
            {
                sb.AppendLine("    <table class=\"diff-table\">");
                sb.AppendLine("      <tr><th>Path</th><th>Expected</th><th>Actual</th><th>Type</th></tr>");
                foreach (var diff in result.Diffs)
                {
                    sb.AppendLine("      <tr>");
                    sb.AppendLine($"""        <td class="path">{HttpUtility.HtmlEncode(diff.Path)}</td>""");
                    sb.AppendLine($"""        <td class="expected">{HttpUtility.HtmlEncode(diff.Expected ?? "—")}</td>""");
                    sb.AppendLine($"""        <td class="actual">{HttpUtility.HtmlEncode(diff.Actual ?? "—")}</td>""");
                    sb.AppendLine($"""        <td class="diff-type">{diff.Type}</td>""");
                    sb.AppendLine("      </tr>");
                }
                sb.AppendLine("    </table>");
            }

            sb.AppendLine("  </div>");
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }
}
