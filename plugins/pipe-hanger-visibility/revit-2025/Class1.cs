// Revit 2025 / .NET 8 / C# 12
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Generic;
using System.Linq;

namespace PipeHangerTools
{
    static class H
    {
        // 交互厚度（mm）——固定值
        public const double NEAR_MM = 10.0;

        #region 小工具
        static double Mm(double v) => UnitUtils.ConvertToInternalUnits(v, UnitTypeId.Millimeters);

        // AABB 相交（数值判断）
        static bool BoxIntersects(XYZ minA, XYZ maxA, XYZ minB, XYZ maxB)
        {
            return !(maxA.X < minB.X || minA.X > maxB.X ||
                     maxA.Y < minB.Y || minA.Y > maxB.Y ||
                     maxA.Z < minB.Z || minA.Z > maxB.Z);
        }

        // 取元素包围盒（优先模型空间）
        static BoundingBoxXYZ BB(Element e, View v)
            => e.get_BoundingBox(null) ?? e.get_BoundingBox(v);

        // 求半长轴：返回 (hx, hy, hz) 和中心 c
        static void HalfSizes(BoundingBoxXYZ bb, out XYZ c, out double hx, out double hy, out double hz)
        {
            var min = bb.Min; var max = bb.Max;
            c = (min + max) * 0.5;
            hx = (max.X - min.X) * 0.5;
            hy = (max.Y - min.Y) * 0.5;
            hz = (max.Z - min.Z) * 0.5;
        }

        // 根据半长轴和中心构造 min/max
        static void FromHalf(XYZ c, double hx, double hy, double hz, out XYZ min, out XYZ max)
        {
            min = new XYZ(c.X - hx, c.Y - hy, c.Z - hz);
            max = new XYZ(c.X + hx, c.Y + hy, c.Z + hz);
        }
        #endregion

        #region —— 两种判定 ——
        // 1) 管道的“缩小盒子”：沿轴向保持，横向两轴压到 固定厚度 nearMm
        static void SmallPipeBox(MEPCurve pipe, View v, double nearMm, out XYZ min, out XYZ max)
        {
            var bb = BB(pipe, v); var c = default(XYZ);
            double hx, hy, hz; HalfSizes(bb, out c, out hx, out hy, out hz);

            double t = Mm(nearMm); // 固定厚度十毫米

            double ax = hx, ay = hy, az = hz;
            if (hx >= hy && hx >= hz) { ay = System.Math.Min(ay, t); az = System.Math.Min(az, t); } // X 最长
            else if (hy >= hx && hy >= hz) { ax = System.Math.Min(ax, t); az = System.Math.Min(az, t); } // Y 最长
            else { ax = System.Math.Min(ax, t); ay = System.Math.Min(ay, t); } // Z 最长

            FromHalf(c, ax, ay, az, out min, out max);
        }

        // 2) 单抱箍的本体包围盒（对准管轴线）：把最长轴（天线）压薄，中心沿该轴对齐到“抱箍中心在管轴上的投影”
        static void ClampBodyBoxAtPipe(Element hanger, MEPCurve pipe, View v, double thicknessMm, out XYZ min, out XYZ max)
        {
            var bb = BB(hanger, v); var c0 = default(XYZ);
            double hx, hy, hz; HalfSizes(bb, out c0, out hx, out hy, out hz);

            double t = Mm(thicknessMm);
            bool xL = hx >= hy && hx >= hz;
            bool yL = hy >= hx && hy >= hz;
            bool zL = !xL && !yL;

            // 把抱箍盒中心投影到本管轴线上
            var lc = (pipe.Location as LocationCurve)?.Curve;
            XYZ proj = c0;
            var pr = lc?.Project(c0);
            if (pr != null) proj = pr.XYZPoint;

            double ax = hx, ay = hy, az = hz;
            var c = c0;
            if (xL) { ax = t; c = new XYZ(proj.X, c0.Y, c0.Z); }
            else if (yL) { ay = t; c = new XYZ(c0.X, proj.Y, c0.Z); }
            else { az = t; c = new XYZ(c0.X, c0.Y, proj.Z); }

            FromHalf(c, ax, ay, az, out min, out max);
        }

        // 3) 门型支架“薄墙盒”：把最短轴压成near的薄板，看穿过的管子
        static void PortalWallBox(Element hanger, View v, double nearMm, out XYZ min, out XYZ max)
        {
            var bb = BB(hanger, v); var c = default(XYZ);
            double hx, hy, hz; HalfSizes(bb, out c, out hx, out hy, out hz);

            double t = Mm(nearMm); // 墙厚

            double ax = hx, ay = hy, az = hz;
            if (hx <= hy && hx <= hz) ax = System.Math.Min(ax, t);
            else if (hy <= hx && hy <= hz) ay = System.Math.Min(ay, t);
            else az = System.Math.Min(az, t);

            FromHalf(c, ax, ay, az, out min, out max);
        }

        // 判定：这个单鲍菇是否属于这根管
        static bool Belongs_Clamp(Element hanger, MEPCurve pipe, View v)
        {
            XYZ pMin, pMax; SmallPipeBox(pipe, v, NEAR_MM, out pMin, out pMax);
            XYZ hMin, hMax; ClampBodyBoxAtPipe(hanger, pipe, v, NEAR_MM, out hMin, out hMax);
            return BoxIntersects(pMin, pMax, hMin, hMax);
        }

        // 判定：这面镜子是否属于这根管
        static bool Belongs_Portal(Element hanger, MEPCurve pipe, View v)
        {
            XYZ pMin, pMax; SmallPipeBox(pipe, v, NEAR_MM, out pMin, out pMax);
            XYZ wMin, wMax; PortalWallBox(hanger, v, NEAR_MM, out wMin, out wMax);
            return BoxIntersects(pMin, pMax, wMin, wMax);
        }

        // 识别两种类型（按类型名关键字目前只处理机械设备类别）
        static int HangerKind(Document doc, Element e)
        {
            var catOk = e.Category != null && e.Category.Id.Value == (int)BuiltInCategory.OST_MechanicalEquipment;
            if (!catOk) return 0;
            var name = (doc.GetElement(e.GetTypeId())?.Name ?? e.Name ?? "").ToLower();
            if (name.Contains("抱箍")) return 1; // 单鲍菇支吊架
            if (name.Contains("u型吊架") || name.Contains("角钢")) return 2; // 角钢支吊架
            return 0;
        }

        public static bool IsPipe(Element e)
        {
            return e != null && e.Category != null &&
                   e.Category.Id.Value == (int)BuiltInCategory.OST_PipeCurves;
        }

        // 支持“过滤器隐藏”
        public static bool IsHiddenForUs(Document doc, View v, Element e)
        {
            if (e == null) return false;
            if (e.IsHidden(v)) return true;
            foreach (var fid in v.GetFilters())
            {
                if (v.GetFilterVisibility(fid)) continue;
                var fe = doc.GetElement(fid) as FilterElement; if (fe == null || e.Category == null) continue;

                var pfe = fe as ParameterFilterElement;
                if (pfe != null)
                {
                    var cats = pfe.GetCategories();
                    if (cats == null || !cats.Contains(e.Category.Id)) continue;
                    var ef = pfe.GetElementFilter();
                    if (ef != null && new FilteredElementCollector(doc).WherePasses(ef).ToElementIds().Contains(e.Id))
                        return true;
                }
                var sfe = fe as SelectionFilterElement;
                if (sfe != null && sfe.GetElementIds().Contains(e.Id)) return true;
            }
            return false;
        }

        // 主函数：按上面的两种规则找“这根管上的吊架”
        public static IEnumerable<ElementId> HangersOnPipe_ByYourRules(Document doc, View v, MEPCurve pipe)
        {
            var hangers = new FilteredElementCollector(doc, v.Id)
                .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .WhereElementIsNotElementType();

            foreach (var h in hangers)
            {
                int kind = HangerKind(doc, h);
                if (kind == 1) { if (Belongs_Clamp(h, pipe, v)) yield return h.Id; }
                else if (kind == 2) { if (Belongs_Portal(h, pipe, v)) yield return h.Id; }
            }
        }
        #endregion
    }

    [Transaction(TransactionMode.Manual)]
    public class HideHangersOfHiddenPipes : IExternalCommand
    {
        static double Mm(double v) => UnitUtils.ConvertToInternalUnits(v, UnitTypeId.Millimeters);
        static BoundingBoxXYZ BB(Element e, View v) => e.get_BoundingBox(null) ?? e.get_BoundingBox(v);
        static void HalfSizes(BoundingBoxXYZ bb, out XYZ c, out double hx, out double hy, out double hz)
        { var min = bb.Min; var max = bb.Max; c = (min + max) * 0.5; hx = (max.X - min.X) * 0.5; hy = (max.Y - min.Y) * 0.5; hz = (max.Z - min.Z) * 0.5; }
        static void FromHalf(XYZ c, double hx, double hy, double hz, out XYZ min, out XYZ max)
        { min = new XYZ(c.X - hx, c.Y - hy, c.Z - hz); max = new XYZ(c.X + hx, c.Y + hy, c.Z + hz); }
        static bool BoxIntersects(XYZ minA, XYZ maxA, XYZ minB, XYZ maxB)
        { return !(maxA.X < minB.X || minA.X > maxB.X || maxA.Y < minB.Y || minA.Y > maxB.Y || maxA.Z < minB.Z || minA.Z > maxB.Z); }

        // 管道“缩小盒子”
        static void SmallPipeBox(MEPCurve pipe, View v, double nearMm, out XYZ min, out XYZ max)
        {
            var bb = BB(pipe, v); var c = default(XYZ);
            double hx, hy, hz; HalfSizes(bb, out c, out hx, out hy, out hz);

            double t = Mm(nearMm);

            double ax = hx, ay = hy, az = hz;
            if (hx >= hy && hx >= hz) { ay = System.Math.Min(ay, t); az = System.Math.Min(az, t); }
            else if (hy >= hx && hy >= hz) { ax = System.Math.Min(ax, t); az = System.Math.Min(az, t); }
            else { ax = System.Math.Min(ax, t); ay = System.Math.Min(ay, t); }

            FromHalf(c, ax, ay, az, out min, out max);
        }

        // 单抱箍本体盒子去掉天线
        static void ClampBodyBoxAtPipe(Element hanger, MEPCurve pipe, View v, double thicknessMm, out XYZ min, out XYZ max)
        {
            var bb = BB(hanger, v); var c0 = default(XYZ);
            double hx, hy, hz; HalfSizes(bb, out c0, out hx, out hy, out hz);

            double t = Mm(thicknessMm);
            bool xL = hx >= hy && hx >= hz;
            bool yL = hy >= hx && hy >= hz;
            bool zL = !xL && !yL;

            var lc = (pipe.Location as LocationCurve)?.Curve;
            XYZ proj = c0;
            var pr = lc?.Project(c0);
            if (pr != null) proj = pr.XYZPoint;

            double ax = hx, ay = hy, az = hz;
            var c = c0;
            if (xL) { ax = t; c = new XYZ(proj.X, c0.Y, c0.Z); }
            else if (yL) { ay = t; c = new XYZ(c0.X, proj.Y, c0.Z); }
            else { az = t; c = new XYZ(c0.X, c0.Y, proj.Z); }

            FromHalf(c, ax, ay, az, out min, out max);
        }

        // 门型支架“薄墙盒”
        static void PortalWallBox(Element hanger, View v, double nearMm, out XYZ min, out XYZ max)
        {
            var bb = BB(hanger, v); var c = default(XYZ);
            double hx, hy, hz; HalfSizes(bb, out c, out hx, out hy, out hz);

            double t = Mm(nearMm);

            double ax = hx, ay = hy, az = hz;
            if (hx <= hy && hx <= hz) ax = System.Math.Min(ax, t);
            else if (hy <= hx && hy <= hz) ay = System.Math.Min(ay, t);
            else az = System.Math.Min(az, t);

            FromHalf(c, ax, ay, az, out min, out max);
        }

        // 类型识别（目前仅机械设备）
        static int HangerKind(Document doc, Element e)
        {
            var catOk = e.Category != null && e.Category.Id.Value == (int)BuiltInCategory.OST_MechanicalEquipment;
            if (!catOk) return 0;
            var name = (doc.GetElement(e.GetTypeId())?.Name ?? e.Name ?? "").ToLower();
            if (name.Contains("抱箍")) return 1;
            if (name.Contains("u型吊架") || name.Contains("角钢")) return 2;
            return 0;
        }

        // 单抱箍：小管盒相交鲍菇盒
        static bool Belongs_Clamp(Element hanger, MEPCurve pipe, View v)
        {
            XYZ pMin, pMax; SmallPipeBox(pipe, v, H.NEAR_MM, out pMin, out pMax);
            XYZ hMin, hMax; ClampBodyBoxAtPipe(hanger, pipe, v, H.NEAR_MM, out hMin, out hMax);
            return BoxIntersects(pMin, pMax, hMin, hMax);
        }

        // 镜子：小管盒相交薄墙盒
        static bool Belongs_Portal(Element hanger, MEPCurve pipe, View v)
        {
            XYZ pMin, pMax; SmallPipeBox(pipe, v, H.NEAR_MM, out pMin, out pMax);
            XYZ wMin, wMax; PortalWallBox(hanger, v, H.NEAR_MM, out wMin, out wMax);
            return BoxIntersects(pMin, pMax, wMin, wMax);
        }

        // 收集“在此视图被隐藏”的所有管道
        static HashSet<ElementId> GetHiddenPipeIds(Document doc, View v)
        {
            var result = new HashSet<ElementId>();
            var pipesCatId = new ElementId(BuiltInCategory.OST_PipeCurves);

            // 所有管（全文档，避免因“整类隐藏”而收不到）
            var basePipes = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PipeCurves)
                .WhereElementIsNotElementType();

            // 第一类类别隐藏
            if (v.CanCategoryBeHidden(pipesCatId) && v.GetCategoryHidden(pipesCatId))
                result.UnionWith(basePipes.ToElementIds());

            // 第二类过滤器隐藏（参数过滤器）
            foreach (var fid in v.GetFilters())
            {
                if (v.GetFilterVisibility(fid)) continue;
                var pfe = doc.GetElement(fid) as ParameterFilterElement;
                if (pfe == null) continue;

                var cats = pfe.GetCategories();
                if (cats == null || !cats.Contains(pipesCatId)) continue;

                var ef = pfe.GetElementFilter();
                if (ef == null) continue;

                var hit = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_PipeCurves)
                    .WhereElementIsNotElementType()
                    .WherePasses(ef)
                    .ToElementIds();

                result.UnionWith(hit);
            }

            // 第三类按图元隐藏逐个检查
            foreach (Element p in basePipes)
                if (p.IsHidden(v)) result.Add(p.Id);

            return result;
        }

        // 以管的缩小盒子外扩一点，只在附近找候选吊架，不再扩大
        static IEnumerable<Element> GetNearHangers(Document doc, View v, MEPCurve pipe, double padMm)
        {
            XYZ pMin, pMax; SmallPipeBox(pipe, v, H.NEAR_MM, out pMin, out pMax);
            double pad = Mm(padMm);
            var outline = new Outline(pMin - new XYZ(pad, pad, pad), pMax + new XYZ(pad, pad, pad));

            return new FilteredElementCollector(doc, v.Id)
                .OfCategory(BuiltInCategory.OST_MechanicalEquipment).WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .ToElements();
        }

        // 一个吊架是否属于这根管按上面两种类型分别判断
        static bool BelongsToPipeByRules(Document doc, View v, Element hanger, MEPCurve pipe)
        {
            int kind = HangerKind(doc, hanger);
            if (kind == 1) return Belongs_Clamp(hanger, pipe, v);
            if (kind == 2) return Belongs_Portal(hanger, pipe, v);
            return false;
        }

        public Result Execute(ExternalCommandData c, ref string m, ElementSet s)
        {
            var doc = c.Application.ActiveUIDocument.Document;
            var v = doc.ActiveView;

            // 1) 找在此视图被隐藏的管（类别/过滤器/按元素三种）
            var hiddenPipeIds = GetHiddenPipeIds(doc, v);

            var toHide = new HashSet<ElementId>();

            // 1.1) 对被隐藏的管：只在附近找候选（按视图收集器，快），命中就按元素隐藏
            foreach (var pid in hiddenPipeIds)
            {
                var pipe = doc.GetElement(pid) as MEPCurve;
                if (pipe == null) continue;

                foreach (var h in GetNearHangers(doc, v, pipe, 200 /*mm 外扩范围*/))
                    if (BelongsToPipeByRules(doc, v, h, pipe))
                        toHide.Add(h.Id);
            }

            // 这里只能隐藏可按类别隐藏的
            toHide.RemoveWhere(id =>
            {
                var el = doc.GetElement(id);
                return el == null || el.Category == null || !v.CanCategoryBeHidden(el.Category.Id);
            });

            if (toHide.Count == 0) return Result.Succeeded;

            using (var t = new Transaction(doc, "Hide/Unhide hangers with pipes"))
            {
                t.Start();
                if (toHide.Count > 0) v.HideElements(toHide);
                t.Commit();
            }
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class SelectHangersWithPipe : IExternalCommand
    {
        public Result Execute(ExternalCommandData c, ref string m, ElementSet s)
        {
            var uidoc = c.Application.ActiveUIDocument;
            var doc = uidoc.Document;
            var v = doc.ActiveView;

            var sel = uidoc.Selection.GetElementIds().ToList();
            var result = new HashSet<ElementId>(sel);

            foreach (var id in sel)
            {
                var e = doc.GetElement(id);
                if (!H.IsPipe(e)) continue;
                foreach (var hid in H.HangersOnPipe_ByYourRules(doc, v, (MEPCurve)e))
                    result.Add(hid);
            }

            uidoc.Selection.SetElementIds(result);
            return Result.Succeeded;
        }
    }
}
