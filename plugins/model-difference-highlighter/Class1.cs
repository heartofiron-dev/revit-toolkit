/* 
 * 比较两版模型（按 getmark / Mark）并：
 *   • 旧模型：除“新增”外全部着色
 *   • 新模型：仅“新增”着色
 *   • 生成 CSV 说明差异
 * 容差 = 对角线 × 1 %（≥ 1 mm）
 * CSV 字段：
 *   ChangeType, Mark, Category, ElementId, Center(mm), Size(mm)
 * 
 */

using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;

namespace IFCRevitDiff
{
    [Transaction(TransactionMode.Manual)]
    public class DiffCommand : IExternalCommand
    {
        const double MIN_TOL_MM = 1.0;   // 绝对下限
        const double TOL_RATIO = 0.01;  // 相对 1 %
        const double FT2MM = 304.8; // ft → mm
        const string CSV_PATH = @"C:\IFC_Diff_Output\diff_log.csv";

        public Result Execute(ExternalCommandData data,
                              ref string msg,
                              ElementSet _)
        {
            /*  打开文件检测  */
            UIApplication uiapp = data.Application;
            var docs = uiapp.Application.Documents
                       .Cast<Document>()
                       .Where(d => !d.IsFamilyDocument)
                       .ToList();
            if (docs.Count < 2)
            {
                msg = "请同时打开 V1 与 V2 两个项目文件";
                return Result.Failed;
            }

            /*  判定旧 / 新  */
            Document docV1 = docs.First(d => d.Title.ToLower().Contains("v1"));
            Document docV2 = docs.First(d => d.Title.ToLower().Contains("v2"));

            /*  建索引  */
            var idxV1 = BuildIndex(docV1);
            var idxV2 = BuildIndex(docV2);

            /*  CSV 收集  */
            var rows = new List<string>();
            rows.Add("ChangeType,Mark,Category,ElementId,Center(mm),Size(mm)");

            /*  着色样式  */
            var styDel = NewOGS(255, 60, 60);  // 删除    红
            var styAdd = NewOGS(60, 120, 255);  // 新增   蓝
            var styStb = NewOGS(40, 200, 40);  // 未变    绿
            var styPos = NewOGS(255, 215, 0);  // 位置变   金黄 
            var styGeo = NewOGS(255, 165, 0);  // 形体变   橙色

            /* ---------- 旧版视图着色 ---------- */
            using (var tx1 = new Transaction(docV1, "Diff-Color-V1"))
            {
                tx1.Start();
                View3D view1 = docV1.ActiveView as View3D;
                foreach (var kv in idxV1)
                {
                    string mk = kv.Key;
                    Element e1 = kv.Value;

                    /* 删除 */
                    if (!idxV2.TryGetValue(mk, out Element e2))
                    {
                        view1?.SetElementOverrides(e1.Id, styDel);
                        AddRow(rows, "Removed", e1);
                        continue;
                    }

                    /* 未变  形体变 */
                    if (IsUnchanged(e1, e2))
                    {
                        view1?.SetElementOverrides(e1.Id, styStb);
                    }
                    else if (IsMoved(e1, e2)) // 移位 
                    {
                        view1?.SetElementOverrides(e1.Id, styPos);
                        AddRow(rows, "Moved", e2);   // 用新位置
                    }
                    else
                    {
                        view1?.SetElementOverrides(e1.Id, styGeo);
                        AddRow(rows, "GeometryChanged", e2);
                    }
                }
                tx1.Commit();
            }

            /* ---------- 新版视图：新增 ---------- */
            using (var tx2 = new Transaction(docV2, "Diff-Color-V2"))
            {
                tx2.Start();
                View3D view2 = docV2.ActiveView as View3D;
                foreach (var kv in idxV2)
                {
                    if (idxV1.ContainsKey(kv.Key)) continue;
                    view2?.SetElementOverrides(kv.Value.Id, styAdd);
                    AddRow(rows, "Added", kv.Value);
                }
                tx2.Commit();
            }

            /* ---------- 写 CSV ---------- */
            Directory.CreateDirectory(Path.GetDirectoryName(CSV_PATH));
            File.WriteAllLines(CSV_PATH, rows, new UTF8Encoding(true));

            TaskDialog.Show("IFC Diff", $"完成！\nCSV: {CSV_PATH}");
            return Result.Succeeded;
        }

        /* Helpers */

        /* 索引：getmark / Mark → Element */
        static Dictionary<string, Element> BuildIndex(Document doc) 
        {
            var dict = new Dictionary<string, Element>();
            var col = new FilteredElementCollector(doc)
                       .WhereElementIsNotElementType();
            foreach (Element e in col)
            {
                string mark = GetMark(e);
                if (string.IsNullOrWhiteSpace(mark)) continue;
                if (!dict.ContainsKey(mark)) dict.Add(mark, e);
            }
            return dict;
        }

        static string GetMark(Element e)
        {
            string m = e.LookupParameter("getmark")?.AsString();
            if (!string.IsNullOrWhiteSpace(m)) return m.Trim();
            m = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
            return string.IsNullOrWhiteSpace(m) ? "" : m.Trim();
        }

        /* 判定 */
        static bool IsUnchanged(Element a, Element b) // 无变化
        {
            double tol = GetTol(a);
            return GeometryEqual(a, b, tol) && CenterDistance(a, b) <= tol;
        }
        static bool IsMoved(Element a, Element b) // 移位
        {
            double tol = GetTol(a);
            return GeometryEqual(a, b, tol) && CenterDistance(a, b) > tol;
        }
        static bool GeometryEqual(Element a, Element b, double tol)
        {
            (XYZ sa, XYZ ea) = GetBBox(a);
            (XYZ sb, XYZ eb) = GetBBox(b);
            return Math.Abs((ea - sa).X - (eb - sb).X) < tol &&
                   Math.Abs((ea - sa).Y - (eb - sb).Y) < tol &&
                   Math.Abs((ea - sa).Z - (eb - sb).Z) < tol;
        }
        static double CenterDistance(Element a, Element b)
        {
            XYZ ca = Center(a), cb = Center(b);
            return ca.DistanceTo(cb);
        }
        static double GetTol(Element e)
        {
            double diag = (GetBBox(e).Item2).DistanceTo(GetBBox(e).Item1);
            return Math.Max(MIN_TOL_MM / FT2MM, diag * TOL_RATIO);
        }

        /* 几何 */
        static (XYZ, XYZ) GetBBox(Element e)
        {
            BoundingBoxXYZ bb = e.get_BoundingBox(null);
            return (bb.Min, bb.Max);
        }
        static XYZ Center(Element e)
        {
            (XYZ s, XYZ t) = GetBBox(e);
            return (s + t) / 2;
        }
        static XYZ Size(Element e)
        {
            (XYZ s, XYZ t) = GetBBox(e);
            return (t - s);
        }

        /* CSV 行 */
        static void AddRow(List<string> rows, string type, Element e)
        {
            XYZ c = Center(e) * FT2MM;   // 转 mm
            XYZ s = Size(e) * FT2MM;
            string fmt(XYZ v) => $"{v.X:F1};{v.Y:F1};{v.Z:F1}";
            rows.Add(string.Join(",",
                type,
                GetMark(e),
                e.Category?.Name ?? "",
                e.Id.IntegerValue,
                fmt(c),
                fmt(s)));
        }

        /* 着色样式 */
        static OverrideGraphicSettings NewOGS(byte r, byte g, byte b)
        {
            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(new Color(r, g, b));
            ogs.SetSurfaceTransparency(35);
            return ogs;
        }
    }
}














