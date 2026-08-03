using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Runtime.InteropServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevisionProofread
{
    [Transaction(TransactionMode.Manual)]
    public class CheckRevisionCommand : IExternalCommand
    {
        // 路径
        private static readonly string EXCEL_PATH_1 = @"C:\LK PowerBI Submittal item summary_Part_01_MEP.xlsx";
        private static readonly string EXCEL_PATH_2 = @"C:\LK PowerBI Submittal item summary_Part_02_Others.xlsx";
        private const string TARGET_SHEET_NAME = "Formatted data";
        private const int HEADER_SCAN_ROWS = 50;

        private static readonly string[] STAGE_LADDER = new[]
        {
            "ISSUE FOR DESIGN REVIEW (30%)",
            "ISSUE FOR DESIGN REVIEW (60%)",
            "ISSUE FOR DESIGN PHASE IFC REVIEW",
            "ISSUE FOR CONSTRUCTION"
        };

        private const double REV_FONT_PT = 14.0; // 变大
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Autodesk.Revit.DB.Document doc = uidoc.Document;

            try
            {
                // 读取两个 Excel（先 OpenXml，读不到再 Excel COM）
                var allRows = new List<ExcelRow>();
                if (File.Exists(EXCEL_PATH_1)) allRows.AddRange(ReadFormattedData_OpenXml(EXCEL_PATH_1));
                if (File.Exists(EXCEL_PATH_2)) allRows.AddRange(ReadFormattedData_OpenXml(EXCEL_PATH_2));

                if (allRows.Count == 0)
                {
                    if (File.Exists(EXCEL_PATH_1)) allRows.AddRange(ReadFormattedData_ExcelCom(EXCEL_PATH_1));
                    if (File.Exists(EXCEL_PATH_2)) allRows.AddRange(ReadFormattedData_ExcelCom(EXCEL_PATH_2));
                }

                if (allRows.Count == 0)
                {
                    TaskDialog.Show("Revision Check", "未能从两个 Excel 中读取到任何数据，请检查文件路径与工作表名。");
                    return Result.Failed;
                }

                // 仅所选，否则全量
                var selIds = uidoc.Selection.GetElementIds();
                var picked = selIds.Select(id => doc.GetElement(id) as ViewSheet).Where(x => x != null).ToList();
                List<ViewSheet> sheets = picked.Count > 0
                    ? picked
                    : new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Sheets)
                        .WhereElementIsNotElementType().Cast<ViewSheet>().ToList();
                bool useSelection = picked.Count > 0;

                // Excel 输出准备
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string xlsxPath = Path.Combine(desktop, "RevisionCheckResult.xlsx");
                TryDelete(xlsxPath);

                var rows = new List<Cell[]>();
                rows.Add(new[] { Cell.Plain($"项目：{doc.Title}") });
                rows.Add(new[] { Cell.Plain($"检查时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}") });
                rows.Add(new[] { Cell.Plain($"Excel：{EXCEL_PATH_1}; {EXCEL_PATH_2}") });
                rows.Add(new[] { Cell.Plain($"策略：只列出会议提到的问题项") });
                rows.Add(new[] { Cell.Plain(useSelection ? $"检测范围：仅选中的图纸（{sheets.Count} 张）" : $"检测范围：全部图纸（{sheets.Count} 张）") });
                rows.Add(Array.Empty<Cell>());
                rows.Add(new[]
                {
                    Cell.Plain("Sheet"),
                    Cell.Plain("Drawing No"),
                    Cell.Plain("当前 REV"),
                    Cell.Plain("推荐 REV"),
                    Cell.Plain("推荐阶段"),
                    Cell.Plain("上一次 REV（字母/数值）"),
                    Cell.Plain("上一次 Final response（来源表）"),
                    Cell.Plain("备注")
                });

                int problemCount = 0;

                foreach (var sheet in sheets)
                {
                    string sheetNo = sheet.SheetNumber ?? "";
                    string prefix = GetParamStr(sheet, "Prefix_SheetNumber").Trim();
                    string revInUI = (GetParamStr(sheet, "Revision_in_Sheet") ?? "").Trim().ToUpperInvariant();

                    if (string.IsNullOrWhiteSpace(prefix))
                    {
                        problemCount++;
                        rows.Add(new[]
                        {
                            Cell.Plain(sheetNo),
                            Cell.Plain(""),
                            Cell.Plain(string.IsNullOrEmpty(revInUI) ? "(空)" : revInUI),
                            Cell.Plain(""),
                            Cell.Plain(""),
                            Cell.Plain(""),
                            Cell.Plain(""),
                            Cell.Plain("缺少图号 Prefix_SheetNumber")
                        });
                        continue;
                    }

                    // Excel 匹配
                    var group = allRows.Where(r => KeyHit(r.Title, prefix)).ToList();
                    if (group.Count == 0)
                    {
                        problemCount++;
                        rows.Add(new[]
                        {
                            Cell.Plain(sheetNo),
                            Cell.Plain(prefix),
                            Cell.Plain(string.IsNullOrEmpty(revInUI) ? "(空)" : revInUI),
                            Cell.Plain(""),
                            Cell.Plain(""),
                            Cell.Plain(""),
                            Cell.Plain(""),
                            Cell.Plain("Excel 无匹配")
                        });
                        continue;
                    }

                    // 上一次送审
                    int maxRevNum = group.Max(r => r.RevisionNum);
                    var latest = group.Where(r => r.RevisionNum == maxRevNum).Last();

                    string lastRevLetter = ToLetter(maxRevNum);
                    string finalResp = latest.FinalResponse ?? "";

                    // 当前阶段（基于 Approved 次数）
                    int approvals = group.Count(r => ContainsApproved(r.FinalResponse));
                    int currentStageIdx = Math.Min(approvals, STAGE_LADDER.Length - 1);

                    // 推荐部分
                    string recommendRev;
                    int recommendStageIdx = currentStageIdx;

                    if (ContainsApproved(finalResp))
                    {
                        recommendRev = IncrementLetter(lastRevLetter);
                        if (recommendStageIdx < STAGE_LADDER.Length - 1) recommendStageIdx++;
                    }
                    else if (ContainsRejected(finalResp) || ContainsResubmission(finalResp))
                    {
                        recommendRev = lastRevLetter; // 保持
                    }
                    else if (IgnoreType(finalResp))
                    {
                        problemCount++;
                        rows.Add(new[]
                        {
                            Cell.Plain(sheetNo),
                            Cell.Plain(prefix),
                            Cell.Plain(string.IsNullOrEmpty(revInUI) ? "(空)" : revInUI),
                            Cell.Plain(""),
                            Cell.Plain(""),
                            Cell.Plain($"{lastRevLetter}/{maxRevNum}"),
                            Cell.Plain($"{finalResp}（{latest.SourceFile}!Row {latest.RowIndex}）"),
                            Cell.Plain("Final response 属于忽略类型（Reviewed with major comments / for record only）")
                        });
                        continue;
                    }
                    else
                    {
                        recommendRev = lastRevLetter; // 未知文案，按保持处理
                    }

                    string recommendStage = STAGE_LADDER[recommendStageIdx];

                    bool mismatch = !string.Equals(revInUI, recommendRev, StringComparison.OrdinalIgnoreCase);
                    if (mismatch)
                    {
                        problemCount++;

                        // 当前 REV 用红色加粗放大；推荐 REV 用绿色加粗放大
                        var curRevCell = ColoredRevCell(revInUI, isGreen: false);
                        var recRevCell = ColoredRevCell(recommendRev, isGreen: true);

                        rows.Add(new[]
                        {
                            Cell.Plain(sheetNo),
                            Cell.Plain(prefix),
                            curRevCell,
                            recRevCell,
                            Cell.Plain(recommendStage),
                            Cell.Plain($"{lastRevLetter}/{maxRevNum}"),
                            Cell.Plain($"{finalResp}（{latest.SourceFile}!Row {latest.RowIndex}）"),
                            Cell.Plain("")
                        });
                    }
                }

                if (problemCount == 0)
                {
                    rows.Add(Array.Empty<Cell>());
                    rows.Add(new[] { Cell.Plain("未发现问题项（所选图纸的当前 REV 已与推荐一致）。") });
                }

                // 写入并打开 XLSX
                WriteSimpleXlsxInlineRich(xlsxPath, rows);
                TryOpen(xlsxPath);

                TaskDialog.Show("Revision Check", $"完成（只列问题项）。已输出并打开：\n{xlsxPath}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        // Final response 规则
        private static bool ContainsApproved(string s) => (s ?? "").IndexOf("approved", StringComparison.OrdinalIgnoreCase) >= 0;
        private static bool ContainsRejected(string s) => (s ?? "").IndexOf("rejected", StringComparison.OrdinalIgnoreCase) >= 0;
        private static bool ContainsResubmission(string s) => (s ?? "").IndexOf("reviewed with comments, resubmission", StringComparison.OrdinalIgnoreCase) >= 0;
        private static bool IgnoreType(string s)
        {
            string t = (s ?? "").ToLowerInvariant();
            return t.Contains("reviewed with major comments") || t.Contains("for record only");
        }

        // Revit 参数
        private static string GetParamStr(Element e, string name)
        {
            try { var p = e.LookupParameter(name); return p != null ? (p.AsString() ?? "").Trim() : ""; }
            catch { return ""; }
        }

        // 字母工具
        private static string ToLetter(int n)
        {
            int v = n + 1; var sb = new StringBuilder();
            while (v > 0) { v--; sb.Insert(0, (char)('A' + (v % 26))); v /= 26; }
            return sb.ToString();
        }
        private static string IncrementLetter(string letters)
        {
            if (string.IsNullOrEmpty(letters)) return "A";
            int v = 0; foreach (char c in letters) v = v * 26 + ((c - 'A') + 1);
            return ToLetter(v);
        }

        // 规范化 / 匹配
        private static readonly Regex RxSpaceLike = new Regex(@"\s+|[\u00A0\u2000-\u200D\u202F\uFEFF]", RegexOptions.Compiled);
        private static readonly Regex RxPunctLike = new Regex(@"[^\p{L}\p{Nd}]", RegexOptions.Compiled);
        private static string Canon(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = RxSpaceLike.Replace(s, "");
            t = t.Replace("（", "(").Replace("）", ")");
            int i = t.IndexOf('('); if (i >= 0) { int j = t.IndexOf(')', i + 1); if (j > i) t = t.Remove(i, j - i + 1); }
            t = RxPunctLike.Replace(t, "");
            return t.ToLowerInvariant();
        }
        private static bool KeyHit(string titleCell, string prefixFromRevit)
        {
            var ct = Canon(titleCell);
            var cp = Canon(prefixFromRevit);
            return !string.IsNullOrEmpty(ct) && !string.IsNullOrEmpty(cp) && ct.Contains(cp);
        }

        // Excel 行结构
        private class ExcelRow
        {
            public string Title;
            public int RevisionNum;
            public string FinalResponse;
            public string SourceFile;
            public int RowIndex;
        }

        // OpenXml 读取（含 inlineStr 兜底）
        private class SheetParseResult
        {
            public int HeaderHitCount;
            public List<ExcelRow> Rows = new List<ExcelRow>();
        }
        private static List<ExcelRow> ReadFormattedData_OpenXml(string xlsxPath)
        {
            var result = new List<ExcelRow>();
            string fileName = Path.GetFileName(xlsxPath);

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var dbgPath = Path.Combine(desktop, $"xlsx_debug_{fileName}.txt");
            var dbg = new List<string> { "=== FILE ===", xlsxPath };

            using (var za = ZipFile.OpenRead(xlsxPath))
            {
                var id2name = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var workbook = za.GetEntry("xl/workbook.xml");
                if (workbook != null)
                {
                    using (var sr = new StreamReader(workbook.Open(), Encoding.UTF8, true))
                    using (var xr = XmlReader.Create(sr))
                    {
                        while (xr.Read())
                        {
                            if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "sheet")
                            {
                                string name = (xr.GetAttribute("name") ?? "").Trim();
                                string sid = xr.GetAttribute("sheetId") ?? "";
                                if (!string.IsNullOrEmpty(sid))
                                {
                                    id2name[sid] = name;
                                    dbg.Add($"sheetId={sid} name='{name}'");
                                }
                            }
                        }
                    }
                }

                // sharedStrings,这玩意没用了读不到
                string[] shared = Array.Empty<string>();
                var sst = za.GetEntry("xl/sharedStrings.xml");
                if (sst != null)
                {
                    var list = new List<string>();
                    using (var sr = new StreamReader(sst.Open(), Encoding.UTF8, true))
                    using (var xr = XmlReader.Create(sr))
                    {
                        var sb = new StringBuilder(); bool inSi = false;
                        while (xr.Read())
                        {
                            if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "si") { inSi = true; sb.Clear(); }
                            else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "si") { inSi = false; list.Add(sb.ToString()); }
                            else if (inSi && xr.NodeType == XmlNodeType.Element && xr.LocalName == "t") { sb.Append(xr.ReadElementContentAsString()); }
                        }
                    }
                    shared = list.ToArray();
                    dbg.Add($"sharedStrings count = {shared.Length}");
                }

                var sheetEntries = za.Entries
                    .Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var entry in sheetEntries)
                {
                    string sid = TryGetSheetIdFromEntry(entry.FullName);
                    id2name.TryGetValue(sid, out var nm);
                    dbg.Add($"-- parsing {entry.FullName} (sheetId={sid}, name='{nm}')");

                    var parsed = ParseOneWorksheet(entry, shared, fileName, dbg);

                    if (parsed.HeaderHitCount >= 2)
                    {
                        if (!string.IsNullOrEmpty(nm) && string.Equals(Canon(nm), Canon(TARGET_SHEET_NAME), StringComparison.OrdinalIgnoreCase))
                        {
                            result.AddRange(parsed.Rows);
                            break;
                        }
                        if (result.Count == 0) result.AddRange(parsed.Rows);
                    }
                }
            }

            try { File.WriteAllLines(dbgPath, dbg, Encoding.UTF8); } catch { }
            return result;
        }
        private static SheetParseResult ParseOneWorksheet(ZipArchiveEntry sheetEntry, string[] shared, string fileName, List<string> dbg)
        {
            var ret = new SheetParseResult();
            int colTitle = -1, colRev = -1, colResp = -1;
            bool headerDone = false; int headerTryRows = 0; int currentRowIndex = 0;

            using (var sr = new StreamReader(sheetEntry.Open(), Encoding.UTF8, true))
            using (var xr = XmlReader.Create(sr))
            {
                List<CellVal> rowCells = null;

                while (xr.Read())
                {
                    if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "row")
                    {
                        rowCells = new List<CellVal>();
                        currentRowIndex = int.Parse(xr.GetAttribute("r") ?? "0");
                    }
                    else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "row")
                    {
                        if (rowCells != null && rowCells.Count > 0)
                        {
                            var arr = RowToSparseArray(rowCells);

                            if (!headerDone)
                            {
                                headerTryRows++;
                                if (headerTryRows <= 5)
                                    dbg.Add($" [row {currentRowIndex}] sample: " + string.Join(" | ", arr.Take(20).Select(v => v ?? "")));

                                for (int i = 0; i < arr.Length; i++)
                                {
                                    string raw = (arr[i] ?? "");
                                    string c = Canon(raw);
                                    if (colTitle < 0 && (c == "title" || c.Contains("title"))) colTitle = i;
                                    if (colRev < 0 && (c.StartsWith("revision") || c == "rev" || c.StartsWith("revis"))) colRev = i;
                                    if (colResp < 0 && (c.StartsWith("finalresponse") || c.StartsWith("finalresponses"))) colResp = i;
                                }

                                ret.HeaderHitCount = 0;
                                if (colTitle >= 0) ret.HeaderHitCount++;
                                if (colRev >= 0) ret.HeaderHitCount++;
                                if (colResp >= 0) ret.HeaderHitCount++;

                                if (colTitle >= 0 && colRev >= 0) headerDone = true;
                                else if (headerTryRows >= HEADER_SCAN_ROWS) break;
                            }
                            else
                            {
                                string title = SafeGet(arr, colTitle);
                                if (!string.IsNullOrWhiteSpace(title))
                                {
                                    string revStr = SafeGet(arr, colRev);
                                    int revNum = ParseIntSafe(revStr);

                                    ret.Rows.Add(new ExcelRow
                                    {
                                        Title = title,
                                        RevisionNum = revNum,
                                        FinalResponse = colResp >= 0 ? SafeGet(arr, colResp) : "",
                                        SourceFile = fileName,
                                        RowIndex = currentRowIndex
                                    });
                                }
                            }
                        }
                        rowCells = null;
                    }
                    else if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "c")
                    {
                        string r = xr.GetAttribute("r") ?? "";
                        string t = xr.GetAttribute("t") ?? "";
                        int colIdx = RefColToIndex(r);

                        string val = "";
                        bool isShared = t == "s";
                        bool isInline = t == "inlineStr";

                        if (!xr.IsEmptyElement)
                        {
                            while (xr.Read())
                            {
                                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "v")
                                {
                                    string raw = xr.ReadElementContentAsString();
                                    if (isShared)
                                    {
                                        if (int.TryParse(raw, out int sidx) && sidx >= 0 && sidx < shared.Length)
                                            val = shared[sidx];
                                    }
                                    else val = raw;
                                }
                                else if (isInline && xr.NodeType == XmlNodeType.Element && xr.LocalName == "is")
                                {
                                    val = ReadInlineString(xr);
                                }
                                else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "c") break;
                            }
                        }
                        rowCells?.Add(new CellVal { Col = colIdx, Val = val });
                    }
                }
            }
            dbg.Add($" headerHit={ret.HeaderHitCount} cols: Title={colTitle}, Revision={colRev}, FinalResp={colResp} rows={ret.Rows.Count}");
            return ret;
        }
        private static string ReadInlineString(XmlReader xr)
        {
            var sb = new StringBuilder();
            while (xr.Read())
            {
                if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t")
                    sb.Append(xr.ReadElementContentAsString());
                else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "is")
                    break;
            }
            return sb.ToString();
        }

        // Excel COM 
        private static List<ExcelRow> ReadFormattedData_ExcelCom(string xlsxPath)
        {
            var result = new List<ExcelRow>();
            string fileName = Path.GetFileName(xlsxPath);

            Type excelType = Type.GetTypeFromProgID("Excel.Application");
            if (excelType == null) return result;

            object app = null, books = null, book = null, sheets = null, sheet = null, used = null;
            try
            {
                app = Activator.CreateInstance(excelType);
                excelType.InvokeMember("Visible", System.Reflection.BindingFlags.SetProperty, null, app, new object[] { false });
                excelType.InvokeMember("DisplayAlerts", System.Reflection.BindingFlags.SetProperty, null, app, new object[] { false });

                books = excelType.InvokeMember("Workbooks", System.Reflection.BindingFlags.GetProperty, null, app, null);
                book = books.GetType().InvokeMember("Open", System.Reflection.BindingFlags.InvokeMethod, null, books, new object[] { xlsxPath, true, true });

                sheets = book.GetType().InvokeMember("Worksheets", System.Reflection.BindingFlags.GetProperty, null, book, null);
                try { sheet = sheets.GetType().InvokeMember("Item", System.Reflection.BindingFlags.GetProperty, null, sheets, new object[] { TARGET_SHEET_NAME }); }
                catch { sheet = sheets.GetType().InvokeMember("Item", System.Reflection.BindingFlags.GetProperty, null, sheets, new object[] { 2 }); }

                used = sheet.GetType().InvokeMember("UsedRange", System.Reflection.BindingFlags.GetProperty, null, sheet, null);
                object values = used.GetType().InvokeMember("Value2", System.Reflection.BindingFlags.GetProperty, null, used, null);

                if (values is object[,] arr)
                {
                    int r1 = arr.GetLowerBound(0), r2 = arr.GetUpperBound(0);
                    int c1 = arr.GetLowerBound(1), c2 = arr.GetUpperBound(1);

                    int headerRow = -1, colTitle = -1, colRev = -1, colResp = -1;
                    for (int r = r1; r <= Math.Min(r2, r1 + HEADER_SCAN_ROWS - 1); r++)
                    {
                        for (int c = c1; c <= c2; c++)
                        {
                            string raw = SafeCell(arr[r, c]);
                            string canon = Canon(raw);
                            if (colTitle < 0 && (canon == "title" || canon.Contains("title"))) { colTitle = c; headerRow = r; }
                            if (colRev < 0 && (canon.StartsWith("revision") || canon == "rev" || canon.StartsWith("revis"))) { colRev = c; headerRow = r; }
                            if (colResp < 0 && (canon.StartsWith("finalresponse") || canon.StartsWith("finalresponses"))) { colResp = c; headerRow = r; }
                        }
                        if (colTitle >= 0 && colRev >= 0) break;
                    }
                    if (colTitle < 0 || colRev < 0) return result;

                    for (int r = headerRow + 1; r <= r2; r++)
                    {
                        string title = SafeCell(arr[r, colTitle]);
                        if (string.IsNullOrWhiteSpace(title)) continue;

                        string revStr = SafeCell(arr[r, colRev]);
                        int revNum = ParseIntSafe(revStr);

                        string resp = colResp >= 0 ? SafeCell(arr[r, colResp]) : "";

                        result.Add(new ExcelRow
                        {
                            Title = title,
                            RevisionNum = revNum,
                            FinalResponse = resp,
                            SourceFile = fileName,
                            RowIndex = r
                        });
                    }
                }
            }
            catch { }
            finally
            {
                void Release(object o) { if (o != null) try { Marshal.FinalReleaseComObject(o); } catch { } }
                if (book != null) try { book.GetType().InvokeMember("Close", System.Reflection.BindingFlags.InvokeMethod, null, book, new object[] { false }); } catch { }
                Release(used); Release(sheet); Release(sheets); Release(book); Release(books);
                if (app != null) try { excelType.InvokeMember("Quit", System.Reflection.BindingFlags.InvokeMethod, null, app, null); } catch { }
                Release(app);
            }
            return result;
        }

        private static string SafeCell(object v) => v == null ? "" : v.ToString();

        // Worksheet 解析小工具
        private class CellVal { public int Col; public string Val; }
        private static string[] RowToSparseArray(List<CellVal> row)
        {
            int max = row.Max(c => c.Col);
            string[] arr = new string[max + 1];
            foreach (var c in row) arr[c.Col] = c.Val;
            return arr;
        }
        private static string SafeGet(string[] arr, int idx) => (idx >= 0 && idx < arr.Length) ? (arr[idx] ?? "") : "";
        private static int ParseIntSafe(string s) => int.TryParse((s ?? "").Trim(), out int v) ? v : 0;
        private static int RefColToIndex(string cellRef)
        {
            if (string.IsNullOrEmpty(cellRef)) return 0;
            int i = 0; while (i < cellRef.Length && char.IsLetter(cellRef[i])) i++;
            string letters = cellRef.Substring(0, i).ToUpperInvariant();
            int col = 0; foreach (char ch in letters) col = col * 26 + (ch - 'A' + 1);
            return Math.Max(0, col - 1);
        }
        private static string TryGetSheetIdFromEntry(string fullName)
        {
            var fn = Path.GetFileNameWithoutExtension(fullName);
            if (fn != null && fn.StartsWith("sheet", StringComparison.OrdinalIgnoreCase))
            {
                var tail = fn.Substring(5);
                if (!string.IsNullOrEmpty(tail) && tail.All(char.IsDigit)) return tail;
            }
            return "";
        }

        // 输出xlsx
        private static Cell ColoredRevCell(string rev, bool isGreen)
        {
            if (string.IsNullOrWhiteSpace(rev)) rev = "(空)";
            var runs = new List<Run> { isGreen ? Run.GreenBig(rev, REV_FONT_PT) : Run.RedBig(rev, REV_FONT_PT) };
            return Cell.WithRuns(runs);
        }

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
            int x = colIndex + 1; var s = new StringBuilder();
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

        // 富文本 
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
            public bool Bold;
            public static Run RedBig(string t, double pt) => new Run { Text = t, ColorRgb = "FFFF0000", Bold = true, SizePt = pt };
            public static Run GreenBig(string t, double pt) => new Run { Text = t, ColorRgb = "FF00B050", Bold = true, SizePt = pt };
        }
    }
}






