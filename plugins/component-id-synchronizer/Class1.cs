/* (Revit 2024 / C# 7.3)
* ──────────────────────────────────────────────────────────────
* 目标：对“同尺寸”的元素做全局一对一配对（匈牙利最小化），
*      先硬门槛过滤（纯平移判定：尺寸一致 + Min/Max/Center 对齐，
*      可选方向一致 + 连接器对齐），再对候选对儿打分，
*      全局最小总代价匹配；匹配结果再做阈值与歧义拦截，
*      仅对“确定性强”的对儿在 v2 写入 v1 的 getmark，并高亮。
*
* 使用：
* 1. 同时/分别选择 v1、v2 RVT（弹窗可选任意路径）
* 2. 执行命令
* 3. 输出 CSV: C:\IFC_Diff_Output\mark_sync_global.csv
*    列：Mode,OldMark,NewMark,OldId,NewId,CenterNew(mm),dXmm,dYmm,dZmm,Cost,Note
* ──────────────────────────────────────────────────────────────*/

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WF = System.Windows.Forms;

namespace IFCRevitDiff
{
    [Transaction(TransactionMode.Manual)]
    public class MarkSyncCommand : IExternalCommand
    {
        /* ===== 常量与权重 ===== */
        const double FT2MM = 304.8;
        const double MIN_TOL_MM = 1.0;     // 几何最小容差（mm）
        const double TOL_RATIO = 0.01;    // 几何相对容差（bbox 对角线的 1%）
        const double CONN_TOL_MM = 5.0;    // 连接器容差（mm）
        const double ORI_DOT_MIN = 0.999;  // 方向一致阈值（≈0.1°）

        // 代价权重（中心 > 最大点 >> 方向；连接器信息可信时提高 W_CONN）
        const double W_CENTER = 1.0;
        const double W_MAXPT = 0.8;
        const double W_ORI = 0.2;
        const double W_CONN = 1.5;

        // 歧义边际：与次优的差距 < margin（= tol*0.5）则判 ambiguous
        const double AMBIGUITY_MARGIN_RATIO = 0.5;

        const string CSV_PATH = @"C:\IFC_Diff_Output\mark_sync_global.csv";

        // —— 功能通道参数（尺寸可变但功能不变） ——
        const double ANCH_BIN_MM = 100.0; // 锚点坐标量化步长（mm）
        const double FUNC_ANCH_TOL_MM = 50.0;  // 锚点对齐容差（mm）
        const double W_FUNC = 2.0;   // 代价中“锚点残差”权重

        struct Anchor
        {
            public string key; // 类别|族|类型|量化坐标
            public XYZ pos; // 模型坐标（英尺）
        }

        struct CandidateInfo
        {
            public bool valid;
            public bool isFunc;       // 是否来自“功能通道”
            public XYZ T;             // 平移向量（英尺）
            public double centerResid;
            public double maxResid;
            public double oriPenalty;
            public double connMean;
        }

        public Result Execute(ExternalCommandData data, ref string msg, ElementSet _)
        {
            UIApplication uiapp = data.Application;

            // —— 选择 v1/v2 文件 ——
            string oldPath = PickRvt("请选择【旧模型】(v1) .rvt");
            if (string.IsNullOrEmpty(oldPath)) return Result.Cancelled;
            string newPath = PickRvt("请选择【新模型】(v2/v3) .rvt");
            if (string.IsNullOrEmpty(newPath) || oldPath == newPath)
            {
                TaskDialog.Show("Mark Sync", "无效选择（相同文件或取消）。");
                return Result.Failed;
            }

            Document docOld = GetOrOpen(uiapp, oldPath);
            Document docNew = GetOrOpen(uiapp, newPath);

            // —— 收集 MEP 元素 ——
            var oldElems = Collect(docOld);
            var newElems = Collect(docNew);

            // —— 按“尺寸签名”分组（英尺 3 位小数） ——
            var oldGroups = GroupBySizeKey(oldElems);
            var newGroups = GroupBySizeKey(newElems);

            // —— 视觉高亮设置（在 v2 上高亮） ——
            OverrideGraphicSettings ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(new Color(150, 80, 255));
            ogs.SetSurfaceForegroundPatternColor(new Color(150, 80, 255));
            ogs.SetSurfaceForegroundPatternId(GetSolidFillId(docNew));
            ogs.SetSurfaceTransparency(30);
            View view = docNew.ActiveView;

            var rows = new List<string> {
        "Mode,OldMark,NewMark,OldId,NewId,CenterNew(mm),dXmm,dYmm,dZmm,Cost,Note"
      };
            int changed = 0;

            using (Transaction tx = new Transaction(docNew, "Sync getmark (global matching)"))
            {
                tx.Start();

                foreach (var key in oldGroups.Keys.Intersect(newGroups.Keys))
                {
                    var olds = oldGroups[key];
                    var news = newGroups[key];
                    if (olds.Count == 0 || news.Count == 0) continue;

                    // 各自容差
                    var tolOld = new double[olds.Count];
                    for (int i = 0; i < olds.Count; i++) tolOld[i] = GetTol(olds[i]);
                    var tolNew = new double[news.Count];
                    for (int j = 0; j < news.Count; j++) tolNew[j] = GetTol(news[j]);

                    double connTolFt = CONN_TOL_MM / FT2MM;

                    // —— 代价矩阵（不可选=+∞；补齐为 N×N 方阵） ——
                    int R = news.Count, C = olds.Count, N = Math.Max(R, C);
                    double[,] cost = new double[N, N];
                    for (int r = 0; r < N; r++) for (int c = 0; c < N; c++) cost[r, c] = double.PositiveInfinity;

                    var cand = new CandidateInfo[R, C];

                    // —— 构建候选（通道 A + 通道 B） ——
                    for (int j = 0; j < R; j++)
                    {
                        for (int i = 0; i < C; i++)
                        {
                            Element eOld = olds[i], eNew = news[j];
                            double tol = Math.Max(tolOld[i], tolNew[j]);

                            bool accepted = false;
                            bool isFunc = false;
                            XYZ T = XYZ.Zero;
                            double maxResid = 0.0, ctrResid = 0.0, oriPenalty = 0.0, connMean = 0.0;
                            double costVal = double.PositiveInfinity;

                            // ===== 通道 A：尺寸一致 + 纯平移 =====
                            if (GeometryEqual(eOld, eNew, tol))
                            {
                                XYZ aMin, aMax; GetBBox(eOld, out aMin, out aMax);
                                XYZ bMin, bMax; GetBBox(eNew, out bMin, out bMax);
                                T = bMin - aMin;

                                maxResid = (aMax + T).DistanceTo(bMax);
                                ctrResid = (Center(eOld) + T).DistanceTo(Center(eNew));
                                if (maxResid <= tol && ctrResid <= tol)
                                {
                                    if (TryGetDir(eOld, out XYZ uo) && TryGetDir(eNew, out XYZ un))
                                    {
                                        double dot = Math.Abs(DotSafe(uo, un));
                                        if (dot < ORI_DOT_MIN) goto AFTER_CHANNELS; // 方向不一致
                                        oriPenalty = 1.0 - dot;
                                    }

                                    var oldConns = GetConnectors(eOld);
                                    var newConns = GetConnectors(eNew);
                                    bool connOk = true;
                                    if (oldConns.Count > 0 && newConns.Count > 0)
                                    {
                                        if (oldConns.Count != newConns.Count) connOk = false;
                                        else
                                        {
                                            bool[] used = new bool[newConns.Count];
                                            double sum = 0.0, mx = 0.0;
                                            for (int k = 0; k < oldConns.Count && connOk; k++)
                                            {
                                                XYZ p = oldConns[k].Origin + T;
                                                int bestIdx = -1; double bestD = double.PositiveInfinity;
                                                for (int h = 0; h < newConns.Count; h++)
                                                {
                                                    if (used[h]) continue;
                                                    double d = p.DistanceTo(newConns[h].Origin);
                                                    if (d < bestD) { bestD = d; bestIdx = h; }
                                                }
                                                if (bestIdx < 0 || bestD > connTolFt) connOk = false;
                                                else { used[bestIdx] = true; sum += bestD; if (bestD > mx) mx = bestD; }
                                            }
                                            connMean = (oldConns.Count > 0) ? (oldConns.Count > 0 ? sum / oldConns.Count : 0.0) : 0.0;
                                        }
                                    }
                                    if (connOk)
                                    {
                                        costVal = W_CENTER * ctrResid + W_MAXPT * maxResid + W_ORI * oriPenalty + W_CONN * connMean;
                                        accepted = true;
                                    }
                                }
                            }

                            // ===== 通道 B：功能一致（尺寸可变） =====
                            if (!accepted)
                            {
                                XYZ Tfunc; double anchMeanFt;
                                if (FunctionalCompatible(eOld, eNew, connTolFt, out Tfunc, out anchMeanFt))
                                {
                                    T = Tfunc;
                                    ctrResid = (Center(eOld) + T).DistanceTo(Center(eNew));

                                    if (TryGetDir(eOld, out XYZ uo2) && TryGetDir(eNew, out XYZ un2))
                                    {
                                        double dot = Math.Abs(DotSafe(uo2, un2));
                                        if (dot < ORI_DOT_MIN) goto AFTER_CHANNELS;
                                        oriPenalty = 1.0 - dot;
                                    }

                                    connMean = 0.0; // 锚点已校验，此处不再加重连接器

                                    costVal = W_CENTER * ctrResid + W_FUNC * anchMeanFt + W_ORI * oriPenalty;
                                    accepted = true;
                                    isFunc = true;
                                }
                            }

                        AFTER_CHANNELS:
                            if (!accepted) continue;

                            cost[j, i] = costVal;
                            cand[j, i] = new CandidateInfo
                            {
                                valid = true,
                                isFunc = isFunc,
                                T = T,
                                centerResid = ctrResid,
                                maxResid = maxResid,
                                oriPenalty = oriPenalty,
                                connMean = connMean
                            };
                        }
                    }

                    // —— 匈牙利求最小总代价一对一匹配 ——
                    var hung = new Hungarian(cost);
                    int[] assign = hung.Solve(); // 行=new(j) → 列=old(i)；>=C 为 dummy

                    // —— 结果把关 + 写入 —— 
                    for (int j = 0; j < R; j++)
                    {
                        int i = assign[j];
                        if (i < 0 || i >= C)
                        {
                            rows.Add($"translated-global,,,,{news[j].Id.IntegerValue},{Fmt(Center(news[j]) * FT2MM)},,,," +
                                     $",unmatched");
                            continue;
                        }
                        if (!cand[j, i].valid || !IsFiniteDouble(cost[j, i]))
                        {
                            rows.Add($"translated-global,,,,{news[j].Id.IntegerValue},{Fmt(Center(news[j]) * FT2MM)},,,," +
                                     $",no-viable-pair");
                            continue;
                        }

                        Element eOld = olds[i], eNew = news[j];
                        var info = cand[j, i];
                        double tolJ = Math.Max(tolOld[i], tolNew[j]);
                        double margin = AMBIGUITY_MARGIN_RATIO * tolJ;

                        // 兜底阈值（单项残差不要超）
                        if (info.centerResid > tolJ || (!info.isFunc && info.maxResid > tolJ))
                        {
                            rows.Add($"translated-global,,,,{eNew.Id.IntegerValue},{Fmt(Center(eNew) * FT2MM)}," +
                                     $"{(info.T.X * FT2MM):F1},{(info.T.Y * FT2MM):F1},{(info.T.Z * FT2MM):F1}," +
                                     $"{cost[j, i]:F6},threshold_exceed");
                            continue;
                        }

                        // 歧义：与次优差距不足
                        double best = cost[j, i];
                        double second = SecondBest(cost, j, C);
                        if (IsFiniteDouble(second) && (second - best) < margin)
                        {
                            rows.Add($"translated-global,,,,{eNew.Id.IntegerValue},{Fmt(Center(eNew) * FT2MM)}," +
                                     $"{(info.T.X * FT2MM):F1},{(info.T.Y * FT2MM):F1},{(info.T.Z * FT2MM):F1}," +
                                     $"{best:F6},ambiguous");
                            continue;
                        }

                        // 同步 getmark
                        string oMark = GetMark(eOld), nMark = GetMark(eNew);
                        if (!string.IsNullOrWhiteSpace(oMark) && oMark != nMark)
                        {
                            Parameter p = eNew.LookupParameter("getmark") ??
                                          eNew.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                            if (p != null && !p.IsReadOnly)
                            {
                                p.Set(oMark);
                                view?.SetElementOverrides(eNew.Id, ogs);
                                changed++;

                                string noteOk = info.isFunc ? "ok(func)" : "ok";
                                rows.Add($"translated-global,{oMark},{nMark},{eOld.Id.IntegerValue},{eNew.Id.IntegerValue}," +
                                         $"{Fmt(Center(eNew) * FT2MM)},{(info.T.X * FT2MM):F1},{(info.T.Y * FT2MM):F1},{(info.T.Z * FT2MM):F1}," +
                                         $"{best:F6},{noteOk}");
                            }
                            else
                            {
                                rows.Add($"translated-global,{oMark},{nMark},{eOld.Id.IntegerValue},{eNew.Id.IntegerValue}," +
                                         $"{Fmt(Center(eNew) * FT2MM)},{(info.T.X * FT2MM):F1},{(info.T.Y * FT2MM):F1},{(info.T.Z * FT2MM):F1}," +
                                         $"{best:F6},param_readonly_or_missing");
                            }
                        }
                        else
                        {
                            string noteNc = info.isFunc ? "no-change(func)" : "no-change";
                            rows.Add($"translated-global,{oMark},{nMark},{eOld.Id.IntegerValue},{eNew.Id.IntegerValue}," +
                                     $"{Fmt(Center(eNew) * FT2MM)},{(info.T.X * FT2MM):F1},{(info.T.Y * FT2MM):F1},{(info.T.Z * FT2MM):F1}," +
                                     $"{best:F6},{noteNc}");
                        }
                    }
                }

                tx.Commit();
            }

            // —— 写 CSV —— 
            Directory.CreateDirectory(Path.GetDirectoryName(CSV_PATH));
            File.WriteAllLines(CSV_PATH, rows, new UTF8Encoding(true));

            TaskDialog.Show("Mark Sync", $"已同步 {changed} 个元素的 getmark（全局匹配）。\nCSV: {CSV_PATH}");
            return Result.Succeeded;
        }

        /* ===================== 工具函数区（包含“功能锚点辅助”） ===================== */

        static string PickRvt(string title)
        {
            using (var dlg = new WF.OpenFileDialog())
            {
                dlg.Title = title;
                dlg.Filter = "Revit 文件 (*.rvt)|*.rvt";
                return dlg.ShowDialog() == WF.DialogResult.OK ? dlg.FileName : null;
            }
        }

        static Document GetOrOpen(UIApplication uiapp, string path)
        {
            foreach (Document d in uiapp.Application.Documents)
                if (d.PathName.Equals(path, StringComparison.OrdinalIgnoreCase)) return d;
            return uiapp.OpenAndActivateDocument(path).Document;
        }

        static IList<Element> Collect(Document doc)
        {
            var list = new List<Element>();
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements())
            {
                if (e.get_BoundingBox(null) == null) continue;
                if (e is MEPCurve) { list.Add(e); continue; }
                var fi = e as FamilyInstance;
                if (fi != null && fi.MEPModel != null) { list.Add(e); continue; }
            }
            return list;
        }

        static Dictionary<string, List<Element>> GroupBySizeKey(IList<Element> elems)
        {
            var dic = new Dictionary<string, List<Element>>();
            foreach (var e in elems)
            {
                string k = SizeKey(e);
                if (!dic.TryGetValue(k, out var list)) dic[k] = list = new List<Element>();
                list.Add(e);
            }
            return dic;
        }

        static string SizeKey(Element e)
        {
            XYZ s = Size(e);
            return $"{s.X:0.###}_{s.Y:0.###}_{s.Z:0.###}";
        }

        static void GetBBox(Element e, out XYZ min, out XYZ max)
        {
            var bb = e.get_BoundingBox(null);
            if (bb == null) { min = XYZ.Zero; max = XYZ.Zero; }
            else { min = bb.Min; max = bb.Max; }
        }
        static XYZ Center(Element e) { XYZ a, b; GetBBox(e, out a, out b); return (a + b) / 2.0; }
        static XYZ Size(Element e) { XYZ a, b; GetBBox(e, out a, out b); return (b - a); }

        static double GetTol(Element e)
        {
            XYZ a, b; GetBBox(e, out a, out b);
            double diagFt = (b - a).GetLength();
            double mm = Math.Max(MIN_TOL_MM, (diagFt * FT2MM) * TOL_RATIO);
            return mm / FT2MM;
        }

        static bool GeometryEqual(Element a, Element b, double tol)
        {
            XYZ asz = Size(a), bsz = Size(b);
            return Math.Abs(asz.X - bsz.X) <= tol &&
                   Math.Abs(asz.Y - bsz.Y) <= tol &&
                   Math.Abs(asz.Z - bsz.Z) <= tol;
        }

        static bool TryGetDir(Element e, out XYZ dir)
        {
            dir = XYZ.Zero;
            var mc = e as MEPCurve;
            if (mc != null)
            {
                var lc = (e.Location as LocationCurve)?.Curve;
                if (lc == null) return false;
                XYZ v = lc.GetEndPoint(1) - lc.GetEndPoint(0);
                if (v.GetLength() < 1e-9) return false;
                dir = v.Normalize();
                return true;
            }
            return false; // FamilyInstance 的方向暂不强制
        }

        static double DotSafe(XYZ a, XYZ b)
        {
            double d = a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            double la = a.GetLength(), lb = b.GetLength();
            if (la <= 1e-9 || lb <= 1e-9) return 0.0;
            return d / (la * lb);
        }

        static List<Connector> GetConnectors(Element e)
        {
            var list = new List<Connector>();
            var mc = e as MEPCurve;
            if (mc != null)
            {
                ConnectorSet set = mc.ConnectorManager.Connectors;
                foreach (Connector c in set) list.Add(c);
                return list;
            }
            var fi = e as FamilyInstance;
            if (fi != null && fi.MEPModel != null && fi.MEPModel.ConnectorManager != null)
            {
                ConnectorSet set = fi.MEPModel.ConnectorManager.Connectors;
                foreach (Connector c in set) list.Add(c);
            }
            return list;
        }

        // ====== 第③处：功能锚点辅助（放在工具函数区，不在 Execute 里） ======

        static bool IsTerminalFi(FamilyInstance fi)
        {
            if (fi?.Category == null) return false;
            var bic = (BuiltInCategory)fi.Category.Id.IntegerValue;
            return bic == BuiltInCategory.OST_PlumbingFixtures
                || bic == BuiltInCategory.OST_MechanicalEquipment
                || bic == BuiltInCategory.OST_SpecialityEquipment;
        }

        static XYZ QuantPosFt(XYZ pFt, double stepMm)
        {
            double sFt = stepMm / FT2MM;
            double q(double v) => Math.Round(v / sFt) * sFt;
            return new XYZ(q(pFt.X), q(pFt.Y), q(pFt.Z));
        }

        static string AnchorKey(FamilyInstance fi, XYZ posFtQuant)
        {
            string cat = fi.Category?.Name ?? "NA";
            string fam = fi.Symbol?.FamilyName ?? "NA";
            string typ = fi.Name ?? "NA";
            return $"{cat}|{fam}|{typ}|{posFtQuant.X:0.###},{posFtQuant.Y:0.###},{posFtQuant.Z:0.###}";
        }

        static List<Anchor> FindFunctionalAnchors(Element e, int maxHops = 6)
        {
            var result = new List<Anchor>();
            var starts = GetConnectors(e);
            if (starts.Count == 0) return result;

            foreach (var sc in starts)
            {
                var q = new Queue<Tuple<Connector, int>>();
                var visited = new HashSet<int>();
                q.Enqueue(Tuple.Create(sc, 0));

                bool found = false;
                Anchor foundAnchor = new Anchor();

                while (q.Count > 0)
                {
                    var item = q.Dequeue();
                    var conn = item.Item1;
                    int depth = item.Item2;
                    if (depth > maxHops) break;

                    ConnectorSet refs = conn.AllRefs;
                    foreach (Connector rc in refs)
                    {
                        var owner = rc.Owner as Element;
                        if (owner == null) continue;
                        if (!visited.Add(owner.Id.IntegerValue)) continue;

                        var fi = owner as FamilyInstance;
                        if (fi != null && IsTerminalFi(fi))
                        {
                            XYZ posQ = QuantPosFt(rc.Origin, ANCH_BIN_MM);
                            foundAnchor = new Anchor { key = AnchorKey(fi, posQ), pos = rc.Origin };
                            found = true;
                            break;
                        }

                        var nexts = GetConnectors(owner);
                        foreach (var nx in nexts)
                        {
                            if ((nx.Origin - conn.Origin).GetLength() < 1e-6) continue;
                            q.Enqueue(Tuple.Create(nx, depth + 1));
                        }
                    }
                    if (found) break;
                }
                if (found) result.Add(foundAnchor);
            }

            // 去重与限量
            result = result.GroupBy(a => a.key).Select(g => g.First()).Take(2).ToList();
            return result;
        }

        static bool FunctionalCompatible(Element oldE, Element newE, double connTolFt,
                                         out XYZ T, out double anchorMeanFt)
        {
            T = XYZ.Zero; anchorMeanFt = double.PositiveInfinity;
            var ao = FindFunctionalAnchors(oldE);
            var an = FindFunctionalAnchors(newE);
            if (ao.Count == 0 || an.Count == 0 || ao.Count != an.Count) return false;

            var map = new Dictionary<string, XYZ>();
            foreach (var a in ao) map[a.key] = a.pos;

            var pairs = new List<Tuple<XYZ, XYZ>>();
            foreach (var b in an)
            {
                if (!map.TryGetValue(b.key, out var pOld)) return false;
                pairs.Add(Tuple.Create(pOld, b.pos));
            }

            // 平移向量 = 各锚点差的平均
            XYZ sum = XYZ.Zero;
            foreach (var pr in pairs) sum += (pr.Item2 - pr.Item1);
            T = sum / pairs.Count;

            double tolAnchFt = FUNC_ANCH_TOL_MM / FT2MM;
            double acc = 0.0, mx = 0.0;
            foreach (var pr in pairs)
            {
                double d = (pr.Item1 + T).DistanceTo(pr.Item2);
                acc += d; if (d > mx) mx = d;
            }
            anchorMeanFt = acc / pairs.Count;

            return (anchorMeanFt <= tolAnchFt) && (mx <= Math.Max(tolAnchFt, connTolFt));
        }

        // ====== 其他通用工具 ======

        static string GetMark(Element e)
        {
            string m = e.LookupParameter("getmark")?.AsString();
            if (!string.IsNullOrWhiteSpace(m)) return m.Trim();
            m = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
            return string.IsNullOrWhiteSpace(m) ? "" : m.Trim();
        }

        static string Fmt(XYZ v) => $"{v.X:F1};{v.Y:F1};{v.Z:F1}";

        static ElementId GetSolidFillId(Document doc)
        {
            foreach (FillPatternElement f in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)))
                if (f.GetFillPattern().IsSolidFill) return f.Id;
            return ElementId.InvalidElementId;
        }

        static double SecondBest(double[,] cost, int row, int validCols)
        {
            double best = double.PositiveInfinity, second = double.PositiveInfinity;
            for (int i = 0; i < validCols; i++)
            {
                double c = cost[row, i];
                if (!IsFiniteDouble(c)) continue;
                if (c < best) { second = best; best = c; }
                else if (c < second) second = c;
            }
            return second;
        }

        static bool IsFiniteDouble(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    }

    /* ===== 匈牙利算法（最小化），支持方阵补齐 ===== */
    internal class Hungarian
    {
        private readonly double[,] _cost;
        private readonly int _n;

        // 新增：本类内部使用的 IsFinite
        private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        public Hungarian(double[,] costSquare)
        {
            _cost = (double[,])costSquare.Clone();
            _n = _cost.GetLength(0);

            // 把 +∞ 替换成大数，避免数值溢出
            double big = 1.0;
            for (int i = 0; i < _n; i++)
                for (int j = 0; j < _n; j++)
                    if (IsFinite(_cost[i, j]) && _cost[i, j] > big) big = _cost[i, j];   // ← 这里用 IsFinite

            big *= 1e9;

            for (int i = 0; i < _n; i++)
                for (int j = 0; j < _n; j++)
                    if (!IsFinite(_cost[i, j])) _cost[i, j] = big;                        // ← 这里也用 IsFinite
        }

        // 返回：row -> col（长度 N；若 col 超出有效列数则视为 dummy）
        public int[] Solve()
        {
            int n = _n;
            double[] u = new double[n + 1];
            double[] v = new double[n + 1];
            int[] p = new int[n + 1];
            int[] way = new int[n + 1];

            for (int i = 1; i <= n; i++)
            {
                p[0] = i;
                int j0 = 0;
                double[] minv = new double[n + 1];
                bool[] used = new bool[n + 1];
                for (int j = 0; j <= n; j++) { minv[j] = double.PositiveInfinity; used[j] = false; }

                do
                {
                    used[j0] = true;
                    int i0 = p[j0], j1 = 0;
                    double delta = double.PositiveInfinity;
                    for (int j = 1; j <= n; j++)
                    {
                        if (used[j]) continue;
                        double cur = _cost[i0 - 1, j - 1] - u[i0] - v[j];
                        if (cur < minv[j]) { minv[j] = cur; way[j] = j0; }
                        if (minv[j] < delta) { delta = minv[j]; j1 = j; }
                    }
                    for (int j = 0; j <= n; j++)
                    {
                        if (used[j]) { u[p[j]] += delta; v[j] -= delta; }
                        else { minv[j] -= delta; }
                    }
                    j0 = j1;
                } while (p[j0] != 0);

                // 反向增广
                do
                {
                    int j1 = way[j0];
                    p[j0] = p[j1];
                    j0 = j1;
                } while (j0 != 0);
            }

            int[] ans = new int[n];
            for (int j = 1; j <= n; j++) if (p[j] != 0) ans[p[j] - 1] = j - 1;
            return ans;
        }
    }
}









