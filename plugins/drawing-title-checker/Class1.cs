using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace DrawingProofread
{
    [Transaction(TransactionMode.Manual)]
    public class CheckDrawingTitleCommand : IExternalCommand
    {
        // 差异字体大小的变量，这里可以随便改
        private const double DIFF_FONT_PT = 14.0;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string csvPath = Path.Combine(desktop, "MSH DCL-Drawing.csv");

                if (!File.Exists(csvPath))
                {
                    TaskDialog.Show("Drawing Check", $"找不到 CSV 文件：{csvPath}");
                    return Result.Failed;
                }

                var numberToTitle = LoadNumberTitleMap(csvPath);
                if (numberToTitle.Count == 0)
                {
                    TaskDialog.Show("Drawing Check", "CSV 中未读取到任何有效的图号和图名数据，请检查表头和内容。");
                    return Result.Failed;
                }

                var selIds = uidoc.Selection.GetElementIds();
                var selectedSheets = selIds.Select(id => doc.GetElement(id) as ViewSheet).Where(vs => vs != null).ToList();

                List<ViewSheet> sheets = selectedSheets.Count > 0
                    ? selectedSheets
                    : new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Sheets).WhereElementIsNotElementType().Cast<ViewSheet>().ToList();

                bool useSelection = selectedSheets.Count > 0;

                int okCount = 0, notFoundCount = 0, wrongCount = 0;

                // Excel输出行表头的名称
                var rows = new List<Cell[]>();
                string info = $"项目：{doc.Title}；检查时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}；CSV：{csvPath}；检测范围：{(useSelection ? $"仅选中的图纸（{sheets.Count} 张）" : $"全部图纸（{sheets.Count} 张）")}";
                rows.Add(new[] { Cell.Plain(info) });
                rows.Add(Array.Empty<Cell>());
                rows.Add(new[] {
                    Cell.Plain("Sheet"),
                    Cell.Plain("Drawing No"),
                    Cell.Plain("当前标题"),
                    Cell.Plain("正确标题"),
                    Cell.Plain("问题类型")
                });

                foreach (var sheet in sheets)
                {
                    string drawingNo = GetParamStr(sheet, "Prefix_SheetNumber");
                    string sheetNumber = sheet.SheetNumber ?? "";

                    string t1 = GetParamStr(sheet, "Sheet_Title_1");
                    string t2 = GetParamStr(sheet, "Sheet_Title_2");
                    string t3 = GetParamStr(sheet, "Sheet_Title_3");
                    string sheetName = GetSheetName(sheet);

                    string currentTitle = Normalize(string.Join(" ", new[] { t1, t2, t3, sheetName }.Where(s => !string.IsNullOrWhiteSpace(s))));

                    if (string.IsNullOrWhiteSpace(drawingNo))
                    {
                        if (!string.IsNullOrWhiteSpace(currentTitle))
                        {
                            wrongCount++;
                            rows.Add(new[] {
                                Cell.Plain(sheetNumber), Cell.Plain(""),
                                Cell.Plain(currentTitle), Cell.Plain(""),
                                Cell.Plain("缺少图号(Prefix_SheetNumber)")
                            });
                        }
                        continue;
                    }

                    string key = drawingNo.Trim();
                    if (!numberToTitle.TryGetValue(key, out string correctTitleRaw))
                    {
                        notFoundCount++;
                        rows.Add(new[] {
                            Cell.Plain(sheetNumber), Cell.Plain(key),
                            Cell.Plain(currentTitle), Cell.Plain(""),
                            Cell.Plain("CSV 无匹配")
                        });
                        continue;
                    }

                    string correctTitle = Normalize(correctTitleRaw);

                    if (string.Equals(currentTitle, correctTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        okCount++;
                    }
                    else
                    {
                        wrongCount++;
                        var (curCell, corCell) = MakeHighlightedPair(currentTitle, correctTitle); // 当前=红, 正确=绿
                        rows.Add(new[] {
                            Cell.Plain(sheetNumber), Cell.Plain(key),
                            curCell, corCell,
                            Cell.Plain("标题不一致")
                        });
                    }
                }

                string xlsxPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DrawingTitleCheckResult.xlsx");
                TryDelete(xlsxPath);
                WriteSimpleXlsxInlineRich(xlsxPath, rows);
                TryOpen(xlsxPath);

                TaskDialog.Show("Drawing Check",
                    $"检查完成（{(useSelection ? "仅检测选中图纸" : "全量检测")}）：\n" +
                    $" 正确：{okCount} 张\n" +
                    $" CSV 无匹配：{notFoundCount} 张\n" +
                    $" 标题不一致：{wrongCount} 张\n\n" +
                    $"结果已输出并打开：\n{xlsxPath}");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        // Revit 参数/工具 
        private static string GetParamStr(Element e, string paramName)
        {
            Parameter p = e.LookupParameter(paramName);
            return p != null ? (p.AsString() ?? "").Trim() : "";
        }

        private static string GetSheetName(ViewSheet sheet) //找名字
        {
            try
            {
                Parameter p = sheet.get_Parameter(BuiltInParameter.SHEET_NAME);
                if (p != null)
                {
                    string v = p.AsString();
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
            }
            catch { }
            try
            {
                string v2 = sheet.Name;
                if (!string.IsNullOrWhiteSpace(v2)) return v2.Trim();
            }
            catch { }
            return GetParamStr(sheet, "图纸名称");
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var t = s.Trim().Replace("\r", " ").Replace("\n", " ");
            while (t.Contains("  ")) t = t.Replace("  ", " ");
            return t;
        }

        // CSV 读取 
        private static Dictionary<string, string> LoadNumberTitleMap(string csvPath)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string text = File.ReadAllText(csvPath);
            if (text.Length >= 1 && text[0] == '\uFEFF') text = text.Substring(1);

            char[] candidates = new[] { ',', ';', '\t' };
            char? sepFromDirective = TryReadSepDirective(text);

            char usedSep = sepFromDirective ?? ',';
            string[][] records = null;

            if (sepFromDirective.HasValue)
            {
                records = ParseCsv(text, usedSep);
                if (!TryFindHeader(records, out int _, out int _))
                {
                    foreach (var c in candidates)
                    {
                        usedSep = c;
                        records = ParseCsv(text, usedSep);
                        if (TryFindHeader(records, out int __, out int __2)) break;
                    }
                }
            }
            else
            {
                foreach (var c in candidates)
                {
                    usedSep = c;
                    records = ParseCsv(text, usedSep);
                    if (TryFindHeader(records, out int __, out int __2)) break;
                }
            }

            if (records == null || records.Length == 0) return dict;
            if (!TryFindHeader(records, out int colNum, out int colTitle)) return dict;

            for (int i = 1; i < records.Length; i++)
            {
                var row = records[i];
                if (row == null) continue;
                if (row.Length <= Math.Max(colNum, colTitle)) continue;

                string num = (row[colNum] ?? "").Trim();
                string title = (row[colTitle] ?? "").Trim();
                if (string.IsNullOrEmpty(num) || string.IsNullOrEmpty(title)) continue;

                if (!dict.ContainsKey(num)) dict[num] = title;
            }
            return dict;
        }

        private static string[][] ParseCsv(string text, char sep)
        {
            var rows = new List<string[]>();
            var fields = new List<string>();
            var sb = new StringBuilder();

            bool inQuotes = false;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        else { inQuotes = false; i++; continue; }
                    }
                    else { sb.Append(c); i++; continue; }
                }
                else
                {
                    if (c == '"') { inQuotes = true; i++; continue; }
                    else if (c == sep) { fields.Add(sb.ToString()); sb.Clear(); i++; continue; }
                    else if (c == '\r' || c == '\n')
                    {
                        fields.Add(sb.ToString()); sb.Clear();
                        rows.Add(fields.ToArray()); fields.Clear();
                        if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i += 2; else i++;
                        continue;
                    }
                    else { sb.Append(c); i++; continue; }
                }
            }
            fields.Add(sb.ToString());
            rows.Add(fields.ToArray());

            if (rows.Count > 0 && rows[^1].Length == 1 && string.IsNullOrWhiteSpace(rows[^1][0])) rows.RemoveAt(rows.Count - 1);
            if (rows.Count > 0 && rows[0].Length == 1 && rows[0][0].StartsWith("sep=", StringComparison.OrdinalIgnoreCase)) rows.RemoveAt(0);
            return rows.ToArray();
        }

        private static char? TryReadSepDirective(string text)
        {
            int len = Math.Min(text.Length, 16);
            string head = text.Substring(0, len);
            if (head.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) && len >= 5)
            {
                char c = head[4];
                if (c == ',' || c == ';' || c == '\t') return c;
            }
            return null;
        }

        private static bool TryFindHeader(string[][] records, out int colNum, out int colTitle)
        {
            colNum = -1; colTitle = -1;
            if (records == null || records.Length == 0) return false;

            var header = records[0];
            for (int i = 0; i < header.Length; i++)
            {
                string h = NormalizeHeaderCell(header[i]);
                if (colNum < 0 && h.StartsWith("Drawing Number", StringComparison.OrdinalIgnoreCase)) colNum = i;
                if (colTitle < 0 && h.StartsWith("Drawing Title", StringComparison.OrdinalIgnoreCase)) colTitle = i;
                if (colNum >= 0 && colTitle >= 0) break;
            }
            return (colNum >= 0 && colTitle >= 0);
        }

        private static string NormalizeHeaderCell(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length > 0 && s[0] == '\uFEFF') s = s.Substring(1);
            s = s.Replace("\r\n", "\n").Replace("\r", "\n");
            int nl = s.IndexOf('\n'); if (nl >= 0) s = s.Substring(0, nl);
            int p = s.IndexOf('（'); if (p >= 0) s = s.Substring(0, p);
            p = s.IndexOf('('); if (p >= 0) s = s.Substring(0, p);
            return s.Trim();
        }

        // 有错的时候，差异高亮颜色当前=红，正确=绿
        private static (Cell cur, Cell cor) MakeHighlightedPair(string cur, string cor)
        {
            cur ??= "";
            cor ??= "";
            int n = Math.Min(cur.Length, cor.Length);

            int pre = 0;
            while (pre < n && char.ToUpperInvariant(cur[pre]) == char.ToUpperInvariant(cor[pre])) pre++;

            int suf = 0;
            while (suf < n - pre &&
                   char.ToUpperInvariant(cur[cur.Length - 1 - suf]) == char.ToUpperInvariant(cor[cor.Length - 1 - suf])) suf++;

            var curRuns = new List<Run>();
            var corRuns = new List<Run>();

            if (pre > 0)
            {
                curRuns.Add(Run.Normal(cur.Substring(0, pre)));
                corRuns.Add(Run.Normal(cor.Substring(0, pre)));
            }

            string curMid = cur.Substring(pre, Math.Max(0, cur.Length - pre - suf));
            string corMid = cor.Substring(pre, Math.Max(0, cor.Length - pre - suf));

            if (curMid.Length > 0) curRuns.Add(Run.RedBig(curMid, DIFF_FONT_PT));     // 当前差异：红
            if (corMid.Length > 0) corRuns.Add(Run.GreenBig(corMid, DIFF_FONT_PT));   // 正确差异：绿

            if (suf > 0)
            {
                curRuns.Add(Run.Normal(cur.Substring(cur.Length - suf)));
                corRuns.Add(Run.Normal(cor.Substring(cor.Length - suf)));
            }

            if (curRuns.Count == 0) curRuns.Add(Run.Normal(cur));
            if (corRuns.Count == 0) corRuns.Add(Run.Normal(cor));

            return (Cell.WithRuns(curRuns), Cell.WithRuns(corRuns));
        }

        // 输出的xlsx
        private static void WriteSimpleXlsxInlineRich(string xlsxPath, List<Cell[]> rows)
        {
            using (var za = ZipFile.Open(xlsxPath, ZipArchiveMode.Create))
            {
                AddText(za, "[Content_Types].xml",
@"<?xml version=""1.0"" encoding=""UTF-8""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
  <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>
  <Default Extension=""xml"" ContentType=""application/xml""/>
  <Override PartName=""/xl/workbook.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml""/>
  <Override PartName=""/xl/worksheets/sheet1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml""/>
</Types>");

                AddText(za, "_rels/.rels",
@"<?xml version=""1.0"" encoding=""UTF-8""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""xl/workbook.xml""/>
</Relationships>");

                AddText(za, "xl/workbook.xml",
@"<?xml version=""1.0"" encoding=""UTF-8""?>
<workbook xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"">
  <sheets><sheet name=""Result"" sheetId=""1"" r:id=""rId1""/></sheets>
</workbook>");

                AddText(za, "xl/_rels/workbook.xml.rels",
@"<?xml version=""1.0"" encoding=""UTF-8""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"" Target=""worksheets/sheet1.xml""/>
</Relationships>");

                var sb = new StringBuilder();
                sb.Append(@"<?xml version=""1.0"" encoding=""UTF-8""?>");
                sb.Append(@"<worksheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">");
                sb.Append("<sheetData>");

                int r = 1;
                foreach (var row in rows)
                {
                    if (row == null) continue;
                    if (row.Length == 0) { sb.Append($@"<row r=""{r}""/>"); r++; continue; }

                    sb.Append($@"<row r=""{r}"">");
                    for (int c = 0; c < row.Length; c++)
                    {
                        var cell = row[c] ?? Cell.Plain("");
                        string a1 = ToA1(c, r);
                        sb.Append($@"<c r=""{a1}"" t=""inlineStr""><is>");

                        if (cell.RichRuns == null || cell.RichRuns.Count == 0)
                        {
                            sb.Append($@"<t xml:space=""preserve"">{XmlEscape(cell.PlainText ?? "")}</t>");
                        }
                        else
                        {
                            foreach (var run in cell.RichRuns)
                            {
                                sb.Append("<r>");
                                if (run.Bold || run.SizePt.HasValue || !string.IsNullOrEmpty(run.ColorRgb))
                                {
                                    sb.Append("<rPr>");
                                    if (run.Bold) sb.Append("<b/>");
                                    if (run.SizePt.HasValue)
                                        sb.Append($@"<sz val=""{run.SizePt.Value.ToString(CultureInfo.InvariantCulture)}""/>");
                                    if (!string.IsNullOrEmpty(run.ColorRgb))
                                        sb.Append($@"<color rgb=""{run.ColorRgb}""/>");
                                    sb.Append("</rPr>");
                                }
                                sb.Append($@"<t xml:space=""preserve"">{XmlEscape(run.Text ?? "")}</t>");
                                sb.Append("</r>");
                            }
                        }

                        sb.Append("</is></c>");
                    }
                    sb.Append("</row>");
                    r++;
                }

                sb.Append("</sheetData></worksheet>");
                AddText(za, "xl/worksheets/sheet1.xml", sb.ToString());
            }
        }

        private static string ToA1(int colIndex, int row)
        {
            int x = colIndex + 1;
            var s = new StringBuilder();
            while (x > 0) { x--; s.Insert(0, (char)('A' + (x % 26))); x /= 26; }
            return s + row.ToString();
        }
        private static string XmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
        private static void AddText(ZipArchive za, string entryName, string content)
        {
            var e = za.CreateEntry(entryName);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) { w.Write(content); }
        }
        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        private static void TryOpen(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { } }

        private class Cell
        {
            public string PlainText;
            public List<Run> RichRuns;

            public static Cell Plain(string s) => new Cell { PlainText = s };
            public static Cell WithRuns(List<Run> runs) => new Cell { RichRuns = runs ?? new List<Run>() };
        }
        private class Run
        {
            public string Text;
            public string ColorRgb;      
            public double? SizePt;       
            public bool Bold;   // 变粗一点

            public static Run Normal(string t) => new Run { Text = t };
            public static Run RedBig(string t, double pt) => new Run { Text = t, ColorRgb = "FFFF0000", Bold = true, SizePt = pt }; // 红
            public static Run GreenBig(string t, double pt) => new Run { Text = t, ColorRgb = "FF00B050", Bold = true, SizePt = pt }; // 绿
        }
    }
}